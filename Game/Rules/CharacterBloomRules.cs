using System.Text.Json;

/// <summary>
/// 「才能開花」消耗规则：按 (稀有度, 段阶) 查 <c>CharacterBloomItemMaster</c>。
/// 客户端界面上显示的「必要 N」就是这个表里的数量（1★段阶0 = 2 个ジュゴン像）。
/// </summary>
static class CharacterBloomRules
{
    public readonly record struct Cost(int Phase, long ItemMasterId, int Quantity);

    static readonly JsonElement[] CharacterRows = Read("InventoryMaster.CharacterMaster.json");
    static readonly Dictionary<long, int> Rarities = CharacterRows.ToDictionary(
        r => r[0].GetInt64(), r => r[5].GetInt32());

    // [rarity, phase, quantity, kind, itemMasterId | null, altItemMasterId | null, altQuantity | null]
    static readonly JsonElement[] BloomRows = Read("Progression.CharacterBloomItemMaster.json");

    public static Cost? Required(long characterMasterId, int phase)
    {
        if (!Rarities.TryGetValue(characterMasterId, out var rarity))
            throw new BadHttpRequestException(CharacterAwakeningErrors.Unsupported, StatusCodes.Status409Conflict);

        foreach (var row in BloomRows)
        {
            if (row[0].GetInt32() != rarity || row[1].GetInt32() != phase) continue;
            // kind=2 的行没有主道具，改用备用道具（最终段阶专用）
            if (row[4].ValueKind != JsonValueKind.Null)
                return new Cost(phase, row[4].GetInt64(), row[2].GetInt32());
            if (row[5].ValueKind != JsonValueKind.Null)
                return new Cost(phase, row[5].GetInt64(), row[6].GetInt32());
            return null;
        }
        // 没有该段阶的消耗行 = 已到最高段阶
        return null;
    }

    public static int MaxPhase(long characterMasterId)
    {
        if (!Rarities.TryGetValue(characterMasterId, out var rarity)) return 0;
        var max = -1;
        foreach (var row in BloomRows)
            if (row[0].GetInt32() == rarity && row[1].GetInt32() > max)
                max = row[1].GetInt32();
        return max;
    }

    static JsonElement[] Read(string name)
    {
        using var stream = typeof(CharacterBloomRules).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少才能開花主数据：{name}");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
    }
}
