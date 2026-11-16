using System.Text.Json;
using Npgsql;

/// <summary>
/// 三連劇（TripleCast）奖励：
///  - 期间结束后按积分排名发 TripleCastGroupRankingRewardPackageMaster
///  - 积分来源：三連劇公演结算（分数/10000）
/// 与联赛同一套结算模型，落库保证只发一次。
/// </summary>
sealed partial class UserDataService
{
    internal sealed record TripleCastPeriod(long Id, DateTime DisplayStart, DateTime DisplayEnd);

    static readonly Lazy<IReadOnlyList<TripleCastPeriod>> TripleCastPeriods = new(LoadTripleCastPeriods);
    static readonly Lazy<IReadOnlyList<(long PackageId, int ClassType, int MinRank, int MaxRank)>> TripleCastRankBands = new(LoadTripleCastRankBands);

    internal static async Task EnsureTripleCastTablesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            create table if not exists user_triple_cast_states (
              "userId" bigint not null,
              triple_cast_master_id bigint not null,
              point bigint not null default 0,
              rank int not null default 0,
              updated_at timestamptz not null default now(),
              primary key ("userId", triple_cast_master_id)
            );

            create table if not exists user_triple_cast_settlements (
              triple_cast_master_id bigint primary key,
              status text not null default 'running',
              settled_at timestamptz,
              ranked_users int not null default 0,
              rewarded_users int not null default 0
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<long> AddTripleCastPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, long tripleCastId, int points)
    {
        await EnsureTripleCastTablesAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            insert into user_triple_cast_states("userId", triple_cast_master_id, point)
            values ($1, $2, $3)
            on conflict ("userId", triple_cast_master_id) do update
              set point = user_triple_cast_states.point + $3, updated_at = now()
            returning point
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(tripleCastId);
        command.Parameters.AddWithValue(points);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>期间结束结算三連劇排名奖励。</summary>
    internal static async Task<(bool Settled, int Ranked, int Rewarded)> SettleTripleCastAsync(
        NpgsqlConnection connection, long tripleCastId)
    {
        await EnsureTripleCastTablesAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = false;
        await using (var claim = new NpgsqlCommand(
            """
            insert into user_triple_cast_settlements(triple_cast_master_id, status)
            values ($1, 'running')
            on conflict (triple_cast_master_id) do nothing
            returning triple_cast_master_id
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue(tripleCastId);
            claimed = await claim.ExecuteScalarAsync() is not null;
        }
        if (!claimed)
        {
            await transaction.RollbackAsync();
            return (false, 0, 0);
        }

        var rows = new List<(long UserId, long Point)>();
        await using (var read = new NpgsqlCommand(
            """
            select "userId", point from user_triple_cast_states
            where triple_cast_master_id = $1 and point > 0
            order by point desc, "userId" asc
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(tripleCastId);
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        var rank = 0;
        var lastPoint = long.MinValue;
        var rewarded = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var (userId, point) = rows[i];
            if (point != lastPoint) { rank = i + 1; lastPoint = point; }

            await ExecuteAsync(connection, transaction,
                """
                update user_triple_cast_states set rank = $3, updated_at = now()
                where "userId" = $1 and triple_cast_master_id = $2
                """, userId, tripleCastId, rank);

            var band = TripleCastRankBands.Value.FirstOrDefault(b => rank >= b.MinRank && rank <= b.MaxRank);
            if (band.PackageId == 0) continue;
            foreach (var thing in ReadLeaguePackage(band.PackageId))
                await GrantRewardThingAsync(connection, transaction, userId, thing.Type, thing.Id, thing.Quantity);
            rewarded++;
        }

        await ExecuteAsync(connection, transaction,
            """
            update user_triple_cast_settlements
            set status = 'done', settled_at = now(), ranked_users = $2, rewarded_users = $3
            where triple_cast_master_id = $1
            """, tripleCastId, rows.Count, rewarded);
        await transaction.CommitAsync();
        Console.WriteLine($"[triple-cast] settled id={tripleCastId} ranked={rows.Count} rewarded={rewarded}");
        return (true, rows.Count, rewarded);
    }

    internal static IReadOnlyList<TripleCastPeriod> AllTripleCastPeriods() => TripleCastPeriods.Value;

    internal static TripleCastPeriod? CurrentTripleCastPeriod(DateTime? nowJst = null)
    {
        var now = DateTime.SpecifyKind(nowJst ?? DateTime.UtcNow.AddHours(9), DateTimeKind.Unspecified);
        return TripleCastPeriods.Value
            .Where(period => now >= period.DisplayStart && now <= period.DisplayEnd)
            .OrderByDescending(period => period.DisplayStart)
            .FirstOrDefault();
    }

    static IReadOnlyList<TripleCastPeriod> LoadTripleCastPeriods()
    {
        var list = new List<TripleCastPeriod>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Competition.TripleCastMaster.json");
        if (stream is null) return list;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 10) continue;
            if (!row[0].TryGetInt64(out var id)) continue;
            var start = ReadDate(row[7]);
            var end = ReadDate(row[9]);
            if (start is null || end is null) continue;
            list.Add(new TripleCastPeriod(id, start.Value, end.Value));
        }
        return list;
    }

    static IReadOnlyList<(long PackageId, int ClassType, int MinRank, int MaxRank)> LoadTripleCastRankBands()
    {
        var list = new List<(long, int, int, int)>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Competition.TripleCastGroupRankingRewardPackageMaster.json");
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
