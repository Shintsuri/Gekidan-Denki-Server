using System.Text.Json;
using Npgsql;

sealed partial class UserDataService
{
    // 活动交换所购买：/Shops/ExchangeShopThing/{id}/{quantity}
    // 商品来自嵌入的 Event.ExchangeShopMaster.json，字段布局与 ReadExchangeShopStatesAsync 一致。
    public async Task<GachaPurchase> ExchangeEventShopThingAsync(HttpContext context, long thingId, int quantity)
    {
        // 兑换数量不设上限（仅保留基本校验与溢出保护）
        if (thingId <= 0 || quantity is < 1 or > 10_000_000)
            throw new BadHttpRequestException(GachaErrors.Invalid, 400);
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);

        var item = await FindExchangeShopItemAsync(thingId);
        if (item is null) throw new BadHttpRequestException(GachaErrors.Unavailable, 404);

        var (rewardType, rewardMasterId, rewardQuantity, limit, requiredItemId, requiredQuantity) = item.Value;
        if (requiredItemId <= 0 || requiredQuantity <= 0)
            throw new BadHttpRequestException(GachaErrors.Unavailable, 404);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId);

        // 限购校验：已兑换数 + 本次数量 不能超过主数据里的 limit（0/缺省 = 不限）。
        var already = 0;
        if (limit > 0)
        {
            await using var check = new NpgsqlCommand(
                """
                select exchanged_count from user_event_exchange_shop_states
                where "userId"=$1 and exchange_shop_thing_id=$2
                for update
                """,
                connection, transaction);
            check.Parameters.AddWithValue(userId);
            check.Parameters.AddWithValue(thingId);
            var existing = await check.ExecuteScalarAsync();
            already = existing is null or DBNull ? 0 : Convert.ToInt32(existing);
            if (already + quantity > limit)
                throw new BadHttpRequestException("EXCHANGE_LIMIT_REACHED", 409);
        }

        // 扣货币：活动海报（ThingType=7）每次兑换消耗上涨 —— 第 N 次消耗 = 基础 PT × N。
        var unitCost = rewardType == 7 ? requiredQuantity * (already + 1) : requiredQuantity;
        var cost = (long)unitCost * quantity;
        await using (var pay = new NpgsqlCommand(
            "update user_item_possessions set quantity = quantity - $3, updated_at = now() where \"userId\"=$1 and item_master_id=$2 and quantity >= $3",
            connection, transaction))
        {
            pay.Parameters.AddWithValue(userId);
            pay.Parameters.AddWithValue(requiredItemId);
            pay.Parameters.AddWithValue(cost);
            if (await pay.ExecuteNonQueryAsync() != 1)
                throw new BadHttpRequestException("EXCHANGE_INSUFFICIENT_ITEMS", 409);
        }

        // 发奖
        var present = new List<object?>();
        var rewardTotal = (long)rewardQuantity * quantity;
        if (rewardType == 13)
        {
            await AddFreeJewelsAsync(connection, transaction, userId, (int)rewardTotal);
            present.Add(DataObject(0, await ReadUserAsync(connection, userId)));
        }
        else if (rewardType == 7)
        {
            // 活动海报（ThingType=7）：写 user_posters 并提升突破阶段，
            // 否则会被当成普通道具塞进 item_possessions，客户端看不到海报突破。
            var owned = (await ReadPostersAsync(connection, userId))
                .FirstOrDefault(row => Convert.ToInt64(row[1]) == rewardMasterId);
            var phase = owned is null ? (int)rewardTotal - 1 : Convert.ToInt32(owned[3]) + (int)rewardTotal;
            var poster = await UpsertPosterAsync(connection, transaction, userId, rewardMasterId, phase);
            present.Add(DataObject(PosterDataObjectUnionKey, poster));
        }
        else
        {
            await UpsertItemQuantityAsync(connection, transaction, userId, rewardMasterId, (int)rewardTotal);
            foreach (var row in await ReadItemPossessionsAsync(connection, userId))
                if (Convert.ToInt64(row[1]) == rewardMasterId) present.Add(DataObject(27, row));
        }

        // 扣货币后的道具状态
        foreach (var row in await ReadItemPossessionsAsync(connection, userId))
            if (Convert.ToInt64(row[1]) == requiredItemId &&
                present.All(p => ((object?[])p!)[0] is not 27 || Convert.ToInt64(((object?[])((object?[])((object?[])p!)[1]!))[1]) != requiredItemId))
                present.Add(DataObject(27, row));

        // 记录购买次数
        await ExecuteAsync(connection, transaction, """
            insert into user_event_exchange_shop_states(id,"userId",exchange_shop_thing_id,exchanged_count)
            values($1,$2,$3,$4)
            on conflict ("userId",exchange_shop_thing_id) do update
              set exchanged_count = user_event_exchange_shop_states.exchanged_count + $4, updated_at = now()
            """, UserScopedId(userId, 820000 + thingId), userId, thingId, quantity);

        await transaction.CommitAsync();

        // 把整份交换所状态推给客户端（DataObject 82），否则兑换后界面仍显示可兑换。
        // 之前这里用 shop[0]（店铺 id）和 thingId（商品 id）比较，永远不相等，一条都没发出去。
        foreach (var shop in await ReadExchangeShopStatesAsync(userId))
            present.Add(DataObject(82, shop));
        // 兑换后的限购状态（union 102）：客户端根据 ExchangedCount 判断是否售罄并下架商品。
        foreach (var row in await ReadExchangeLimitsAsync(connection, userId))
            present.Add(DataObject(102, row));

        // Result = 获得列表，客户端靠它弹「获得道具」提示
        var rewards = new List<object?> { GachaRules.Received(rewardType, rewardMasterId, (int)rewardTotal) };
        return new(rewards.ToArray(), present.ToArray());
    }

    async Task<(int Type, long MasterId, int Quantity, int Limit, long RequiredItemId, int RequiredQuantity)?> FindExchangeShopItemAsync(long thingId)
    {
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Event.ExchangeShopMaster.json");
        if (stream is null) return null;
        using var doc = await JsonDocument.ParseAsync(stream);
        foreach (var shop in doc.RootElement.EnumerateArray())
        {
            if (shop.ValueKind != JsonValueKind.Array || shop.GetArrayLength() < 12) continue;
            if (shop[10].ValueKind != JsonValueKind.Array) continue;
            foreach (var item in shop[10].EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 14) continue;
                if (!item[0].TryGetInt64(out var id) || id != thingId) continue;
                var type = item[2].TryGetInt32(out var t) ? t : 1;
                var masterId = item[1].TryGetInt64(out var m) ? m : 0;
                var qty = item[3].TryGetInt32(out var q) ? q : 1;
                // 限购次数在 [4]（[5] 是 thingId/order，不是限购）
                var limit = item[4].ValueKind == JsonValueKind.Number ? item[4].GetInt32() : 0;
                // 活动海报（type 7）允许兑换 5 次（4 次突破 + 1），主数据里写的是 1，这里覆盖。
                if (type == 7 && limit is > 0 and < 5) limit = 5;
                var reqId = item[12].TryGetInt64(out var r) ? r : 0;
                var reqQty = item[13].TryGetInt32(out var rq) ? rq : 0;
                return (type, masterId, qty, limit, reqId, reqQty);
            }
        }
        return null;
    }

    /// <summary>读取交换所限购状态，供客户端 ExchangeLimit(union 102) 判断售罄并下架商品。</summary>
    static async Task<List<object?[]>> ReadExchangeLimitsAsync(NpgsqlConnection connection, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, exchange_shop_thing_id, exchanged_count
            from user_event_exchange_shop_states
            where "userId" = $1
            """, connection);
        command.Parameters.AddWithValue(userId);
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new object?[] { reader.GetInt64(0), reader.GetInt64(1), 0, null, reader.GetInt32(2), null });
        return rows;
    }
}
