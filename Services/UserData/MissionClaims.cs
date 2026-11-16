using System.Text.Json;
using Npgsql;

sealed partial class UserDataService
{
    static readonly Lazy<IReadOnlyDictionary<long, MissionClaimDefinition>> MissionClaimDefinitions = new(LoadMissionClaimDefinitions);

    public sealed record MissionClaimResponse(object?[] Rewards, object?[] Present);

    public async Task<object?[]> MarkShopFreePackMissionAsync(HttpContext context, object? payload)
    {
        if (payload is not object?[] { Length: > 0 } values || GetLong(values, 0) != 4101)
            return Array.Empty<object?>();
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);
        await using var connection = await database.OpenConnectionAsync();
        await CompleteShopFreePackMissionsAsync(connection, null, userId);
        var present = (await ReadMissionsAsync(connection, userId))
            .Where(row => Convert.ToInt64(row[5]) is 100300 or 100400)
            .Select(row => (object?)DataObject(48, row))
            .ToList();
        present.Add(DataObject(0, await ReadUserAsync(connection, userId)));
        present.Add(DataObject(128, await ReadCurrencyAsync(connection, userId)));
        return present.ToArray();
    }

    static async Task CompleteShopFreePackMissionsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long userId)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into user_mission_statuses
              (id, "userId", is_cleared, is_received, progress, completed_at, mission_from, mission_to)
            values ($1, $2, true, false, $3, now(), $4, $5)
            on conflict ("userId", mission_from, mission_to)
            do update set is_cleared = true, progress = excluded.progress,
                          completed_at = coalesce(user_mission_statuses.completed_at, now()),
                          updated_at = now()
            """, connection, transaction);
        // 只完成「每日免费包」任务（100400）。
        // 之前这里连 100300「完成所有每日任务」也一起写成 progress=3、is_cleared=true，
        // 导致玩家领一次免费包（或做 1 个日常）就白拿 50 石。
        foreach (var (from, to, progress) in new[] { (100400L, 100401L, 1) })
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(UserScopedId(userId, 1000000 + from));
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(progress);
            command.Parameters.AddWithValue(from);
            command.Parameters.AddWithValue(to);
            await command.ExecuteNonQueryAsync();
        }
        // 免费包任务完成后必须重算「完成所有每日任务」(100300)，
        // 否则玩家做满 3 个日常也领不到 50 石。
        await RefreshDailyAllMissionAsync(connection, transaction, userId);
    }

    public async Task<MissionClaimResponse> ReceiveMissionRewardsAsync(HttpContext context, long? missionId, int? category)
    {
        if (missionId is null && category is null)
            throw new BadHttpRequestException(CharacterProgressionErrors.InvalidRequest);
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockLessonUserAsync(connection, transaction, userId);
        var candidates = new List<(long Id, long From, long To, int Progress, DateTime? CompletedAt)>();
        await using (var command = new NpgsqlCommand(
            """
            select id, mission_from, mission_to, progress, completed_at
            from user_mission_statuses
            where "userId" = $1 and is_cleared and not is_received
            for update
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                candidates.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }

        var selected = candidates.Where(row =>
                MissionClaimDefinitions.Value.TryGetValue(row.From, out var definition)
                && (missionId == row.From || missionId == row.Id || category == definition.Category))
            .ToArray();
        var rewards = new List<object?>();
        var present = new List<object?>();
        var updatedItems = new HashSet<long>();
        var currencyChanged = false;
        foreach (var status in selected)
        {
            var definition = MissionClaimDefinitions.Value[status.From];
            var stage = definition.Stages.SingleOrDefault(value => value.Id == status.To);
            if (stage is null) throw new InvalidOperationException($"任务 {status.From} 的阶段 {status.To} 不存在");
            foreach (var reward in stage.Rewards)
            {
                rewards.Add(new object?[] { reward.Type, reward.MasterId, reward.Quantity, null, null, null, false });
                if (reward.Type == 1)
                {
                    await UpsertItemQuantityAsync(connection, transaction, userId, reward.MasterId, reward.Quantity);
                    updatedItems.Add(reward.MasterId);
                }
                else if (reward.Type is 12 or 13)
                {
                    var column = reward.Type == 12 ? "coin" : "free_jewel";
                    await ExecuteAsync(connection, transaction,
                        $"update user_item_currencies set {column} = {column} + $2 where \"userId\" = $1",
                        userId, reward.Quantity);
                    await ExecuteAsync(connection, transaction,
                        $"update user_game_states set {column} = {column} + $2 where \"userId\" = $1",
                        userId, reward.Quantity);
                    currencyChanged = true;
                }
                else
                {
                    throw new InvalidOperationException($"任务 {status.From} 使用尚未实现的奖励类型 {reward.Type}");
                }
            }

            await ExecuteAsync(connection, transaction,
                "update user_mission_statuses set is_received = true, updated_at = now() where id = $1 and \"userId\" = $2",
                status.Id, userId);
        }

        if (currencyChanged)
        {
            present.Add(DataObject(0, await ReadUserAsync(connection, userId)));
            present.Add(DataObject(128, await ReadCurrencyAsync(connection, userId)));
        }
        if (updatedItems.Count > 0)
        {
            foreach (var item in await ReadItemPossessionsAsync(connection, userId))
                if (updatedItems.Contains(Convert.ToInt64(item[1]))) present.Add(DataObject(27, item));
        }
        // 重试没有新奖励时也返回权威状态，修复客户端漏收上一次领取响应后的角标。
        present.InsertRange(0, (await ReadMissionsAsync(connection, userId)).Where(row =>
            MissionClaimDefinitions.Value.TryGetValue(Convert.ToInt64(row[5]), out var definition)
            && (missionId == Convert.ToInt64(row[5]) || missionId == Convert.ToInt64(row[0]) || category == definition.Category))
            .Select(row => DataObject(48, row)));
        await transaction.CommitAsync();
        context.RequestServices.GetRequiredService<ILogger<UserDataService>>().LogInformation(
            "任务奖励领取完成 operation={Operation} userId={UserId} missionId={MissionId} category={Category} claimedCount={ClaimedCount}",
            "mission-claim", userId, missionId, category, selected.Length);
        return new MissionClaimResponse(rewards.ToArray(), present.ToArray());
    }

    static IReadOnlyDictionary<long, MissionClaimDefinition> LoadMissionClaimDefinitions()
    {
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("Mission.MissionMaster.json")
            ?? throw new InvalidOperationException("缺少任务主数据");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToDictionary(
            row => row[0].GetInt64(),
            row => new MissionClaimDefinition(
                row[1].GetInt32(),
                row[16].EnumerateArray().Select(stage => new MissionClaimStage(
                    stage[0].GetInt64(),
                    stage[4].EnumerateArray().Select(reward => new MissionClaimReward(
                        reward[1].GetInt32(), reward[0].GetInt64(), reward[2].GetInt32())).ToArray()))
                    .ToArray()));
    }

    sealed record MissionClaimDefinition(int Category, MissionClaimStage[] Stages);
    sealed record MissionClaimStage(long Id, MissionClaimReward[] Rewards);
    sealed record MissionClaimReward(int Type, long MasterId, int Quantity);

    /// <summary>每日任务进度 +delta（如消耗体力/写真/稽古），够了就标记完成。</summary>
    static async Task AdvanceDailyMissionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        long userId, long from, int delta)
    {
        var target = from switch { 100000L => 300, 100100L => 1, 100200L => 1, _ => 1 };
        await using var command = new NpgsqlCommand("""
            insert into user_mission_statuses
              (id, "userId", is_cleared, is_received, progress, completed_at, mission_from, mission_to)
            values ($1, $2, false, false, least($3, $4), null, $5, $6)
            on conflict ("userId", mission_from, mission_to) do update set
              progress = least($3, user_mission_statuses.progress + $4),
              is_cleared = user_mission_statuses.is_cleared
                           or (user_mission_statuses.progress + $4 >= $3),
              completed_at = case
                when user_mission_statuses.is_cleared then user_mission_statuses.completed_at
                when user_mission_statuses.progress + $4 >= $3 then now()
                else user_mission_statuses.completed_at end,
              updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue(UserScopedId(userId, 1000000 + from));
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(target);
        command.Parameters.AddWithValue(delta);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(from + 1);
        await command.ExecuteNonQueryAsync();
        await RefreshDailyAllMissionAsync(connection, transaction, userId);
    }

    /// <summary>重算「完成所有每日任务」（100300）：三个日常任务都完成了才算完成。</summary>
    static async Task RefreshDailyAllMissionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long userId)
    {
        // 兜底：progress 已经达到目标但 is_cleared 还是 false 的（早期进度更新只写了 progress），
        // 这里统一补上，否则任务会「满了但领不了」。
        await using (var fix = new NpgsqlCommand("""
            update user_mission_statuses
            set is_cleared = true,
                completed_at = coalesce(completed_at, now()),
                updated_at = now()
            where "userId" = $1
              and not is_cleared
              and mission_from in (100000, 100200, 100400)
              and progress >= case mission_from when 100000 then 300 else 1 end
            """, connection, transaction))
        {
            fix.Parameters.AddWithValue(userId);
            await fix.ExecuteNonQueryAsync();
        }
        await using var count = new NpgsqlCommand("""
            select count(*) from user_mission_statuses
            where "userId" = $1 and is_cleared and mission_from in (100000, 100200, 100400)
            """, connection, transaction);
        count.Parameters.AddWithValue(userId);
        var done = Convert.ToInt32(await count.ExecuteScalarAsync());
        var cleared = done >= 3;
        await using var update = new NpgsqlCommand("""
            insert into user_mission_statuses
              (id, "userId", is_cleared, is_received, progress, completed_at, mission_from, mission_to)
            values ($1, $2, $3, false, $4, case when $3 then now() else null end, 100300, 100301)
            on conflict ("userId", mission_from, mission_to) do update set
              progress = excluded.progress,
              is_cleared = excluded.is_cleared,
              completed_at = case when excluded.is_cleared
                                  then coalesce(user_mission_statuses.completed_at, now())
                                  else null end,
              updated_at = now()
            """, connection, transaction);
        update.Parameters.AddWithValue(UserScopedId(userId, 1000000 + 100300));
        update.Parameters.AddWithValue(userId);
        update.Parameters.AddWithValue(cleared);
        update.Parameters.AddWithValue(done);
        await update.ExecuteNonQueryAsync();
    }

    /// <summary>写真「現像」完成 → 每日任务 100100 +1。</summary>
    public async Task RecordPhotoMissionAsync(HttpContext context)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AdvanceDailyMissionAsync(connection, transaction, userId, 100100, 1);
        await transaction.CommitAsync();
    }
}
