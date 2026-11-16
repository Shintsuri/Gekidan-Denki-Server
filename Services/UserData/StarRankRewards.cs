using System.Text.Json;
using Npgsql;

/// <summary>
/// 星等级奖励（StarRankRewardMaster / CharacterStarRankRewardGroupMaster）。
///
/// 官方行为：星等级到达某档位时，该档位的奖励自动发放（界面只是「報酬一覧」，显示達成）。
/// 这里在星等级提升时补发所有未领档位，并保留一个客户端领取入口（幂等补发）。
/// </summary>
sealed partial class UserDataService
{
    internal sealed record StarRankGrant(int Ranks, List<(int Type, long Id, int Quantity)> Things);

    static readonly Lazy<IReadOnlyDictionary<(long BaseId, int Rank), long>> StarRankGroups = new(LoadStarRankGroups);
    static readonly Lazy<IReadOnlyDictionary<long, (int Type, long Id, int Quantity)[]>> StarRankGroupThings = new(LoadStarRankGroupThings);

    static bool starRankRewardTableReady;

    internal static async Task EnsureStarRankRewardTableAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        if (starRankRewardTableReady) return;
        await using var command = new NpgsqlCommand(
            """
            create table if not exists user_star_rank_rewards (
              "userId" bigint not null,
              character_base_master_id bigint not null,
              rank int not null,
              granted_at timestamptz not null default now(),
              primary key ("userId", character_base_master_id, rank)
            )
            """, connection, transaction);
        await command.ExecuteNonQueryAsync();
        starRankRewardTableReady = true;
    }

    /// <summary>补发到指定星等级为止的所有未领奖励。</summary>
    internal static async Task<StarRankGrant> GrantStarRankRewardsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        long characterBaseMasterId,
        int upToRank)
    {
        await EnsureStarRankRewardTableAsync(connection, transaction);
        var things = new List<(int Type, long Id, int Quantity)>();
        if (upToRank <= 0) return new StarRankGrant(0, things);

        var granted = new HashSet<int>();
        await using (var command = new NpgsqlCommand(
            """
            select rank from user_star_rank_rewards
            where "userId" = $1 and character_base_master_id = $2
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(characterBaseMasterId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) granted.Add(reader.GetInt32(0));
        }

        var ranks = 0;
        for (var rank = 1; rank <= upToRank; rank++)
        {
            if (granted.Contains(rank)) continue;
            if (!StarRankGroups.Value.TryGetValue((characterBaseMasterId, rank), out var groupId)) continue;
            if (!StarRankGroupThings.Value.TryGetValue(groupId, out var rewards)) continue;

            foreach (var reward in rewards)
            {
                await GrantRewardThingAsync(connection, transaction, userId, reward.Type, reward.Id, reward.Quantity);
                things.Add(reward);
            }

            await using (var insert = new NpgsqlCommand(
                """
                insert into user_star_rank_rewards("userId", character_base_master_id, rank)
                values ($1, $2, $3)
                on conflict do nothing
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(userId);
                insert.Parameters.AddWithValue(characterBaseMasterId);
                insert.Parameters.AddWithValue(rank);
                await insert.ExecuteNonQueryAsync();
            }
            ranks++;
        }

        if (ranks > 0)
            Console.WriteLine($"[star-rank] user={userId} base={characterBaseMasterId} upToRank={upToRank} grantedRanks={ranks} things={things.Count}");
        return new StarRankGrant(ranks, things);
    }

    /// <summary>客户端「领取」入口：补发当前星等级之前所有未领档位。</summary>
    internal async Task<object?[]> ReceiveStarRankRewardsAsync(HttpContext context, long baseOrCharacterId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();

        var baseMasterId = await ResolveBaseMasterIdAsync(connection, userId, baseOrCharacterId);
        if (baseMasterId == 0) return Array.Empty<object?>();

        var rank = 0;
        await using (var command = new NpgsqlCommand(
            """
            select rank from user_character_bases
            where "userId" = $1 and character_base_master_id = $2
            """, connection))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(baseMasterId);
            rank = await command.ExecuteScalarAsync() is int value ? value : 0;
        }

        await using var transaction = await connection.BeginTransactionAsync();
        var grant = await GrantStarRankRewardsAsync(connection, transaction, userId, baseMasterId, rank);
        await transaction.CommitAsync();

        return grant.Things
            .Select(thing => new object?[] { thing.Type, thing.Id, thing.Quantity, null, null, null, false })
            .ToArray();
    }

    static async Task<long> ResolveBaseMasterIdAsync(NpgsqlConnection connection, long userId, long id)
    {
        if (id <= 0) return 0;
        // 参数可能是 user_character_bases.id（行 id），也可能是 character_base_master_id
        await using var command = new NpgsqlCommand(
            """
            select character_base_master_id
            from user_character_bases
            where "userId" = $1 and (id = $2 or character_base_master_id = $2)
            order by (id = $2) desc
            limit 1
            """, connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync() is long value ? value : 0L;
    }

    static IReadOnlyDictionary<(long BaseId, int Rank), long> LoadStarRankGroups()
    {
        var map = new Dictionary<(long, int), long>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("CharacterMission.StarRankRewardMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;
            if (!row[0].TryGetInt32(out var rank) || !row[1].TryGetInt64(out var baseId) || !row[2].TryGetInt64(out var groupId)) continue;
            map[(baseId, rank)] = groupId;
        }
        return map;
    }

    static IReadOnlyDictionary<long, (int Type, long Id, int Quantity)[]> LoadStarRankGroupThings()
    {
        var map = new Dictionary<long, (int, long, int)[]>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("CharacterMission.CharacterStarRankRewardGroupMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2 || !row[0].TryGetInt64(out var id)) continue;
            if (row[1].ValueKind != JsonValueKind.Array) continue;
            var things = new List<(int, long, int)>();
            foreach (var thing in row[1].EnumerateArray())
            {
                if (thing.ValueKind != JsonValueKind.Array || thing.GetArrayLength() < 3) continue;
                if (!thing[0].TryGetInt32(out var type) || !thing[1].TryGetInt64(out var thingId) || !thing[2].TryGetInt32(out var quantity)) continue;
                things.Add((type, thingId, quantity));
            }
            map[id] = things.ToArray();
        }
        return map;
    }
}
