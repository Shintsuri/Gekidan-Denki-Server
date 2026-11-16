using System.Text.Json;
using Npgsql;

/// <summary>
/// 活动奖励（累计 PT 奖励 / 排名奖励）。
///
/// 官方行为（按用户要求对齐）：
///  1) 累计 PT 奖励：活动进行中 PT 达到档位就发放（不再需要手动领取）。
///  2) 排名奖励：活动结算期结束时（StoryEventMaster.ForceEndDate）按最终排名发放。
///
/// 数据结构：
///  user_story_event_total_point_rewards  已发放的累计 PT 档位（防止重复发放 / 给客户端 receivedRewardOrder）
///  user_story_event_rank_rewards         已发放的排名奖励
///  user_story_event_settlements          活动结算快照（保证每个活动只结算一次）
/// </summary>
sealed partial class UserDataService
{
    internal sealed record EventTotalPointGrant(int ReceivedOrder, object?[] Drops, object?[] Present);

    internal sealed record EventRankSettlementResult(bool Settled, int Ranked, int Rewarded, string Detail);

    static bool eventRewardTablesReady;

    internal static async Task EnsureEventRewardTablesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        if (eventRewardTablesReady) return;
        const string sql = """
            create table if not exists user_story_event_total_point_rewards (
              "userId" bigint not null,
              event_master_id bigint not null,
              reward_master_id bigint not null,
              required_point bigint not null default 0,
              granted_at timestamptz not null default now(),
              primary key ("userId", event_master_id, reward_master_id)
            );

            create table if not exists user_story_event_rank_rewards (
              "userId" bigint not null,
              event_master_id bigint not null,
              final_rank int not null,
              reward_master_id bigint not null,
              package_id bigint not null,
              granted_at timestamptz not null default now(),
              primary key ("userId", event_master_id)
            );

            create table if not exists user_story_event_settlements (
              event_master_id bigint primary key,
              status text not null default 'running',
              force_end_at timestamptz,
              started_at timestamptz not null default now(),
              settled_at timestamptz,
              ranked_users int not null default 0,
              rewarded_users int not null default 0
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        eventRewardTablesReady = true;
    }

    /// <summary>
    /// 发放累计 PT 奖励中“已达成但还没发”的档位，返回最新已发放档位数与展示数据。
    /// 调用方必须处于事务中（打歌 / 稽古结算）。
    /// </summary>
    internal static async Task<EventTotalPointGrant> GrantEventTotalPointRewardsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        long eventId,
        long totalPoint)
    {
        await EnsureEventRewardTablesAsync(connection, transaction);

        var granted = new HashSet<long>();
        await using (var command = new NpgsqlCommand(
            """
            select reward_master_id
            from user_story_event_total_point_rewards
            where "userId" = $1 and event_master_id = $2
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(eventId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) granted.Add(reader.GetInt64(0));
        }

        var drops = new List<object?>();
        var present = new List<object?>();
        var newlyGranted = 0;
        foreach (var tier in EventRewardRules.TotalPointRewards(eventId))
        {
            if (tier.RequiredPoint > totalPoint || granted.Contains(tier.Id)) continue;

            var reward = await GrantEventThingAsync(connection, transaction, userId, tier.ThingType, tier.ThingId, tier.Quantity);
            if (reward.Present is not null) present.Add(reward.Present);
            drops.Add(new object?[]
            {
                new object?[] { tier.ThingType, tier.ThingId, tier.Quantity, null, null, null, false }, 0, false
            });

            await using (var insert = new NpgsqlCommand(
                """
                insert into user_story_event_total_point_rewards("userId", event_master_id, reward_master_id, required_point)
                values ($1, $2, $3, $4)
                on conflict do nothing
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(userId);
                insert.Parameters.AddWithValue(eventId);
                insert.Parameters.AddWithValue(tier.Id);
                insert.Parameters.AddWithValue(tier.RequiredPoint);
                await insert.ExecuteNonQueryAsync();
            }
            granted.Add(tier.Id);
            newlyGranted++;
        }

        if (newlyGranted > 0)
            Console.WriteLine($"[event-reward] user={userId} event={eventId} totalPoint={totalPoint} grantedTotalPointTiers={newlyGranted}");

        return new EventTotalPointGrant(granted.Count, drops.ToArray(), present.ToArray());
    }

    /// <summary>已发放的累计 PT 档位数（客户端 union 140 的 receivedRewardOrder）。</summary>
    internal static async Task<int> ReadReceivedTotalPointOrderAsync(NpgsqlConnection connection, long userId, long eventId)
    {
        await EnsureEventRewardTablesAsync(connection);
        await using var command = new NpgsqlCommand(
            """
            select count(*)::int
            from user_story_event_total_point_rewards
            where "userId" = $1 and event_master_id = $2
            """, connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(eventId);
        return await command.ExecuteScalarAsync() is int count ? count : 0;
    }

    /// <summary>事务内版本：避免在已有事务的连接上产生“游离命令”。</summary>
    internal static async Task<int> ReadReceivedTotalPointOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, long eventId)
    {
        await EnsureEventRewardTablesAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            select count(*)::int
            from user_story_event_total_point_rewards
            where "userId" = $1 and event_master_id = $2
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(eventId);
        return await command.ExecuteScalarAsync() is int count ? count : 0;
    }

    /// <summary>
    /// 客户端手动领取入口：官方界面有「受け取る」按钮，正常情况已自动发放，这里只补发漏掉的档位并返回奖励列表。
    /// </summary>
    internal static async Task<object?[]> ReceiveTotalPointRewardsAsync(NpgsqlConnection connection, long userId, long eventId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        long totalPoint;
        await using (var command = new NpgsqlCommand(
            """
            select point from user_story_event_states
            where "userId" = $1 and event_master_id = $2
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(eventId);
            totalPoint = await command.ExecuteScalarAsync() is long point ? point : 0L;
        }

        var grant = await GrantEventTotalPointRewardsAsync(connection, transaction, userId, eventId, totalPoint);
        await transaction.CommitAsync();

        var rewards = new List<object?>();
        foreach (var drop in grant.Drops)
        {
            if (drop is not object?[] entry || entry[0] is not object?[] thing) continue;
            rewards.Add(new object?[]
            {
                Convert.ToInt32(thing[0]), Convert.ToInt64(thing[1]), Convert.ToInt32(thing[2]), null, null, null, false
            });
        }
        return rewards.ToArray();
    }

    /// <summary>总点数活动信息：客户端 TotalPointEventInformationResult = [CurrentTotalPoint, EntryCode]。</summary>
    internal async Task<object?[]> ReadTotalPointEventInformationAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var eventId = ReadFirstActiveEventId();
        if (eventId == 0) return new object?[] { 0L, "" };

        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select point from user_story_event_states
            where "userId" = $1 and event_master_id = $2
            """, connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(eventId);
        var point = await command.ExecuteScalarAsync() is long value ? value : 0L;
        return new object?[] { point, "" };
    }

    /// <summary>客户端领取入口（幂等）：补发漏发的累计 PT 档位，返回 ReceivedThing 列表。</summary>
    internal async Task<object?[]> ReceiveTotalPointRewardsAsync(HttpContext context, long eventId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        if (eventId == 0) eventId = ReadFirstActiveEventId();
        if (eventId == 0) return Array.Empty<object?>();

        await using var connection = await database.OpenConnectionAsync();
        return await ReceiveTotalPointRewardsAsync(connection, userId, eventId);
    }

    static long ReadFirstActiveEventId()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "active_events.json");
            if (!File.Exists(path)) return 0;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.TryGetInt64(out var eventId)) return eventId;
        }
        catch
        {
            // 配置坏了就当没有活动
        }
        return 0;
    }

    /// <summary>发放单个奖励物（ThingTypes）。返回客户端 present 条目（可为 null）。</summary>
    internal static async Task<(object? Present, bool SentInbox)> GrantEventThingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        int thingType,
        long thingId,
        int quantity)
    {
        // 统一走 RewardGrant：活动奖励支持全部 ThingTypes
        var present = await GrantRewardThingAsync(connection, transaction, userId, thingType, thingId, quantity);
        return (present, false);
    }
    internal static async Task<EventRankSettlementResult> SettleStoryEventRankingAsync(
        NpgsqlConnection connection,
        long eventId,
        DateTime forceEndUtc)
    {
        await EnsureEventRewardTablesAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = false;        await using (var claim = new NpgsqlCommand(
            """
            insert into user_story_event_settlements(event_master_id, status, force_end_at, started_at)
            values ($1, 'running', $2, now())
            on conflict (event_master_id) do nothing
            returning event_master_id
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue(eventId);
            claim.Parameters.AddWithValue(forceEndUtc);
            claimed = await claim.ExecuteScalarAsync() is not null;
        }
        if (!claimed)
        {
            await transaction.RollbackAsync();
            return new EventRankSettlementResult(false, 0, 0, "already-settled");
        }

        var rows = new List<(long UserId, long Point)>();
        await using (var command = new NpgsqlCommand(
            """
            select "userId", point
            from user_story_event_states
            where event_master_id = $1 and point > 0
            order by point desc, "userId" asc
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(eventId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        var ranked = 0;
        var rewarded = 0;
        var lastPoint = long.MinValue;
        var rank = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var (userId, point) = rows[i];
            if (point != lastPoint)
            {
                rank = i + 1;
                lastPoint = point;
            }
            ranked++;

            await ExecuteAsync(connection, transaction,
                """
                update user_story_event_states
                set rank = $3, updated_at = now()
                where "userId" = $1 and event_master_id = $2
                """, userId, eventId, rank);

            var tier = EventRewardRules.RankRewardFor(eventId, rank);
            if (tier is null) continue;
            var things = EventRewardRules.PackageRewards(tier.PackageId);
            if (things.Count == 0) continue;

            // 已经发过（例如手工补发过）就不再重复发
            var alreadyRewarded = false;
            await using (var check = new NpgsqlCommand(
                """
                select exists (
                  select 1 from user_story_event_rank_rewards
                  where "userId" = $1 and event_master_id = $2
                )
                """, connection, transaction))
            {
                check.Parameters.AddWithValue(userId);
                check.Parameters.AddWithValue(eventId);
                alreadyRewarded = await check.ExecuteScalarAsync() is true;
            }
            if (alreadyRewarded) continue;

            foreach (var thing in things)
                await GrantEventThingAsync(connection, transaction, userId, thing.ThingType, thing.ThingId, thing.Quantity);

            await using (var record = new NpgsqlCommand(
                """
                insert into user_story_event_rank_rewards("userId", event_master_id, final_rank, reward_master_id, package_id)
                values ($1, $2, $3, $4, $5)
                on conflict ("userId", event_master_id) do update
                  set final_rank = excluded.final_rank,
                      reward_master_id = excluded.reward_master_id,
                      package_id = excluded.package_id
                """, connection, transaction))
            {
                record.Parameters.AddWithValue(userId);
                record.Parameters.AddWithValue(eventId);
                record.Parameters.AddWithValue(rank);
                record.Parameters.AddWithValue(tier.Id);
                record.Parameters.AddWithValue(tier.PackageId);
                await record.ExecuteNonQueryAsync();
            }
            rewarded++;
        }

        await ExecuteAsync(connection, transaction,
            """
            update user_story_event_settlements
            set status = 'done', settled_at = now(), ranked_users = $2, rewarded_users = $3
            where event_master_id = $1
            """, eventId, ranked, rewarded);

        await transaction.CommitAsync();
        Console.WriteLine($"[event-settlement] event={eventId} ranked={ranked} rewarded={rewarded} forceEndUtc={forceEndUtc:O}");
        return new EventRankSettlementResult(true, ranked, rewarded, $"ranked={ranked} rewarded={rewarded}");
    }
}
