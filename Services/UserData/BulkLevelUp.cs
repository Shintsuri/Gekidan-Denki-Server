using Npgsql;

sealed partial class UserDataService
{
    // 一键升级：/Characters/BulkLevelUp
    // 请求 payload: [[characterBaseId, index], ...]
    // 真正平均升级：轮转给每人一张经验道具，所有角色一起涨，直到道具用完或全部满级。
    public async Task<object?[]> BulkLevelUpAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);

        var characterIds = new List<long>();
        if (payload is object?[] rows)
            foreach (var row in rows)
                if (row is object?[] { Length: >= 1 } pair && GetLong(pair, 0) is long id && id > 0)
                    characterIds.Add(id);
        if (characterIds.Count is < 1 or > 10)
            throw new BadHttpRequestException(CharacterProgressionErrors.InvalidRequest);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        decimal bonus;
        await using (var readBonus = new NpgsqlCommand(
            "select experience_bonus from user_growth_bonuses where \"userId\"=$1 for update", connection, transaction))
        {
            readBonus.Parameters.AddWithValue(userId);
            bonus = (decimal)(await readBonus.ExecuteScalarAsync() ?? throw new InvalidOperationException("账号经验加成缺失"));
        }

        // 经验道具池（按 id 升序）
        var pool = (await ReadItemPossessionsAsync(connection, userId))
            .Select(row => (Id: Convert.ToInt64(row[1]), Quantity: Convert.ToInt32(row[2])))
            .Where(entry => entry.Quantity > 0 && CharacterProgressionRules.TryGetItem(entry.Id, out _, out _))
            .OrderBy(entry => entry.Id)
            .Select(entry => (entry.Id, entry.Quantity))
            .ToList();

        var present = new List<object?>();
        if (pool.Count == 0)
        {
            await transaction.CommitAsync();
            return present.ToArray();
        }

        // 载入角色当前等级/经验
        var states = new List<(long Id, long MasterId, int Level, int Experience)>();
        foreach (var characterId in characterIds)
        {
            var character = await ReadCharacterForUpdateAsync(connection, transaction, userId, characterId);
            states.Add((characterId, character.MasterId, character.Level, character.Experience));
        }

        var consumed = new Dictionary<long, int>();
        var poolIndex = 0;
        var progressed = true;
        // 轮转：每轮每人吃一张，谁满了就跳过，直到道具耗尽或全都满级。
        while (progressed)
        {
            progressed = false;
            for (var i = 0; i < states.Count; i++)
            {
                var (id, masterId, level, experience) = states[i];
                if (characterIds.Count == 0) break;
                var remaining = CharacterProgressionRules.Remaining(masterId, level, experience);
                if (remaining <= 0) continue;
                while (poolIndex < pool.Count && pool[poolIndex].Quantity <= 0) poolIndex++;
                if (poolIndex >= pool.Count) break;
                var itemId = pool[poolIndex].Id;
                if (!CharacterProgressionRules.TryGetItem(itemId, out var itemExp, out var itemBonus)) { poolIndex++; continue; }
                pool[poolIndex] = (itemId, pool[poolIndex].Quantity - 1);
                consumed[itemId] = consumed.GetValueOrDefault(itemId) + 1;
                var gain = Math.Max(1, (int)(itemExp * (100m + bonus) / 100m));
                bonus += itemBonus;
                var next = CharacterProgressionRules.ApplyExperience(level, experience, gain, masterId);
                states[i] = (id, masterId, next.Level, next.Experience);
                progressed = true;
            }
        }

        // 写回角色
        foreach (var (id, _, level, experience) in states)
        {
            var original = await ReadCharacterForUpdateAsync(connection, transaction, userId, id);
            if (original.Level == level && original.Experience == experience) continue;
            present.Add(DataObject(4, await UpdateCharacterExperienceAsync(connection, transaction, userId, id, (level, experience))));
        }

        // 扣道具
        foreach (var (itemMasterId, quantity) in consumed)
        {
            var left = await ConsumeExperienceItemAsync(connection, transaction, userId, itemMasterId, quantity);
            present.Add(DataObject(27, new object?[] { UserScopedId(userId, itemMasterId), itemMasterId, left }));
        }

        await using (var updateBonus = new NpgsqlCommand(
            "update user_growth_bonuses set experience_bonus=$2 where \"userId\"=$1", connection, transaction))
        {
            updateBonus.Parameters.AddWithValue(userId);
            updateBonus.Parameters.AddWithValue(bonus);
            await updateBonus.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        present.Add(DataObject(67, new object?[] { userId, (long)bonus, 0L }));
        return present.ToArray();
    }
}
