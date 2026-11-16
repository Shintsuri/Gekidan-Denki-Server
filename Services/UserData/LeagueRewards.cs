using System.Text.Json;
using Npgsql;

/// <summary>
/// 演劇リーグ / 三連劇 奖励：
///  - 参加奖励：每局リーグ公演结算时发放 LeaguePlayRewardPackageMaster（id = リーグ期 id / 三連劇 3000+id）
///  - 排名奖励：期间结束后按积分排名，发放 LeagueGroupRankingRewardPackageMaster（按 class + 名次段）
/// 统一走 RewardGrant 发东西，结算结果落库保证只发一次。
/// </summary>
sealed partial class UserDataService
{
    internal sealed record LeaguePeriod(long Id, DateTime DisplayStart, DateTime DisplayEnd, int ClassType);

    static bool leagueTablesReady;

    internal static async Task EnsureLeagueRewardTablesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        // 注意：建表不能只靠进程内标记。事务里的 DDL 一旦回滚，标记却已置位，
        // 后续调用会以为表存在而直接 42P01。这里每次都执行 IF NOT EXISTS（幂等、开销可忽略）。
        const string sql = """
            create table if not exists user_league_states (
              "userId" bigint not null,
              league_master_id bigint not null,
              class_type int not null default 3,
              point bigint not null default 0,
              rank int not null default 0,
              updated_at timestamptz not null default now(),
              primary key ("userId", league_master_id)
            );

            create table if not exists user_league_settlements (
              league_master_id bigint primary key,
              status text not null default 'running',
              class_type int not null default 3,
              settled_at timestamptz,
              ranked_users int not null default 0,
              rewarded_users int not null default 0
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<long> AddLeaguePointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, long leagueId, int points, int classType = 3)
    {
        await EnsureLeagueRewardTablesAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            insert into user_league_states("userId", league_master_id, class_type, point)
            values ($1, $2, $3, $4)
            on conflict ("userId", league_master_id) do update
              set point = user_league_states.point + $4, updated_at = now(), class_type = $3
            returning point
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(leagueId);
        command.Parameters.AddWithValue(classType);
        command.Parameters.AddWithValue(points);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>参加奖励（每局一发）。periodId 直接用リーグ期 id；三連劇传 3000+id。</summary>
    internal static async Task GrantLeaguePlayRewardAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, long packageId, List<object?> present)
    {
        var things = ReadLeaguePackage(packageId);
        if (things.Count == 0) return;
        foreach (var thing in things)
        {
            var entry = await GrantRewardThingAsync(connection, transaction, userId, thing.Type, thing.Id, thing.Quantity);
            if (entry is not null) present.Add(entry);
        }
    }

    /// <summary>期间结束结算：按积分排名发 LeagueGroupRankingRewardPackageMaster。</summary>
    internal static async Task<(bool Settled, int Ranked, int Rewarded)> SettleLeagueAsync(
        NpgsqlConnection connection, long leagueId, int classType)
    {
        await EnsureLeagueRewardTablesAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = false;
        await using (var claim = new NpgsqlCommand(
            """
            insert into user_league_settlements(league_master_id, status, class_type)
            values ($1, 'running', $2)
            on conflict (league_master_id) do nothing
            returning league_master_id
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue(leagueId);
            claim.Parameters.AddWithValue(classType);
            claimed = await claim.ExecuteScalarAsync() is not null;
        }
        if (!claimed)
        {
            await transaction.RollbackAsync();
            return (false, 0, 0);
        }

        var rows = new List<long>();
        await using (var read = new NpgsqlCommand(
            """
            select "userId" from user_league_states
            where league_master_id = $1 and class_type = $2 and point > 0
            order by point desc, "userId" asc
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(leagueId);
            read.Parameters.AddWithValue(classType);
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetInt64(0));
        }

        var rank = 0;
        var lastPoint = long.MinValue;
        var rewarded = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var userId = rows[i];
            long point;
            await using (var pointRead = new NpgsqlCommand(
                """
                select point from user_league_states
                where "userId" = $1 and league_master_id = $2
                """, connection, transaction))
            {
                pointRead.Parameters.AddWithValue(userId);
                pointRead.Parameters.AddWithValue(leagueId);
                point = await pointRead.ExecuteScalarAsync() is long value ? value : 0L;
            }
            if (point != lastPoint) { rank = i + 1; lastPoint = point; }

            await ExecuteAsync(connection, transaction,
                """
                update user_league_states set rank = $3, updated_at = now()
                where "userId" = $1 and league_master_id = $2
                """, userId, leagueId, rank);

            var packageId = LeagueGroupRankingPackageId(classType, rank);
            if (packageId == 0) continue;
            foreach (var thing in ReadLeaguePackage(packageId))
                await GrantRewardThingAsync(connection, transaction, userId, thing.Type, thing.Id, thing.Quantity);
            rewarded++;
        }

        await ExecuteAsync(connection, transaction,
            """
            update user_league_settlements
            set status = 'done', settled_at = now(), ranked_users = $2, rewarded_users = $3
            where league_master_id = $1
            """, leagueId, rows.Count, rewarded);
        await transaction.CommitAsync();
        Console.WriteLine($"[league] settled league={leagueId} class={classType} ranked={rows.Count} rewarded={rewarded}");
        return (true, rows.Count, rewarded);
    }

    // ---- 主数据读取 ----

    static readonly Lazy<IReadOnlyList<LeaguePeriod>> LeaguePeriods = new(LoadLeaguePeriods);
    static readonly Lazy<IReadOnlyDictionary<long, IReadOnlyList<(int Type, long Id, int Quantity)>>> LeaguePackages = new(LoadLeaguePackages);
    static readonly Lazy<IReadOnlyList<(long PackageId, int ClassType, int MinRank, int MaxRank)>> LeagueRankBands = new(LoadLeagueRankBands);

    /// <summary>当前正在进行的リーグ（JST 墙上时间口径，与主数据一致）。</summary>
    internal static LeaguePeriod? CurrentLeaguePeriod(DateTime? nowJst = null)
    {
        var now = nowJst ?? DateTime.UtcNow.AddHours(9);
        var jstNow = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);
        return LeaguePeriods.Value
            .Where(period => jstNow >= period.DisplayStart && jstNow <= period.DisplayEnd)
            .OrderByDescending(period => period.DisplayStart)
            .FirstOrDefault();
    }

    internal static IReadOnlyList<LeaguePeriod> AllLeaguePeriods() => LeaguePeriods.Value;

    internal static long LeagueGroupRankingPackageId(int classType, int rank) =>
        LeagueRankBands.Value
            .Where(band => band.ClassType == classType && rank >= band.MinRank && rank <= band.MaxRank)
            .Select(band => band.PackageId)
            .FirstOrDefault();

    internal static IReadOnlyList<(int Type, long Id, int Quantity)> ReadLeaguePackage(long packageId) =>
        LeaguePackages.Value.TryGetValue(packageId, out var things) ? things : Array.Empty<(int, long, int)>();

    static DateTime? ReadDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var date)) return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.String && DateTime.TryParse(inner.GetString(), out var innerDate))
            return DateTime.SpecifyKind(innerDate, DateTimeKind.Unspecified);
        return null;
    }

    static IReadOnlyList<LeaguePeriod> LoadLeaguePeriods()
    {
        var list = new List<LeaguePeriod>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Competition.LeagueMaster.json");
        if (stream is null) return list;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 8) continue;
            if (!row[0].TryGetInt64(out var id)) continue;
            var start = ReadDate(row[3]);
            var end = ReadDate(row[5]);
            if (start is null || end is null) continue;
            var classType = row[6].TryGetInt32(out var cls) ? cls : 3;
            list.Add(new LeaguePeriod(id, start.Value, end.Value, classType));
        }
        return list;
    }

    static IReadOnlyDictionary<long, IReadOnlyList<(int, long, int)>> LoadLeaguePackages()
    {
        var map = new Dictionary<long, IReadOnlyList<(int, long, int)>>();
        foreach (var resource in new[] { "Competition.LeagueRewardPackageMaster.json", "Competition.LeaguePlayRewardPackageMaster.json", "Competition.LeagueGroupRankingRewardPackageMaster.json", "Competition.TripleCastGroupRankingRewardPackageMaster.json" })
        {
            using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var document = JsonDocument.Parse(stream);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2 || !row[0].TryGetInt64(out var id)) continue;
                if (row[1].ValueKind != JsonValueKind.Array) continue;
                var things = new List<(int, long, int)>();
                foreach (var thing in row[1].EnumerateArray())
                {
                    if (thing.ValueKind != JsonValueKind.Array || thing.GetArrayLength() < 3) continue;
                    if (!thing[0].TryGetInt64(out var thingId) || !thing[1].TryGetInt32(out var type) || !thing[2].TryGetInt32(out var quantity)) continue;
                    things.Add((type, thingId, quantity));
                }
                map[id] = things;
            }
        }
        return map;
    }

    static IReadOnlyList<(long PackageId, int ClassType, int MinRank, int MaxRank)> LoadLeagueRankBands()
    {
        var list = new List<(long, int, int, int)>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Competition.LeagueGroupRankingRewardPackageMaster.json");
        if (stream is null) return list;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5) continue;
            if (!row[0].TryGetInt64(out var id) || !row[2].TryGetInt32(out var cls)
                || !row[3].TryGetInt32(out var min) || !row[4].TryGetInt32(out var max)) continue;
            list.Add((id, cls, min, max));
        }
        return list;
    }
}


