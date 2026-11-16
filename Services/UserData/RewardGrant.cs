using System.Text.Json;
using Npgsql;

/// <summary>
/// 统一奖励发放（ThingTypes 全类型）。
/// 所有“发东西”的入口（活动奖励、星等级奖励、任务、礼物箱…）都应该走这里，
/// 避免每个系统各自写一份 SQL 导致某些类型漏发。
///
/// 说明：
///  - 类型 7(スタンプ)/8(ネームプレート)/9(名前色)/18(名前ベース色)/19(アイコンフレーム) 没有“拥有表”，
///    客户端按主数据全量可用，因此发放本身不需要落库（返回 null）。
///  - 类型 2(Character) 用 CharacterMaster 的 base 映射建卡。
///    其余未覆盖类型打日志，方便后续补表。
/// </summary>
sealed partial class UserDataService
{
    /// <summary>发放一个奖励物；返回需要下发给客户端的 present 条目（可能为 null）。</summary>
    internal static async Task<object?[]> GrantRewardThingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        int thingType,
        long thingId,
        int quantity)
    {
        if (quantity <= 0) return null!;
        switch (thingType)
        {
            case 1: // Item
            {
                var item = await AddItemPossessionAsync(connection, transaction, userId, thingId, quantity);
                return DataObject(27, item);
            }
            case 2: // Character（角色卡）
            {
                var baseMasterId = CharacterBaseOfMaster(thingId);
                if (baseMasterId == 0)
                {
                    Console.WriteLine($"[reward] unknown character master={thingId}");
                    return null!;
                }

                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_character_bases(id, "userId", character_base_master_id, costume_master_id, selected_character_id)
                    values ($1, $2, $3, 11, 0)
                    on conflict ("userId", character_base_master_id) do nothing
                    """, UserScopedId(userId, 2000 + baseMasterId), userId, baseMasterId);

                long baseId;
                await using (var find = new NpgsqlCommand(
                    """
                    select id from user_character_bases
                    where "userId" = $1 and character_base_master_id = $2
                    """, connection, transaction))
                {
                    find.Parameters.AddWithValue(userId);
                    find.Parameters.AddWithValue(baseMasterId);
                    baseId = await find.ExecuteScalarAsync() is long value ? value : 0L;
                }
                if (baseId == 0) return null!;

                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_character_cards(id, "userId", character_master_id, character_base_id, skill_level)
                    values ($1, $2, $3, $4, 1)
                    on conflict ("userId", character_master_id) do nothing
                    """, UserScopedId(userId, 1000 + thingId), userId, thingId, baseId);
                return null!;
            }
            case 3: // Poster
            {
                var poster = await UpsertPosterAsync(connection, transaction, userId, thingId, 0);
                return DataObject(11, poster);
            }
            case 4: // Accessory
            {
                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_accessories("userId", accessory_master_id)
                    select $1, $2
                    where not exists (
                      select 1 from user_accessories where "userId" = $1 and accessory_master_id = $2)
                    """, userId, thingId);
                return null!;
            }
            case 5: // Costume
            {
                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_character_costumes(id, "userId", costume_master_id)
                    values ($1, $2, $3)
                    on conflict ("userId", costume_master_id) do nothing
                    """, UserScopedId(userId, 600000 + thingId), userId, thingId);
                return null!;
            }
            case 6: // Trophy
            {
                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_trophies(id, "userId", trophy_master_id, trophy_group_master_id, current_order)
                    values ($1, $2, $3, 0, $4)
                    on conflict do nothing
                    """, UserScopedId(userId, 600000 + thingId), userId, thingId, quantity);
                return null!;
            }
            case 12: // Coin
            {
                await AddCoinsAsync(connection, transaction, userId, quantity);
                return DataObject(128, await ReadCurrencyAsync(connection, transaction, userId));
            }
            case 13: // Jewel（免费石）
            {
                await AddFreeJewelsAsync(connection, transaction, userId, quantity);
                return DataObject(128, await ReadCurrencyAsync(connection, transaction, userId));
            }
            case 14: // Music
            {
                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_live_music_states(id, "userId", music_master_id, is_unlocked)
                    values ($1, $2, $3, true)
                    on conflict ("userId", music_master_id) do update set is_unlocked = true
                    """, UserScopedId(userId, 25000000 + thingId), userId, thingId);
                return null!;
            }
            case 15: // Stamina
            {
                await ExecuteAsync(connection, transaction,
                    """
                    update user_game_states
                    set stamina = least(3000, stamina + $2), updated_at = now()
                    where "userId" = $1
                    """, userId, quantity);
                return null!;
            }
            case 20: // Home skin
            {
                await ExecuteAsync(connection, transaction,
                    """
                    insert into user_home_skin_possessions("userId", home_skin_master_id)
                    values ($1, $2)
                    on conflict do nothing
                    """, userId, thingId);
                return null!;
            }
            case 7:  // Stamp
            case 8:  // Nameplate
            case 9:  // Name color
            case 18: // Name base color
            case 19: // Icon frame
                // 这些没有拥有表，客户端按主数据全量可用
                return null!;
            default:
                Console.WriteLine($"[reward] unsupported thingType={thingType} thingId={thingId} qty={quantity} user={userId}");
                return null!;
        }
    }

    static readonly Lazy<IReadOnlyDictionary<long, long>> CharacterBaseByMaster = new(LoadCharacterBaseByMaster);

    static IReadOnlyDictionary<long, long> LoadCharacterBaseByMaster()
    {
        var map = new Dictionary<long, long>();
        using var stream = typeof(UserDataService).Assembly.GetManifestResourceStream("InventoryMaster.CharacterMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            if (!row[0].TryGetInt64(out var cardId) || !row[1].TryGetInt64(out var baseId)) continue;
            map[cardId] = baseId;
        }
        return map;
    }

    static long CharacterBaseOfMaster(long characterMasterId) =>
        CharacterBaseByMaster.Value.TryGetValue(characterMasterId, out var baseId) ? baseId : 0L;
}
