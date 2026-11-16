using Npgsql;

/// <summary>调试：输出某个队伍的编成加成明细（服务端口径），用于和客户端显示对齐。</summary>
sealed partial class UserDataService
{
    internal async Task<object?[]> DescribeEventBonusAsync(long userId, long eventId, long partyId = 1004001L)
    {
        await using var connection = await database.OpenConnectionAsync();
        var actors = new List<EventBonusRules.ActorEntry>();
        await using var command = new NpgsqlCommand(
            """
            select c.character_master_id, b.character_base_master_id
            from user_party_slots s
            join user_character_cards c on c.id = s.character_card_id and c."userId" = s."userId"
            join user_character_bases b on b.id = c.character_base_id and b."userId" = c."userId"
            where s."userId" = $1 and s.party_id = $2
            order by s.id
            limit 5
            """, connection);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(partyId);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                actors.Add(new EventBonusRules.ActorEntry(reader.GetInt64(1), reader.GetInt64(0)));
        }

        return new object?[]
        {
            EventBonusRules.Calculate(eventId, actors),
            EventBonusRules.Describe(eventId, actors)
        };
    }
}
