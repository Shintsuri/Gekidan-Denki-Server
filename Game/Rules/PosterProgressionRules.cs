using System.Text.Json;

static class PosterProgressionRules
{
    public sealed record Costume(int Phase, long Id);
    public sealed record Poster(long Id, int Rarity, long LevelPattern, int MaxPhase, int ReleaseGroup, Costume[] Costumes);
    public sealed record Cost(int Level, long ItemId, int Quantity);
    public static readonly IReadOnlyDictionary<long, Poster> Posters = GachaRules.ReadRows("Shop.PosterMaster.json")
        .Cast<object?[]>().ToDictionary(row => GachaRules.Long(row[0]), row => new Poster(
            GachaRules.Long(row[0]), (int)GachaRules.Long(row[3]), GachaRules.Long(row[5]), (int)GachaRules.Long(row[25]),
            // 上限解放材料组 = levelPatternGroupMasterId（与 PosterReleaseItemGroupMaster 的 id 一一对应）
            (int)GachaRules.Long(row[5]),
            ((object?[])row[16]!).Cast<object?[]>().Where(costume => costume[1] is not null)
                .Select(costume => new Costume((int)GachaRules.Long(costume[0]), GachaRules.Long(costume[1]))).ToArray()));
    public static readonly IReadOnlyDictionary<long, Cost[]> Patterns = ReadPatterns();

    // 上限解放（突破）消耗：PosterReleaseItemGroupMaster
    // 组 -> (阶段 -> (道具, 数量))
    public static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, (long ItemId, int Quantity)>> ReleaseGroups =
        new Dictionary<int, IReadOnlyDictionary<int, (long, int)>>
        {
            [1] = new Dictionary<int, (long, int)> { [0] = (130071, 2), [1] = (130071, 2), [2] = (130071, 2), [3] = (130071, 2) },
            [2] = new Dictionary<int, (long, int)> { [0] = (130071, 5), [1] = (130071, 5), [2] = (130071, 5), [3] = (130071, 5) },
            [3] = new Dictionary<int, (long, int)> { [0] = (130071, 20), [1] = (130071, 20), [2] = (130071, 20), [3] = (130071, 20) },
            [4] = new Dictionary<int, (long, int)> { [0] = (130072, 10), [1] = (130072, 25), [2] = (130072, 50), [3] = (130072, 120), [4] = (130072, 295) },
            [5] = new Dictionary<int, (long, int)> { [0] = (130071, 30), [1] = (130071, 35), [2] = (130071, 35), [3] = (130071, 40) },
            [6] = new Dictionary<int, (long, int)> { [0] = (130071, 30), [1] = (130071, 35), [2] = (130071, 35) },
        };

    static Dictionary<long, Cost[]> ReadPatterns()
    {
        using var stream = typeof(PosterProgressionRules).Assembly.GetManifestResourceStream("Progression.PosterLevelPatternGroupMaster.json")
            ?? throw new InvalidOperationException("缺少海报升级主数据");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToDictionary(row => row[0].GetInt64(),
            row => row[1].EnumerateArray().Select(cost => new Cost(cost[2].GetInt32(), cost[3].GetInt64(), cost[4].GetInt32())).ToArray());
    }

    // 与客户端 Poster.LevelLimit 一致，第五次及以后的突破每次增加两级。
    public static int LevelLimit(Poster poster, int phase) =>
        checked(poster.Rarity * 2 + (phase <= 4 ? phase : phase * 2 - 4));

    public static Dictionary<long, int> LevelCosts(Poster poster, int from, int to) => Patterns[poster.LevelPattern]
        .Where(cost => cost.Level >= from && cost.Level < to).GroupBy(cost => cost.ItemId)
        .ToDictionary(group => group.Key, group => checked(group.Sum(cost => cost.Quantity)));

    // 从 phase 突破到 phaseTo 的消耗（累加中间每个阶段）
    public static Dictionary<long, int> BreakthroughCosts(Poster poster, int phase, int phaseTo)
    {
        if (!ReleaseGroups.TryGetValue(poster.ReleaseGroup, out var table))
            return new Dictionary<long, int>();
        var result = new Dictionary<long, int>();
        for (var p = phase; p < phaseTo; p++)
        {
            if (!table.TryGetValue(p, out var cost))
                continue;
            result[cost.ItemId] = result.GetValueOrDefault(cost.ItemId) + cost.Quantity;
        }
        return result;
    }
}

static class PosterProgressionErrors
{
    public const string Invalid = "POSTER_INVALID_REQUEST";
    public const string NotOwned = "POSTER_NOT_OWNED";
    public const string LevelLimit = "POSTER_LEVEL_LIMIT";
    public const string InsufficientItems = "POSTER_INSUFFICIENT_ITEMS";
}
