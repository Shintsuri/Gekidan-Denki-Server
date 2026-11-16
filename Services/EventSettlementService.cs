using System.Text.Json;
using Npgsql;

/// <summary>
/// 活动结算后台服务。
///
/// 官方节奏：活动期间累计 PT 奖励随打歌/稽古实时发放；结算期结束（StoryEventMaster.ForceEndDate）
/// 时按最终排名发放排名奖励。这里每 30 秒检查一次，到点结算一次并落库。
///
/// 时间口径：主数据里的 DateTime 是游戏内本地时间（JST 墙上时间），客户端原样显示，
/// 所以结算时间 = ForceEndDate 当作 JST 解释。可用 event_settlement.json 覆盖：
///   { "overrides": { "20005": "2026-11-18T08:00:00" } }
/// </summary>
sealed class EventSettlementService : BackgroundService
{
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SettleLead = TimeSpan.FromMinutes(1); // “在结束时间之前发”
    static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);  // 只处理最近结束的活动，避免翻旧账

    readonly LocalServerState state;
    readonly HashSet<long> skipped = new();

    public EventSettlementService(LocalServerState state)
    {
        this.state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 让 Web 服务器先起来，避免启动阶段和主数据读取抢 IO
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await using var connection = await state.Database.OpenConnectionAsync(stoppingToken);
            await UserDataService.EnsureEventRewardTablesAsync(connection);
            await UserDataService.EnsureStarRankRewardTableAsync(connection);
            await LogAsync("[event-settlement] service started");
        }
        catch (Exception error)
        {
            await LogAsync($"[event-settlement] init failed: {error.Message}");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                await LogAsync($"[event-settlement] tick failed: {error.Message}");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task TickAsync(CancellationToken cancellationToken)
    {
        var databasePath = await ReadActiveMasterDatabasePathAsync(cancellationToken);
        if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath)) return;

        List<(long Id, DateTime? Start, DateTime? End, DateTime? ForceEnd)> windows;
        try
        {
            windows = MasterDataTableReader.ReadStoryEventWindows(databasePath);
        }
        catch (Exception error)
        {
            await LogAsync($"[event-settlement] read master failed: {error.Message}");
            return;
        }
        if (windows.Count == 0) return;

        var overrides = ReadOverrides();
        // 主数据时间 = JST 墙上时间；用 JST 的“现在”比较
        var nowJst = DateTime.UtcNow.AddHours(9);
        var recentFloor = nowJst - RecentWindow;

        foreach (var window in windows)
        {
            if (skipped.Contains(window.Id)) continue;

            DateTime settleLocal;
            if (overrides.TryGetValue(window.Id, out var overridden)) settleLocal = overridden;
            else if (window.ForceEnd is { } forceEnd) settleLocal = forceEnd;
            else continue;

            if (settleLocal < recentFloor)
            {
                // 历史活动不结算；这里不做永久跳过缓存，否则后来加 override 会被旧缓存挡住
                continue;
            }

            // 结算提前 1 分钟执行，保证 08:00 整客户端切到结算结果时奖励已经到账
            var settleAtJst = settleLocal - SettleLead;
            if (nowJst < settleAtJst) continue;

            await using var connection = await state.Database.OpenConnectionAsync(cancellationToken);
            if (!await HasParticipantsAsync(connection, window.Id, cancellationToken))
            {
                skipped.Add(window.Id);
                continue;
            }

            // 主数据是 JST 墙上时间 → 结算时刻按 JST 解释
            var settleUtc = DateTime.SpecifyKind(settleLocal, DateTimeKind.Unspecified).AddHours(-9);
            var result = await UserDataService.SettleStoryEventRankingAsync(connection, window.Id, settleUtc);
            if (result.Settled)
            {
                skipped.Add(window.Id);
                await LogAsync($"[event-settlement] event={window.Id} {result.Detail} settleAtJst={settleLocal:yyyy-MM-dd HH:mm}");
            }
        }

        // 演劇リーグ：期间结束后按积分排名发奖励（每个期间只结算一次）
        foreach (var period in UserDataService.AllLeaguePeriods())
        {
            if (nowJst < period.DisplayEnd.AddMinutes(1)) continue;
            if (period.DisplayEnd < nowJst.AddDays(-30)) continue;

            await using var connection = await state.Database.OpenConnectionAsync(cancellationToken);
            if (!await HasLeagueParticipantsAsync(connection, period.Id, cancellationToken)) continue;
            var (settled, ranked, rewarded) = await UserDataService.SettleLeagueAsync(connection, period.Id, period.ClassType);
            if (settled)
                await LogAsync($"[league] league={period.Id} class={period.ClassType} ranked={ranked} rewarded={rewarded}");
        }

        // 三連劇：期间结束后按积分排名发奖励
        foreach (var period in UserDataService.AllTripleCastPeriods())
        {
            if (nowJst < period.DisplayEnd.AddMinutes(1)) continue;
            if (period.DisplayEnd < nowJst.AddDays(-30)) continue;

            await using var connection = await state.Database.OpenConnectionAsync(cancellationToken);
            if (!await HasTripleCastParticipantsAsync(connection, period.Id, cancellationToken)) continue;
            var (settled, ranked, rewarded) = await UserDataService.SettleTripleCastAsync(connection, period.Id);
            if (settled)
                await LogAsync($"[triple-cast] id={period.Id} ranked={ranked} rewarded={rewarded}");
        }
    }

    static async Task<bool> HasTripleCastParticipantsAsync(NpgsqlConnection connection, long tripleCastId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists (
              select 1 from user_triple_cast_states
              where triple_cast_master_id = $1 and point > 0
            )
            """, connection);
        command.Parameters.AddWithValue(tripleCastId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    static async Task<bool> HasLeagueParticipantsAsync(NpgsqlConnection connection, long leagueId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists (
              select 1 from user_league_states
              where league_master_id = $1 and point > 0
            )
            """, connection);
        command.Parameters.AddWithValue(leagueId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    async Task LogAsync(string message)
    {
        try
        {
            await state.Logger.LogAsync(message);
        }
        catch
        {
            // 日志失败不影响结算
        }
    }

    static async Task<bool> HasParticipantsAsync(NpgsqlConnection connection, long eventId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists (
              select 1 from user_story_event_states
              where event_master_id = $1 and point > 0
            )
            """, connection);
        command.Parameters.AddWithValue(eventId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    async Task<string?> ReadActiveMasterDatabasePathAsync(CancellationToken cancellationToken)
    {
        await using var connection = await state.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select local_file_path
            from system_master_data_versions
            where is_active
            order by updated_at desc
            limit 1
            """, connection);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    static Dictionary<long, DateTime> ReadOverrides()
    {
        var map = new Dictionary<long, DateTime>();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "event_settlement.json");
            if (!File.Exists(path)) return map;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("overrides", out var overrides)) return map;
            foreach (var property in overrides.EnumerateObject())
            {
                if (!long.TryParse(property.Name, out var eventId)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                if (DateTime.TryParse(property.Value.GetString(), out var value))
                    map[eventId] = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
                else
                    Console.WriteLine($"[event-settlement] bad override for event={eventId}");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"[event-settlement] override file invalid: {error.Message}");
        }
        return map;
    }
}

