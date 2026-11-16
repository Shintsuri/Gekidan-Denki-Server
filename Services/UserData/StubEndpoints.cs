using Npgsql;

sealed partial class UserDataService
{
    // ========== 饰品升级 ==========
    // 客户端请求 /Accessories/{id}/LevelUp/{levelTo}，之前没有这个路由会直接 404
    // （游戏里就表现为强化失败并弹错误码）。私服不扣强化材料，直接提到目标等级。
    public async Task<object?[]> LevelUpAccessoryAsync(HttpContext context, long accessoryId, int levelTo)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        if (levelTo < 1 || levelTo > 10) throw new BadHttpRequestException("ACCESSORY_INVALID_LEVEL", 400);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "update user_accessories set level = greatest(level, $3) where \"userId\"=$1 and id=$2 returning level", connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(accessoryId);
            command.Parameters.AddWithValue(levelTo);
            if (await command.ExecuteScalarAsync() is not int) throw new BadHttpRequestException("ACCESSORY_NOT_OWNED", 404);
        }
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadAccessoriesAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == accessoryId) present.Add(DataObject(26, row));
        return present.ToArray();
    }

    // ========== 饰品锁定 ==========
    public async Task<object?[]> SwitchAccessoryLockAsync(HttpContext context, long accessoryId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "update user_accessories set locked = not locked where \"userId\"=$1 and id=$2 returning locked", connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(accessoryId);
            if (await command.ExecuteScalarAsync() is not bool) throw new BadHttpRequestException("ACCESSORY_NOT_OWNED", 404);
        }
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadAccessoriesAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == accessoryId) present.Add(DataObject(26, row));
        return present.ToArray();
    }

    // ========== 饰品收藏 ==========
    public async Task<object?[]> SetAccessoryFavoriteAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var (id, value) = ReadIdAndBool(payload);
        if (id <= 0) throw new BadHttpRequestException("ACCESSORY_INVALID_REQUEST", 400);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_accessories set is_favorite=$3 where \"userId\"=$1 and id=$2", userId, id, value);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadAccessoriesAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == id) present.Add(DataObject(26, row));
        return present.ToArray();
    }

    // ========== 照片收藏/锁定 ==========
    public async Task<object?[]> SwitchPhotoLockAsync(HttpContext context, long photoId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_photo_records set is_favorite = not is_favorite, updated_at=now() where \"userId\"=$1 and id=$2",
            userId, photoId);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadPhotoRecordsAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == photoId) present.Add(DataObject(123, row));
        return present.ToArray();
    }

    // ========== 照片售卖 ==========
    public async Task<object?[]> SellPhotosAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var ids = ReadIds(payload);
        if (ids.Count == 0) throw new BadHttpRequestException("PHOTO_INVALID_REQUEST", 400);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sold = 0;
        await using (var command = new NpgsqlCommand(
            "delete from user_photo_records where \"userId\"=$1 and id = any($2)", connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(ids.ToArray());
            sold = await command.ExecuteNonQueryAsync();
        }
        if (sold == 0) throw new BadHttpRequestException("PHOTO_NOT_FOUND", 404);
        await AddCurrencyIncrementAsync(connection, transaction, userId, "coin", sold * 500);
        await transaction.CommitAsync();
        return new object?[]
        {
            DataObject(0, await ReadUserAsync(connection, userId)),
            DataObject(128, await ReadCurrencyAsync(connection, userId))
        };
    }

    // ========== 退社 / 解散 ==========
    public async Task<object?[]> ResignCircleAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "delete from user_circle_memberships where \"userId\"=$1", userId);
        await transaction.CommitAsync();
        return Array.Empty<object?>();
    }

    // ========== 队伍改名 ==========
    public async Task<object?[]> ChangePartyNameAsync(HttpContext context, long partyId, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var name = ReadFirstString(payload);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32) throw new BadHttpRequestException("PARTY_INVALID_NAME", 400);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_party_groups set name=$3, updated_at=now() where \"userId\"=$1 and id=$2", userId, partyId, name);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadPartiesAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == partyId) present.Add(DataObject(6, row));
        return present.ToArray();
    }

    // ========== 三人队伍编辑 ==========
    public async Task<object?[]> EditTripleCastPartyAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        var present = new List<object?>();
        foreach (var row in await ReadPartiesAsync(connection, userId)) present.Add(DataObject(6, row));
        foreach (var row in await ReadPartySlotsAsync(connection, userId)) present.Add(DataObject(7, row));
        return present.ToArray();
    }

    // ========== 玩家等级上限解放 ==========
    public async Task<object?[]> ReleasePlayerRankCapAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_game_states set rank_limit = rank_limit + 10, updated_at=now() where \"userId\"=$1", userId);
        await transaction.CommitAsync();
        return new object?[] { DataObject(0, await ReadUserAsync(connection, userId)) };
    }

    // ========== 剧情一键全读 ==========
    public async Task<object?[]> ReadAllEpisodesAsync(HttpContext context, long episodeId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "insert into user_episode_read_states(\"userId\", episode_master_id, is_new, read_at) values($1,$2,false,now()) on conflict do nothing",
            userId, episodeId);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadEpisodeReadStatesAsync(connection, userId)) present.Add(DataObject(95, row));
        return present.ToArray();
    }

    // ========== 多人房间搜索 ==========
    public async Task<object?[]> SearchMultiRoomsAsync(HttpContext context, object? payload)
    {
        // 无公开房间时返回空列表，客户端显示「没有找到房间」
        return Array.Empty<object?>();
    }

    // ========== 轮盘抽奖 ==========
    public async Task<object?[]> RollRouletteAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // 给一点金币作为奖励，保证看得见结果
        await AddCurrencyIncrementAsync(connection, transaction, userId, "coin", 1000);
        await transaction.CommitAsync();
        return new object?[]
        {
            DataObject(0, await ReadUserAsync(connection, userId)),
            DataObject(128, await ReadCurrencyAsync(connection, userId))
        };
    }

    // ---------- 工具 ----------
    static (long Id, bool Value) ReadIdAndBool(object? payload)
    {
        long id = 0; bool value = false;
        void Walk(object? node)
        {
            switch (node)
            {
                case bool b: value = b; break;
                case long l when l > 0: if (id == 0) id = l; break;
                case int i when i > 0: if (id == 0) id = i; break;
                case long[] arr: if (arr.Length > 0 && id == 0) id = arr[0]; break;
                case int[] arr: if (arr.Length > 0 && id == 0) id = arr[0]; break;
                case object?[] arr: foreach (var x in arr) Walk(x); break;
            }
        }
        Walk(payload);
        return (id, value);
    }

    static List<long> ReadIds(object? payload)
    {
        var ids = new List<long>();
        void Walk(object? node)
        {
            switch (node)
            {
                case long l: if (l > 0) ids.Add(l); break;
                case int i: if (i > 0) ids.Add(i); break;
                case long[] arr: ids.AddRange(arr.Where(x => x > 0)); break;
                case int[] arr: ids.AddRange(arr.Where(x => x > 0).Select(x => (long)x)); break;
                case object?[] arr: foreach (var x in arr) Walk(x); break;
            }
        }
        Walk(payload);
        return ids.Distinct().Take(2000).ToList();
    }

    static string? ReadFirstString(object? payload)
    {
        string? found = null;
        void Walk(object? node)
        {
            switch (node)
            {
                case string s: if (found is null) found = s; break;
                case object?[] arr: foreach (var x in arr) Walk(x); break;
            }
        }
        Walk(payload);
        return found;
    }
}
