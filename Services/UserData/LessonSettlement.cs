using System.Security.Cryptography;
using Npgsql;

sealed partial class UserDataService
{
    public async Task<object?[]> FinishLessonAsync(HttpContext context, object? payload)
    {
        var userId = await GetCurrentUserIdAsync(context) ?? throw new BadHttpRequestException(PartyNotFound, 404);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId);
        var settlement = await FinishLessonCoreAsync(connection, transaction, userId, payload);
        await transaction.CommitAsync();
        return settlement.Present;
    }

    async Task<LiveSettlement> FinishLessonCoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, object? payload)
    {
        if (payload is not object?[] { Length: 3 } finish || GetLong(finish, 0) is not long score || score < 0
            || GetLong(finish, 1) is not long combo || combo is < 0 or > int.MaxValue)
            throw new BadHttpRequestException(LessonErrors.InvalidFinish, 400);
        var judges = ReadFinishJudges(finish);
        PlayerProgressionRules.Achievement(judges);
        if (combo > judges.Values.Sum(count => (long)count)) throw new BadHttpRequestException(LessonErrors.InvalidFinish, 400);
        var hash = SHA256.HashData(MsgPack.Encode(payload));
        var session = await ReadLessonSessionAsync(connection, transaction, userId)
            ?? throw new BadHttpRequestException(LessonErrors.SessionMissing, 409);
        // 稽古也要写一份谱面成绩：客户端「楽曲報酬」是按 user_live_play_results 的达成率
        // 点亮 5 评价 × 4 难度格子的。这一步放在幂等缓存判断之前，
        // 否则同一局重复结算（走 completion 缓存）时永远写不进去。
        if (PlayerProgressionRules.Charts.TryGetValue(session.LiveId, out var lessonChart))
        {
            await SaveChartResultAsync(connection, transaction, userId, lessonChart, score,
                PlayerProgressionRules.Achievement(judges), 0m, 1, true);
        }
        if (session.Completion is not null)
        {
            if (!hash.SequenceEqual(session.FinishHash!)) throw new BadHttpRequestException(LessonErrors.SessionConflict, 409);
            var cached = (object?[])MsgPack.Decode(session.Completion)!;
            if (cached.Length == 2)
            {
                var cachedResult = (object?[])cached[0]!;
                if (cachedResult[2] is null)
                {
                    // 补齐旧结算缓存缺失的显示字段，保留奖励和高分结果，不重新结算。
                    cachedResult[2] = await BuildLessonRateResultAsync(connection, userId, PlayerProgressionRules.Achievement(judges));
                    await ExecuteAsync(connection, transaction, "update user_lesson_sessions set completion=$2 where \"userId\"=$1",
                        userId, MsgPack.Encode(cached));
                }
                return new(cachedResult, (object?[])cached[1]!);
            }
            // 兼容修复前仅保存 Present 的已完成稽古，不重新发放奖励。
            var cachedParty = (object?[])cached.Cast<object?[]>().Single(row => Convert.ToInt32(row[0]) == 39)[1]!;
            return new(await BuildLessonFinishResultAsync(connection, userId, session.BaseId, score, judges,
                Convert.ToInt64(cachedParty[2]), Convert.ToInt64(cachedParty[2]), Array.Empty<LessonRules.Reward>()), cached);
        }
        var party = (await ReadLessonPartiesAsync(connection, userId, transaction)).Single(row => Convert.ToInt64(row[0]) == session.BaseId);
        // 稽古次数在「成功结算」时才扣一次：开始时不扣，这样失败/退出不消耗次数。
        await ExecuteAsync(connection, transaction,
            "update user_daily_limits set daily_lesson_times = daily_lesson_times + 1, updated_at = now() where \"userId\" = $1", userId);
        // 每日任务：稽古 1 回（100200）
        await AdvanceDailyMissionAsync(connection, transaction, userId, 100200, 1);
        var previousScore = Convert.ToInt64(party[2]);
        var bestScore = Math.Max(previousScore, score);
        var receivedScore = Convert.ToInt64(party[4]);
        var milestones = LessonRules.Milestones[session.BaseId].Where(rule => rule.Score > receivedScore && rule.Score <= bestScore).ToArray();
        var rewardScore = milestones.Length == 0 ? receivedScore : milestones.Max(rule => rule.Score);
        var touchedItems = new HashSet<long>();
        foreach (var reward in milestones.SelectMany(rule => rule.Rewards))
        {
            if (reward.Type == 1)
            {
                await UpsertItemQuantityAsync(connection, transaction, userId, reward.MasterId, reward.Quantity);
                touchedItems.Add(reward.MasterId);
            }
            else if (reward.Type is 12 or 13)
            {
                var column = reward.Type == 12 ? "coin" : "free_jewel";
                await ExecuteAsync(connection, transaction, $"update user_game_states set {column} = {column} + $2, updated_at = now() where \"userId\" = $1", userId, reward.Quantity);
                await ExecuteAsync(connection, transaction, $"update user_item_currencies set {column} = {column} + $2 where \"userId\" = $1", userId, reward.Quantity);
            }
            else throw new InvalidOperationException($"稽古奖励类型尚未实现：{reward.Type}");
        }
        await ExecuteAsync(connection, transaction,
            """
            update user_live_lesson_party_states
            set payload = payload || jsonb_build_object('bestScore',$3,'rewardReceivedHighScore',$4), updated_at=now()
            where "userId"=$1 and lesson_master_id=$2
            """, userId, session.BaseId, bestScore, rewardScore);
        party[2] = bestScore;
        party[4] = rewardScore;
        // 稽古给参战角色加经验（按分数折算），到 CharacterLevelMaster 的阈值就升级。
        // 这对应客户端结算页的角色等级变化，之前完全没有这段，所以打完稽古等级不动。
        var expGain = Math.Max(1, (int)(score / 100));
        var leveledCharacters = new List<object?[]>();
        foreach (var slot in (object?[][])party[1]!)
        {
            if (slot.Length < 2 || slot[1] is null || slot[1] is DBNull) continue;
            var cardId = Convert.ToInt64(slot[1]);
            try
            {
                var card = await ReadCharacterForUpdateAsync(connection, transaction, userId, cardId);
                var next = CharacterProgressionRules.ApplyExperience(card.Level, card.Experience, expGain, card.MasterId);
                if (next.Level != card.Level || next.Experience != card.Experience)
                    leveledCharacters.Add(await UpdateCharacterExperienceAsync(connection, transaction, userId, cardId, next));

                // 角色星级（user_character_bases）：客户端 CharacterBase 只有
                // StarRank(rank) 和 TotalStarPoint(exp)，没有「等级」。稽古给的星点累加到 exp，
                // rank 按 CharacterStarRankMaster 重新计算。level 列是 KeyMissionLevel，不能动。
                await using (var baseRead = new NpgsqlCommand(
                    """
                    select b.id, b.rank, b.exp, b.character_base_master_id
                    from user_character_bases b
                    join user_character_cards c on c.character_base_id = b.id and c."userId" = b."userId"
                    where c.id = $1 and b."userId" = $2
                    """, connection, transaction))
                {
                    baseRead.Parameters.AddWithValue(cardId);
                    baseRead.Parameters.AddWithValue(userId);
                    await using var reader = await baseRead.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var baseRowId = reader.GetInt64(0);
                        var baseRank = reader.GetInt32(1);
                        var basePoint = reader.GetInt32(2);
                        var baseMasterId = reader.GetInt64(3);
                        await reader.CloseAsync();
                        var nextPoint = checked(basePoint + expGain);
                        var nextRank = Math.Max(baseRank, CharacterMissionRules.Rank(nextPoint));
                        if (nextRank != baseRank || nextPoint != basePoint)
                            await ExecuteAsync(connection, transaction,
                                "update user_character_bases set exp=$3, rank=$4 where \"userId\"=$1 and id=$2",
                                userId, baseRowId, nextPoint, nextRank);
                        // 星等级提升 → 补发该档位的星等级奖励
                        if (nextRank > baseRank)
                            await GrantStarRankRewardsAsync(connection, transaction, userId, baseMasterId, nextRank);
                    }
                }
            }
            catch
            {
                // 单张卡升级失败不影响稽古结算
            }
        }
        // 活动点数：和普通演出同一套规则（只有 GM 在 active_events.json 里开启的活动才累加）。
        // 结算界面「活动PT」面板由 FinishLiveResult.EventResult(result[22]) 驱动，null 时客户端不显示。
        var eventPointRows = new List<object?[]>();
        var eventRewardPresent = new List<object?[]>();
        object?[]? eventResultRow = null;
        try
        {
            var activePath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
            if (System.IO.File.Exists(activePath))
            {
                using var activeDoc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(activePath));
                foreach (var item in activeDoc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetInt64(out var eventId)) continue;
                    var eventGain = (long)Math.Max(1, score / 1000);  // 和公演同系数
                    await using (var upsert = new NpgsqlCommand("""
                        insert into user_story_event_states(id, "userId", event_master_id, point)
                        values ($1, $2, $3, $4)
                        on conflict ("userId", event_master_id) do update
                          set point = user_story_event_states.point + $4, updated_at = now()
                        returning point
                        """, connection, transaction))
                    {
                        upsert.Parameters.AddWithValue(UserScopedId(userId, 9000000 + eventId));
                        upsert.Parameters.AddWithValue(userId);
                        upsert.Parameters.AddWithValue(eventId);
                        upsert.Parameters.AddWithValue(eventGain);
                        var total = Convert.ToInt64(await upsert.ExecuteScalarAsync());
                        // 奖励发放失败不能拖垮稽古结算：用 savepoint 单独回滚
                        await transaction.SaveAsync("event_reward_grant");
                        EventTotalPointGrant? eventGrant = null;
                        try
                        {
                            eventGrant = await GrantEventTotalPointRewardsAsync(
                                connection, transaction, userId, eventId, total);
                        }
                        catch
                        {
                            await transaction.RollbackAsync("event_reward_grant");
                        }
                        var receivedOrder = eventGrant?.ReceivedOrder
                            ?? await ReadReceivedTotalPointOrderAsync(connection, transaction, userId, eventId);
                        if (eventGrant is not null)
                            eventRewardPresent.AddRange(eventGrant.Present.Cast<object?[]>());
                        eventPointRows.Add(new object?[]
                        {
                            101,
                            new object?[]
                            {
                                UserScopedId(userId, 101000 + eventId), eventId, total,
                                DateTime.UtcNow, 0, false, 1
                            }
                        });
                        eventPointRows.Add(new object?[]
                        {
                            140,
                            new object?[] { UserScopedId(userId, 140000 + eventId), eventId, total, receivedOrder }
                        });
                        if (EventPtCoins.Map.TryGetValue(eventId, out var ptCoinId))
                            await UpsertItemQuantityAsync(connection, transaction, userId, ptCoinId, (int)eventGain);
                        eventResultRow ??= new object?[]{ (int)eventGain, 0L, null, null, null, null, null, 0L, 0L, eventId, eventId, null, null, null };
                    }
                }
            }
        }
        catch
        {
            // 活动结算失败不能影响稽古本身
        }
        var present = new List<object?>
        {
            DataObject(39, party), DataObject(109, await ReadDailyLimitAsync(connection, userId, transaction)),
            DataObject(0, await ReadUserAsync(connection, userId)), DataObject(128, await ReadCurrencyAsync(connection, userId))
        };
        foreach (var item in await ReadItemPossessionsAsync(connection, userId))
            if (touchedItems.Contains(Convert.ToInt64(item[1]))) present.Add(DataObject(27, item));
        foreach (var row in eventPointRows) present.Add(row);
        foreach (var row in eventRewardPresent) present.Add(row);
        foreach (var character in leveledCharacters) present.Add(DataObject(4, character));
        var result = await BuildLessonFinishResultAsync(connection, userId, session.BaseId, score, judges,
            previousScore, bestScore, milestones.SelectMany(rule => rule.Rewards).ToArray(), eventResultRow,
            Math.Max(1, (int)(score / 100)));
        var settlement = new LiveSettlement(result, present.ToArray());
        await ExecuteAsync(connection, transaction, "update user_lesson_sessions set completion=$2, finish_hash=$3 where \"userId\"=$1",
            userId, MsgPack.Encode(new object?[] { settlement.Result, settlement.Present }), hash);
        return settlement;
    }
    async Task<object?[]> BuildLessonRateResultAsync(NpgsqlConnection connection, long userId, decimal achievement)
    {
        var rating = await ReadPlayerRatingAsync(connection, userId);
        // 稽古不改写普通演出的 Rating，但结果页仍需要本次达成率的 Before/After。
        return new object?[] { new object?[] { 0d, (double)achievement }, new object?[] { 0d, 0d }, rating, rating };
    }

    async Task<object?[]> BuildLessonFinishResultAsync(NpgsqlConnection connection, long userId, long baseId, long score,
        Dictionary<int, int> judges, long before, long after, LessonRules.Reward[] rewards, object?[]? eventResult = null, int starPointGain = 0)
    {
        var characterBase = (await ReadCharacterBasesAsync(connection, userId)).Single(row => Convert.ToInt64(row[1]) == baseId);
        var achievement = PlayerProgressionRules.Achievement(judges);
        var result = new object?[31];
        result[1] = Array.Empty<object?>();
        result[2] = await BuildLessonRateResultAsync(connection, userId, achievement);
        result[5] = judges.Where(pair => pair.Key < 5).Sum(pair => pair.Value) == 0 ? 6 : 1;
        result[6] = PlayerProgressionRules.Grade(achievement);
        result[8] = score > before;
        result[9] = 0; result[10] = 0;
        result[11] = Array.Empty<object?>(); result[12] = Array.Empty<object?>();
        result[13] = (double)achievement; result[20] = 0; result[21] = 0;
        // 稽古结算同样要填 PlayerRankPointResult（Key 3）：客户端 OnGameResult 会无条件读它，
        // 之前一直是 null，导致 NullReferenceException，结算界面根本进不去。
        await using (var playerState = new NpgsqlCommand(
            "select rank, exp, stamina from user_game_states where \"userId\" = $1", connection))
        {
            playerState.Parameters.AddWithValue(userId);
            await using var playerReader = await playerState.ExecuteReaderAsync();
            if (await playerReader.ReadAsync())
            {
                var playerRank = playerReader.GetInt32(0);
                var playerExp = playerReader.GetInt32(1);
                var playerStamina = playerReader.GetInt32(2);
                result[3] = new object?[] { playerRank, playerRank, playerExp, playerExp, 0, playerStamina };
            }
            else result[3] = new object?[] { 1, 1, 0, 0, 0, 0 };
        }
        // 稽古结算界面会读 SpRateUpdateResult（Key 16），之前一直是 null，
        // 客户端拿到 null 后算 SP 面板就崩了。用本次/历史分数填充。
        result[16] = new object?[] { (int)Math.Min(int.MaxValue, after), (int)Math.Min(int.MaxValue, score),
            (int)Math.Min(int.MaxValue, after), (int)Math.Min(int.MaxValue, score) };
        // 「楽曲報酬」面板：普通演出用 result[23] 的掉落列表驱动，
        // 稽古之前一直是空数组，所以那个报酬动画不播。这里填成本次稽古拿到的奖励。
        result[23] = rewards
            .Select(reward => (object?)new object?[] { reward.Type, reward.MasterId, reward.Quantity, null, null, null, false })
            .ToArray();
        result[30] = Array.Empty<object?>();
        // 活动点数面板（普通演出用同一个字段）
        result[22] = eventResult;
        // 尚未实现 SP 加成计算，返回现有等级和点数，避免伪造角色成长。
        // 稽古会给星点（expGain = score/100），之前这里把 StarPointAcquired 写死成 0，
        // 客户端拿着「获得 0 星点」却看到等级已变化，结算动画算不下去。
        var pointAfter = Convert.ToInt32(characterBase[3]);
        var rankAfter = Convert.ToInt32(characterBase[2]);
        var pointBefore = Math.Max(0, pointAfter - Math.Max(0, starPointGain));
        var rankBefore = Math.Min(rankAfter, CharacterMissionRules.Rank(pointBefore));
        result[24] = new object?[] { baseId,
            new object?[] { rankBefore, rankAfter, pointBefore, pointAfter, Math.Max(0, starPointGain), Array.Empty<object?>() },
            rewards.Select(reward => (object?)new object?[] { reward.Type, reward.MasterId, reward.Quantity, null, null, null, false }).ToArray(), before, after };
        return result;
    }
}
