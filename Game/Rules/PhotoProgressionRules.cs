using System.Text.Json;

/// <summary>
/// 照片强化规则：PhotoLevelUpItemGroupMaster 每行是
/// [Id, Group(稀有度), ToLevel, Exp, [[ItemMasterId, Quantity], ...]]
/// </summary>
static class PhotoProgressionRules
{
    public sealed record Material(long ItemMasterId, int Quantity);
    public sealed record Step(long Id, int Group, int ToLevel, long Exp, Material[] Materials);

    public static readonly IReadOnlyDictionary<(int Group, int ToLevel), Step> Steps = Load();

    static IReadOnlyDictionary<(int Group, int ToLevel), Step> Load()
    {
        using var stream = typeof(PhotoProgressionRules).Assembly
            .GetManifestResourceStream("Photo.PhotoLevelUpItemGroupMaster.json")
            ?? throw new InvalidOperationException("缺少照片强化主数据");
        using var document = JsonDocument.Parse(stream);
        var steps = new Dictionary<(int, int), Step>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5) continue;
            var id = row[0].GetInt64();
            var group = row[1].GetInt32();
            var toLevel = row[2].GetInt32();
            var exp = row[3].GetInt64();
            var materials = row[4].EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2)
                .Select(m => new Material(m[0].GetInt64(), m[1].GetInt32()))
                .ToArray();
            steps[(group, toLevel)] = new Step(id, group, toLevel, exp, materials);
        }
        return steps;
    }

    public static Step? Next(int group, int currentLevel) =>
        Steps.TryGetValue((group, currentLevel + 1), out var step) ? step : null;
}
