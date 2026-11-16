using Npgsql;

sealed partial class UserDataService
{
    public async Task<object?[]> AwakenCharacterAsync(HttpContext context, long characterId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long master;
        int level, phase;
        await using (var read = new NpgsqlCommand("""
            select character_master_id, level, awakening from user_character_cards
            where id=$1 and "userId"=$2 for update
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new BadHttpRequestException(CharacterAwakeningErrors.CharacterMissing, StatusCodes.Status404NotFound);
            master = reader.GetInt64(0);
            level = reader.GetInt32(1);
            phase = reader.GetInt32(2);
        }
        // 才能開花：按 (稀有度, 段阶) 从 CharacterBloomItemMaster 取消耗；段阶 0..5。
        var cost = CharacterBloomRules.Required(master, phase);
        var present = new List<object?>();
        if (cost is null)
        {
            // 已到最高段阶：返回当前数据，不再扣材料，也不改段阶。
            await using var current = new NpgsqlCommand("""
                select id, character_master_id, level, exp, awakening, character_base_id,
                       skill_level, extra_1, extra_2, is_new, display_awakening_status, is_favorite, talent_stage
                from user_character_cards where id=$1 and "userId"=$2
                """, connection, transaction);
            current.Parameters.AddWithValue(characterId);
            current.Parameters.AddWithValue(userId);
            await using var currentReader = await current.ExecuteReaderAsync();
            if (!await currentReader.ReadAsync()) throw new BadHttpRequestException(CharacterAwakeningErrors.CharacterMissing, StatusCodes.Status404NotFound);
            var card = ReadUpdatedCharacter(currentReader);
            await currentReader.DisposeAsync();
            await transaction.CommitAsync();
            return new object?[] { DataObject(4, card) };
        }
        var remaining = await ConsumeExperienceItemAsync(connection, transaction, userId, cost.Value.ItemMasterId, cost.Value.Quantity, CharacterAwakeningErrors.ItemInsufficient);
        present.Add(DataObject(27, new object?[] { UserScopedId(userId, cost.Value.ItemMasterId), cost.Value.ItemMasterId, remaining }));
        // 才能開花段阶 +1
        await using (var update = new NpgsqlCommand("""
            update user_character_cards
            set awakening=awakening+1,
                display_awakening_status = $3 and (awakening=0 or display_awakening_status)
            where id=$1 and "userId"=$2
            returning id, character_master_id, level, exp, awakening, character_base_id,
                      skill_level, extra_1, extra_2, is_new, display_awakening_status, is_favorite, talent_stage
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue(userId);
            update.Parameters.AddWithValue(CharacterAwakeningRules.HasAwakeningArt(master));
            await using var reader = await update.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("觉醒更新后角色数据丢失");
            present.Insert(0, DataObject(4, ReadUpdatedCharacter(reader)));
        }
        await transaction.CommitAsync();
        context.RequestServices.GetRequiredService<ILogger<UserDataService>>().LogInformation(
            "角色才能開花 operation=character-bloom userId={UserId} characterId={CharacterId} from={From} outcome=success", userId, characterId, phase);
        return present.ToArray();
    }

    // 客户端 2.31.3 的才能开花：一次可以指定目标段阶（stageTo）
    public async Task<object?[]> BloomTalentAsync(HttpContext context, long characterId, int stageTo)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await EnsureDefaultUserDataAsync(userId);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        long master;
        int phase;
        await using (var read = new NpgsqlCommand("""
            select character_master_id, talent_stage from user_character_cards
            where id=$1 and "userId"=$2 for update
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(characterId);
            read.Parameters.AddWithValue(userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new BadHttpRequestException(CharacterAwakeningErrors.CharacterMissing, StatusCodes.Status404NotFound);
            master = reader.GetInt64(0);
            phase = reader.GetInt32(1) - 1;   // talent_stage 是 1 起始，内部按 0 起始算
        }

        var present = new List<object?>();
        // 客户端传的 stageTo 是「段数」（1 起始，和 talent_stage 同基准），
        // 这里内部按 phase（0 起始）逐段消耗，所以要减 1，否则每次都会多升一段。
        var fromPhase = phase;
        var target = Math.Max(phase, Math.Min(stageTo - 1, phase + 6));
        var steps = 0;

        // 逐段消耗（每段消耗不同材料），中途材料不足就停在当前段
        while (phase < target && steps < 6)
        {
            var cost = CharacterBloomRules.Required(master, phase);
            if (cost is null) break;
            try
            {
                var remaining = await ConsumeExperienceItemAsync(connection, transaction, userId,
                    cost.Value.ItemMasterId, cost.Value.Quantity, CharacterAwakeningErrors.ItemInsufficient);
                present.RemoveAll(item => item is object?[] row && row.Length > 0 && row[0] is int key && key == 27);
                present.Add(DataObject(27, new object?[] { UserScopedId(userId, cost.Value.ItemMasterId), cost.Value.ItemMasterId, remaining }));
            }
            catch (BadHttpRequestException)
            {
                break;
            }
            phase++;
            steps++;
        }

        await using (var update = new NpgsqlCommand("""
            update user_character_cards
            set talent_stage=$3 + 1
            where id=$1 and "userId"=$2
            returning id, character_master_id, level, exp, awakening, character_base_id,
                      skill_level, extra_1, extra_2, is_new, display_awakening_status, is_favorite, talent_stage
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(characterId);
            update.Parameters.AddWithValue(userId);
            update.Parameters.AddWithValue(phase);
            update.Parameters.AddWithValue(CharacterAwakeningRules.HasAwakeningArt(master));
            await using var reader = await update.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("才能开花更新后角色数据丢失");
            present.Insert(0, DataObject(4, ReadUpdatedCharacter(reader)));
        }

        await transaction.CommitAsync();
        context.RequestServices.GetRequiredService<ILogger<UserDataService>>().LogInformation(
            "角色才能开花(指定段阶) operation=character-bloom-stage userId={UserId} characterId={CharacterId} stageTo={StageTo} fromPhase={FromPhase} targetPhase={TargetPhase} toPhase={ToPhase} steps={Steps} outcome=success",
            userId, characterId, stageTo, fromPhase, target, phase, steps);
        return present.ToArray();
    }

    public async Task<object?[]> SwitchCharacterDisplayAwakeningStatusAsync(HttpContext context, long characterId)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        await using var connection = await database.OpenConnectionAsync();
        // 单条更新原子切换，避免并发请求读到同一个旧值。
        await using var command = new NpgsqlCommand("""
            update user_character_cards set display_awakening_status = not display_awakening_status
            where id=$1 and "userId"=$2 and awakening > 0 and character_master_id=any($3)
            returning id, character_master_id, level, exp, awakening, character_base_id,
                      skill_level, extra_1, extra_2, is_new, display_awakening_status, is_favorite, talent_stage
            """, connection);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(CharacterAwakeningRules.DisplayMasterIds);
        object?[] card;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                throw new BadHttpRequestException(CharacterAwakeningErrors.DisplayUnavailable, StatusCodes.Status409Conflict);
            card = ReadUpdatedCharacter(reader);
        }
        context.RequestServices.GetRequiredService<ILogger<UserDataService>>().LogInformation(
            "切换角色卡面 operation=character-art-switch userId={UserId} characterId={CharacterId} displayAwakeningStatus={DisplayAwakeningStatus} outcome=success",
            userId, characterId, card[10]);
        return new object?[] { DataObject(4, card) };
    }
}
