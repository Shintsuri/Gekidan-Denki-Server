using System.Buffers.Binary;

/// <summary>
/// 宝石商店主数据补丁：
/// 1) 解锁全部分类（客户端按 IsDisplayLocking 过滤，未解锁时「お得パス」等标签不显示）
/// 2) 生日礼包去掉上架期间限制，让所有角色的生日礼包常驻
/// </summary>
static class JewelShopMasterOverlay
{
    // JewelShopCategoryMaster: [Id, Name, LayoutType, IsDisplayLocking, BannerPath, Order]
    public static object?[] Categories(object?[] rows)
    {
        var patched = 0;
        foreach (var row in rows)
        {
            if (row is not object?[] cells || cells.Length <= 3) continue;
            if (cells[3] is bool flag && !flag) continue;
            cells[3] = false;   // 全部解锁显示
            patched++;
        }
        Console.WriteLine($"[jewel-overlay] unlocked categories={patched}");
        return rows;
    }

    // JewelShopItemMaster 关键下标：
    //   6  m_jewel_shop_category_id
    //  20  is_display_locking
    //  23  start_date
    //  24  end_date
    const int CategoryIndex = 6;
    const int DisplayLockingIndex = 20;
    const int StartDateIndex = 23;
    const int EndDateIndex = 24;
    const long BirthdayCategoryId = 8;

    public static object?[] Items(object?[] rows)
    {
        var patched = 0;
        foreach (var row in rows)
        {
            if (row is not object?[] cells || cells.Length <= EndDateIndex) continue;
            if (cells[CategoryIndex] is null || ToLong(cells[CategoryIndex]) != BirthdayCategoryId) continue;
            cells[DisplayLockingIndex] = false;
            cells[StartDateIndex] = null;
            cells[EndDateIndex] = null;
            patched++;
        }
        Console.WriteLine($"[jewel-overlay] birthday packs unlocked={patched}");
        return rows;
    }

    static long ToLong(object? value) => value switch
    {
        byte b => b,
        sbyte b => b,
        short n => n,
        ushort n => n,
        int n => n,
        uint n => n,
        long n => n,
        ulong n => (long)n,
        _ => long.MinValue
    };
}
