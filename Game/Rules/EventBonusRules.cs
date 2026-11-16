using System.Text.Json;

/// <summary>
/// 活动编成加成。三类来源：
///
/// 1) 剧团加成：StoryEventBonusCharacterBaseMaster 的 type 1，value 是 CompanyMaster 的剧团 id。
///    CharacterBaseMaster[row][9] 就是角色的剧团 id。命中 +25%/人。
/// 2) 角色加成：同表 type 2，value 是角色 base id。命中 +25%/人。
/// 3) 属性加成：StoryEventMaster[row][2] 是活动指定属性；
///    角色属性编码在 CharacterMaster[row][15] 的中间位（131→3、411→1、431→3）。
///    命中 +25%/人。双人卡第二人按 base 的属性表额外判定一次。
///
/// 5 人最多 +250%（剧团 125% + 属性 125%），客户端还会显示额外 +3.5%。
/// </summary>
static class EventBonusRules
{
    sealed record BonusRow(long EventMasterId, int Type, long Value);
    public readonly record struct ActorEntry(long BaseId, long CardMasterId);

    static readonly BonusRow[] BonusRows = LoadBonus();
    static readonly Dictionary<long, int> BaseCompanies = LoadBaseCompanies();
    static readonly Dictionary<long, int> BaseAttributes = LoadBaseAttributes();
    static readonly Dictionary<long, int> CardAttributes = LoadCardAttributes();
    static readonly Dictionary<long, long> SecondaryBases = LoadSecondaryBases();
    static readonly Dictionary<long, int> EventAttributes = LoadEventAttributes();

    static BonusRow[] LoadBonus()
    {
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("Event.StoryEventBonusCharacterBaseMaster.json")
            ?? throw new InvalidOperationException("缺少活动加成主数据");
        using var document = JsonDocument.Parse(stream);
        var rows = new List<BonusRow>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4) continue;
            rows.Add(new BonusRow(row[1].GetInt64(), row[2].GetInt32(), row[3].GetInt64()));
        }
        return rows.ToArray();
    }

    static Dictionary<long, int> LoadBaseCompanies()
    {
        var map = new Dictionary<long, int>();
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("InventoryMaster.CharacterBaseMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 10) continue;
            if (row[9].ValueKind != JsonValueKind.Number) continue;
            map[row[0].GetInt64()] = row[9].GetInt32();
        }
        return map;
    }

    /// <summary>角色属性：CharacterMaster[row][15] 是三位数，中间位即属性（131→3、411→1）。</summary>
    static int DecodeAttribute(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return 0;
        var code = value.GetInt32();
        if (code < 100 || code > 499) return 0;
        return (code / 10) % 10;
    }

    static Dictionary<long, int> LoadCardAttributes()
    {
        var map = new Dictionary<long, int>();
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("InventoryMaster.CharacterMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 16) continue;
            var attribute = DecodeAttribute(row[15]);
            if (attribute != 0) map[row[0].GetInt64()] = attribute;
        }
        return map;
    }

    static Dictionary<long, int> LoadBaseAttributes()
    {
        var map = new Dictionary<long, int>();
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("InventoryMaster.CharacterMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 16) continue;
            var baseId = row[1].GetInt64();
            if (map.ContainsKey(baseId)) continue;
            var attribute = DecodeAttribute(row[15]);
            if (attribute != 0) map[baseId] = attribute;
        }
        return map;
    }

    /// <summary>双人卡：CharacterMaster[row][25] 是第二人的 base id。</summary>
    static Dictionary<long, long> LoadSecondaryBases()
    {
        var map = new Dictionary<long, long>();
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("InventoryMaster.CharacterMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 26) continue;
            if (row[25].ValueKind != JsonValueKind.Number) continue;
            var secondary = row[25].GetInt64();
            if (secondary > 0) map[row[0].GetInt64()] = secondary;
        }
        return map;
    }

    static Dictionary<long, int> LoadEventAttributes()
    {
        var map = new Dictionary<long, int>();
        using var stream = typeof(EventBonusRules).Assembly
            .GetManifestResourceStream("Event.StoryEventMaster.json");
        if (stream is null) return map;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;
            if (row[2].ValueKind != JsonValueKind.Number) continue;
            map[row[0].GetInt64()] = row[2].GetInt32();
        }
        return map;
    }

    /// <summary>计算编成加成（0.25 = +25%）。</summary>
    public static decimal Calculate(long eventMasterId, IEnumerable<ActorEntry> actors)
    {
        var rows = BonusRows.Where(row => row.EventMasterId == eventMasterId).ToArray();
        var entries = actors.Where(a => a.BaseId > 0).Distinct().Take(5).ToArray();
        if (entries.Length == 0) return 0m;

        var bonus = 0m;
        var eventAttribute = EventAttributes.GetValueOrDefault(eventMasterId);

        foreach (var entry in entries)
        {
            var company = BaseCompanies.GetValueOrDefault(entry.BaseId);
            var cardAttribute = CardAttributes.GetValueOrDefault(entry.CardMasterId);

            // 主数据里配置的加成行（剧团 type 1 / 角色 type 2）
            foreach (var row in rows)
            {
                var hit = row.Type switch
                {
                    1 => company != 0 && company == row.Value,
                    2 => entry.BaseId == row.Value,
                    _ => false
                };
                if (hit) bonus += 0.25m;
            }

            // 属性加成（配置不在加成表里，按活动属性 + 卡片属性判定）
            if (eventAttribute != 0 && cardAttribute == eventAttribute) bonus += 0.25m;

            // 双人卡第二人：剧团、角色、属性都要单独再判一次
            if (SecondaryBases.TryGetValue(entry.CardMasterId, out var secondBase))
            {
                var secondCompany = BaseCompanies.GetValueOrDefault(secondBase);
                foreach (var row in rows)
                {
                    var hit = row.Type switch
                    {
                        1 => secondCompany != 0 && secondCompany == row.Value,
                        2 => secondBase == row.Value,
                        _ => false
                    };
                    if (hit) bonus += 0.25m;
                }
                if (eventAttribute != 0 && BaseAttributes.GetValueOrDefault(secondBase) == eventAttribute)
                    bonus += 0.25m;
            }
        }
        return bonus;
    }
}
