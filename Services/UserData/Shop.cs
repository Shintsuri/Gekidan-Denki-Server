using Npgsql;

sealed partial class UserDataService
{
    public async Task<object?[]> UpdateShopLastViewedAtAsync(HttpContext context, object? payload)
    {
        var userId = await GetCurrentUserIdAsync(context);
        if (userId is null) return Array.Empty<object?>();

        var values = payload as object?[] ?? Array.Empty<object?>();
        var shopCategory = (int)(GetLong(values, 0) ?? 0);
        var shopMasterId = GetNullableLong(values, 1);

        // 校正：若客户端上报的商店不是当前开启活动的商店，改写成当前活动的商店
        // （否则客户端会一直去加载旧活动的 ShopExchangeTab 图，导致 7007 / 错误 70）
        try
        {
            var activePath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
            if (System.IO.File.Exists(activePath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(activePath));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetInt64(out var activeEventId)) continue;
                    if (shopMasterId is null || shopMasterId != activeEventId)
                    {
                        shopMasterId = activeEventId;
                    }
                    break;
                }
            }
        }
        catch { }

        var viewKey = $"{shopCategory}:{shopMasterId?.ToString() ?? ""}";

        await EnsureDefaultUserDataAsync(userId.Value);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into user_shop_last_views ("userId", shop_category, shop_master_id, view_key, last_viewed_at, updated_at)
            values ($1, $2, $3, $4, now(), now())
            on conflict ("userId", view_key)
            do update set shop_category = excluded.shop_category,
                          shop_master_id = excluded.shop_master_id,
                          last_viewed_at = now(),
                          updated_at = now()
            returning id, shop_master_id, last_viewed_at, shop_category
            """,
            connection);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(shopCategory);
        command.Parameters.AddWithValue((object?)shopMasterId ?? DBNull.Value);
        command.Parameters.AddWithValue(viewKey);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new object?[]
        {
            DataObject(65, new object?[]
            {
                reader.GetInt64(0),
                GetNullableInt64(reader, 1),
                reader.GetDateTime(2),
                reader.GetInt32(3)
            })
        };
    }
}
