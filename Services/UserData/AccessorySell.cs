using Npgsql;

sealed partial class UserDataService
{
    // 饰品售卖：/Accessories/Sell
    // 请求里是待售饰品的实例 id 列表（可能是 [id,...] 或 [[id,count],...]）。
    public async Task<object?[]> SellAccessoriesAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var ids = new List<long>();
        void Collect(object? node)
        {
            switch (node)
            {
                case long value: if (value > 0) ids.Add(value); break;
                case int value: if (value > 0) ids.Add(value); break;
                case long[] values: ids.AddRange(values.Where(value => value > 0)); break;
                case int[] values: ids.AddRange(values.Where(value => value > 0).Select(value => (long)value)); break;
                case object?[] values:
                    foreach (var item in values) Collect(item);
                    break;
            }
        }
        Collect(payload);
        ids = ids.Distinct().Take(2000).ToList();
        if (ids.Count == 0)
            throw new BadHttpRequestException("ACCESSORY_INVALID_REQUEST", 400);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId);

        var sold = 0;
        await using (var delete = new NpgsqlCommand(
            "delete from user_accessories where \"userId\"=$1 and id = any($2)", connection, transaction))
        {
            delete.Parameters.AddWithValue(userId);
            delete.Parameters.AddWithValue(ids.ToArray());
            sold = await delete.ExecuteNonQueryAsync();
        }
        if (sold == 0)
            throw new BadHttpRequestException("ACCESSORY_NOT_OWNED", 404);

        // 分解奖励：每个饰品给金币与素材（下等素材用 130011）
        var coinGain = sold * 1000;
        var materialGain = sold * 5;
        await AddCurrencyIncrementAsync(connection, transaction, userId, "coin", coinGain);
        await UpsertItemQuantityAsync(connection, transaction, userId, 130011, materialGain);

        var present = new List<object?>
        {
            DataObject(0, await ReadUserAsync(connection, userId)),
            DataObject(128, await ReadCurrencyAsync(connection, userId))
        };
        foreach (var row in await ReadItemPossessionsAsync(connection, userId))
            if (Convert.ToInt64(row[1]) == 130011) present.Add(DataObject(27, row));
        present.AddRange((await ReadAccessoriesAsync(connection, userId)).Select(row => DataObject(26, row)));

        await transaction.CommitAsync();
        return present.ToArray();
    }

    /// <summary>保存饰品自动售卖设置（rarityFlag 位图），存进 user_player_settings.payload。</summary>
    public async Task<object?[]> SetAccessoryAutoSellAsync(HttpContext context, string? rarityFlag)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return Array.Empty<object?>();
        int flag = 0;
        if (!string.IsNullOrWhiteSpace(rarityFlag)) int.TryParse(rarityFlag, out flag);
        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            update user_player_settings
            set payload = payload || jsonb_build_object('accessoryAutoSellRarityFlag', $2::int),
                updated_at = now()
            where "userId" = $1
            """, connection);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(flag);
        await command.ExecuteNonQueryAsync();
        return Array.Empty<object?>();
    }
}
