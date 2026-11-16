using Npgsql;

sealed partial class UserDataService
{
    public async Task<string> GetLiveIdentityAsync(HttpContext context)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? throw new BadHttpRequestException(AccountErrors.Unauthorized, 401);
        return await ReadPublicIdAsync(userId);
    }

    public async Task<object?[]> StartMultiLiveAsync(HttpContext context, object?[] payload, MultiLiveRealtimeService realtime)
    {
        // 客户端用 0 表示「不指定队伍」。队伍 id 是用户作用域的全局 id（如 1004001），
        // 所以不能直接当 1 处理，要回落到该玩家当前选中的队伍。
        var chartId = GetLong(payload, 0) ?? 0;
        var liveId = GetLong(payload, 1) ?? 0;
        var consume = payload[2] is bool consumeValue && consumeValue;
        var ratio = GetLong(payload, 3) ?? -1;
        var partyId = GetLong(payload, 4);
        if (partyId is 0) partyId = null;
        if (payload.Length != 5 || chartId <= 0 || liveId <= 0 || ratio is < 0 or > 10
            || (partyId is not null && partyId <= 0))
            throw new BadHttpRequestException(LiveProgressionErrors.InvalidStart);
        var identity = await GetLiveIdentityAsync(context);
        var setting = realtime.ValidateLiveStart(identity, liveId, chartId);
        if (partyId is null)
        {
            var userId = (await GetCurrentUserIdAsync(context))!.Value;
            await EnsureDefaultUserDataAsync(userId);
            await using var connection = await database.OpenConnectionAsync();
            await using var selected = new NpgsqlCommand("select selected_party_id from user_player_preferences where \"userId\"=$1", connection);
            selected.Parameters.AddWithValue(userId);
            partyId = await selected.ExecuteScalarAsync() is long current ? current
                : throw new BadHttpRequestException(PartyNotFound, 404);
        }
        // 房间 ID 加入请求摘要，相同歌曲的下一局不能重放上一局未结束的开演快照。
        var start = new object?[] { partyId, chartId, null, null, consume, ratio, setting, false };
        // 不要在这里强制跳到 PlayingGame：
        // 官方顺序是 OnGoGame → (各端发 BeforeGameCalculate) → OnBeforeGameCalculate
        //             → (各端发 StartGame) → OnPlayGame。
        // 提前广播 OnPlayGame 会让客户端卡在帷幕等 OnBeforeGameCalculate。
        return (await RegisterLiveStartAsync(context, start, buildUnit: true, mode: LiveRewardRules.Mode.Multi,
            modeContext: [liveId]))!;
    }
}
