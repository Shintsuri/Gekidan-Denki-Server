using Npgsql;

// 编队进阶：位置调整 / 多队 / 三人队基础。之前这些路由都直接返回 Ok()，什么都没保存。
sealed partial class UserDataService
{
    // /Parties/{partyId}/EditPosition：把每个槽位的 position 写回 user_party_slots。
    // 客户端 payload 通常是 [[slotNumber, position], ...]，也兼容 [slotNumber, position]。
    public async Task<object?[]> EditPartyPositionAsync(HttpContext context, long partyId, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var pairs = ReadPairs(payload);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (slot, position) in pairs)
        {
            await ExecuteAsync(connection, transaction,
                """
                update user_party_slots set position = $4, updated_at = now()
                where "userId" = $1 and party_id = $2 and slot_number = $3
                """, userId, partyId, slot, position);
        }
        await transaction.CommitAsync();
        return await ReadPartyScreenPresentAsync(connection, userId, partyId);
    }

    // /Parties/{partyId}/SetMultiParty：切换编队的多队模式（party_type）。
    public async Task<object?[]> SetMultiPartyAsync(HttpContext context, long partyId, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var partyType = ReadFirstInt(payload, 1);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "update user_party_groups set party_type = $3, updated_at = now() where \"userId\" = $1 and id = $2",
            userId, partyId, partyType);
        await transaction.CommitAsync();
        return await ReadPartyScreenPresentAsync(connection, userId, partyId);
    }

    // 三人队编辑：把 payload 里的槽位成员写回 user_party_slots。
    public async Task<object?[]> EditTripleCastPartyRealAsync(HttpContext context, object? payload)
    {
        var userId = await RequireAuthenticatedUserAsync(context);
        var slots = ReadSlots(payload);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var partyId = 0L;
        foreach (var slot in slots)
        {
            partyId = slot.PartyId > 0 ? slot.PartyId : partyId;
            await ExecuteAsync(connection, transaction,
                """
                update user_party_slots
                set character_card_id = $4, accessory_id = $5, poster_id = $6, updated_at = now()
                where "userId" = $1 and party_id = $2 and slot_number = $3
                """, userId, slot.PartyId, slot.SlotNumber, slot.CardId, slot.AccessoryId, slot.PosterId);
        }
        await transaction.CommitAsync();
        return await ReadPartyScreenPresentAsync(connection, userId, partyId);
    }

    async Task<object?[]> ReadPartyScreenPresentAsync(NpgsqlConnection connection, long userId, long partyId)
    {
        var present = new List<object?>();
        foreach (var row in await ReadPartiesAsync(connection, userId))
            present.Add(DataObject(6, row));
        foreach (var row in await ReadPartySlotsAsync(connection, userId))
            present.Add(DataObject(7, row));
        return present.ToArray();
    }

    // ---- payload 解析（客户端结构不完全统一，尽量宽松） ----
    static List<(int Slot, int Position)> ReadPairs(object? payload)
    {
        var result = new List<(int, int)>();
        void Walk(object? node)
        {
            if (node is not object?[] array) return;
            var numbers = array.Where(v => v is int or long).Select(Convert.ToInt32).ToArray();
            if (numbers.Length >= 2)
            {
                result.Add((numbers[0], numbers[1]));
                return;
            }
            foreach (var child in array) Walk(child);
        }
        Walk(payload);
        return result;
    }

    static int ReadFirstInt(object? payload, int fallback)
    {
        void Walk(object? node, ref int found)
        {
            switch (node)
            {
                case int i when found == 0: found = i; break;
                case long l when found == 0: found = (int)l; break;
                case object?[] array: foreach (var child in array) Walk(child, ref found); break;
            }
        }
        var value = 0;
        Walk(payload, ref value);
        return value == 0 ? fallback : value;
    }

    static (long PartyId, long NotationId) ReadPartyAndNotation(object? payload)
    {
        long partyId = 0, notationId = 0;
        void Walk(object? node)
        {
            if (node is not object?[] array) return;
            foreach (var child in array)
            {
                if (child is int i && partyId == 0) { partyId = i; continue; }
                if (child is long l)
                {
                    if (partyId == 0) partyId = l;
                    else if (notationId == 0) notationId = l;
                    continue;
                }
                Walk(child);
            }
        }
        Walk(payload);
        return (partyId, notationId);
    }

    static List<(long PartyId, int SlotNumber, long? CardId, long? AccessoryId, long? PosterId)> ReadSlots(object? payload)
    {
        var slots = new List<(long, int, long?, long?, long?)>();
        void Walk(object? node)
        {
            if (node is not object?[] array) return;
            var numbers = array.Where(v => v is int or long).Select(v => Convert.ToInt64(v)).ToArray();
            if (numbers.Length >= 2)
            {
                // [partyId, slotNumber, cardId?, accessoryId?, posterId?]
                var party = numbers[0];
                var slot = (int)numbers[1];
                long? card = numbers.Length > 2 && numbers[2] > 0 ? numbers[2] : null;
                long? accessory = numbers.Length > 3 && numbers[3] > 0 ? numbers[3] : null;
                long? poster = numbers.Length > 4 && numbers[4] > 0 ? numbers[4] : null;
                slots.Add((party, slot, card, accessory, poster));
                return;
            }
            foreach (var child in array) Walk(child);
        }
        Walk(payload);
        return slots;
    }
}
