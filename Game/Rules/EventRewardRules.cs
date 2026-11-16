using System.Text.Json;

/// <summary>
/// 活动奖励主数据（累计 PT 奖励 / 排名奖励 / 奖励礼包）。
/// 字段顺序严格按客户端类：
///   StoryEventTotalPointRewardMaster = [Id, EventId, RequiredTotalPoint, ThingType, ThingId, ThingQuantity]
///   StoryEventRewardMaster           = [Id, EventId, TargetMinRank, TargetMaxRank, RewardItemPackageId]
///   StoryEventRewardItemPackage      = [Id, [[ThingId, ThingType, ThingQuantity], ...]]
/// </summary>
static class EventRewardRules
{
    public sealed record TotalPointReward(long Id, long EventId, int RequiredPoint, int ThingType, long ThingId, int Quantity);

    public sealed record RankReward(long Id, long EventId, int MinRank, int MaxRank, long PackageId);

    public sealed record RewardThing(long ThingId, int ThingType, int Quantity);

    static readonly Lazy<IReadOnlyList<TotalPointReward>> TotalPointRows = new(LoadTotalPointRewards);
    static readonly Lazy<IReadOnlyList<RankReward>> RankRows = new(LoadRankRewards);
    static readonly Lazy<IReadOnlyDictionary<long, IReadOnlyList<RewardThing>>> Packages = new(LoadPackages);

    public static IReadOnlyList<TotalPointReward> TotalPointRewards(long eventId) =>
        TotalPointRows.Value.Where(row => row.EventId == eventId)
            .OrderBy(row => row.RequiredPoint)
            .ThenBy(row => row.Id)
            .ToArray();

    public static RankReward? RankRewardFor(long eventId, int rank) =>
        RankRows.Value.FirstOrDefault(row => row.EventId == eventId && rank >= row.MinRank && rank <= row.MaxRank);

    public static IReadOnlyList<RewardThing> PackageRewards(long packageId) =>
        Packages.Value.TryGetValue(packageId, out var list) ? list : Array.Empty<RewardThing>();

    static IReadOnlyList<TotalPointReward> LoadTotalPointRewards()
    {
        var list = new List<TotalPointReward>();
        foreach (var row in Rows("Event.StoryEventTotalPointRewardMaster.json"))
        {
            if (row.Length < 6) continue;
            list.Add(new TotalPointReward(
                Long(row[0]), Long(row[1]), (int)Long(row[2]), (int)Long(row[3]), Long(row[4]), (int)Long(row[5])));
        }
        return list;
    }

    static IReadOnlyList<RankReward> LoadRankRewards()
    {
        var list = new List<RankReward>();
        foreach (var row in Rows("Event.StoryEventRewardMaster.json"))
        {
            if (row.Length < 5) continue;
            list.Add(new RankReward(
                Long(row[0]), Long(row[1]), (int)Long(row[2]), (int)Long(row[3]), Long(row[4])));
        }
        return list;
    }

    static IReadOnlyDictionary<long, IReadOnlyList<RewardThing>> LoadPackages()
    {
        var map = new Dictionary<long, IReadOnlyList<RewardThing>>();
        foreach (var row in Rows("Event.StoryEventRewardItemPackage.json"))
        {
            if (row.Length < 2 || row[1].ValueKind != JsonValueKind.Array) continue;
            var rewards = row[1];
            var things = new List<RewardThing>();
            foreach (var thing in rewards.EnumerateArray())
            {
                if (thing.ValueKind != JsonValueKind.Array || thing.GetArrayLength() < 3) continue;
                things.Add(new RewardThing(
                    thing[0].TryGetInt64(out var thingId) ? thingId : 0,
                    thing[1].TryGetInt32(out var thingType) ? thingType : 0,
                    thing[2].TryGetInt32(out var quantity) ? quantity : 0));
            }
            map[Long(row[0])] = things;
        }
        return map;
    }

    static IEnumerable<JsonElement[]> Rows(string resource)
    {
        using var stream = typeof(EventRewardRules).Assembly.GetManifestResourceStream(resource);
        if (stream is null) yield break;
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            yield return row.EnumerateArray().ToArray();
        }
    }

    static long Long(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)) return parsed;
        return 0;
    }
}
