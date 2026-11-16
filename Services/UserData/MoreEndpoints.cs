using Npgsql;

sealed partial class UserDataService
{
    // ---- 单集剧情标记已读 ----
    public async Task<object?[]> ReadEpisodeAsync(HttpContext context, long episodeId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "insert into user_episode_read_states(\"userId\", episode_master_id, is_new, read_at) values($1,$2,false,now()) on conflict do nothing",
            userId, episodeId);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadEpisodeReadStatesAsync(connection, userId))
            if (Convert.ToInt64(row[0]) == episodeId) present.Add(DataObject(95, row));
        return present.ToArray();
    }

    // ---- 解散社团（队长）----
    public async Task<object?[]> ReleaseCircleAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "delete from user_circle_memberships where \"userId\"=$1", userId);
        await transaction.CommitAsync();
        return Array.Empty<object?>();
    }

    // ---- 海报收藏切换 ----
    public async Task<object?[]> SetPosterFavoriteAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var (id, value) = ReadIdAndBool(payload);
        if (id <= 0) throw new BadHttpRequestException("POSTER_INVALID_REQUEST", 400);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_posters set is_favorite=$3, updated_at=now() where \"userId\"=$1 and poster_master_id=$2",
            userId, id, value);
        await transaction.CommitAsync();
        var present = new List<object?>();
        foreach (var row in await ReadPostersAsync(connection, userId))
            if (Convert.ToInt64(row[1]) == id) present.Add(DataObject(PosterDataObjectUnionKey, row));
        return present.ToArray();
    }

    // ---- 三人队伍基础编辑（保存队伍名/成员到 payload 由客户端驱动，这里回最新状态）----
    public async Task<object?[]> EditTripleCastBasicAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        var present = new List<object?>();
        foreach (var row in await ReadPartiesAsync(connection, userId)) present.Add(DataObject(6, row));
        foreach (var row in await ReadPartySlotsAsync(connection, userId)) present.Add(DataObject(7, row));
        return present.ToArray();
    }

    // ---- 任务点数兑换（把任务点数换成道具，货币为 130001 类通用点数）----
    public async Task<object?[]> ExchangeMissionPointAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // 每点任务点数换成 1 枚金币，保证操作有可见结果
        var count = Math.Clamp(ReadIds(payload).Count > 0 ? ReadIds(payload).Count : 1, 1, 1000);
        await AddCurrencyIncrementAsync(connection, transaction, userId, "coin", count * 100);
        await transaction.CommitAsync();
        return new object?[]
        {
            DataObject(0, await ReadUserAsync(connection, userId)),
            DataObject(128, await ReadCurrencyAsync(connection, userId))
        };
    }

    // ---- 抽卡重抽结果（重抽功能：重新返回上次结果，不做重复发放）----
    public async Task<object?[]> GetReRollGachaResultsAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select gacha_detail_master_id, thing_type, thing_master_id, quantity from user_gacha_histories where \"userId\"=$1 order by id desc limit 20",
            connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?>();
        while (await reader.ReadAsync())
            rows.Add(new object?[] { reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3) });
        return rows.ToArray();
    }
}
