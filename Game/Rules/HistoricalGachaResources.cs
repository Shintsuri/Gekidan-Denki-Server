using System.Text.Json;

static class HistoricalGachaResources
{
    public static readonly long[] Ids = Load();
    static long[] Load()
    {
        using var stream = typeof(HistoricalGachaResources).Assembly.GetManifestResourceStream("GameData.historical-gachas.json")
            ?? throw new InvalidOperationException("缺少已核验往期卡池目录");
        return JsonSerializer.Deserialize<long[]>(stream) ?? throw new InvalidDataException("往期卡池目录为空");
    }
    public static bool Available(GachaRules.Pool pool, string resourcesRoot)
    {
        if (!pool.Historical) return true;
        var paths = new List<string> {
            $"gachas_assets_gachas/background_{pool.Id}.bundle",
            $"gachas_assets_gachas/logo_{pool.Id}.bundle",
            $"shopexchangetab_assets_shopexchangetab/{pool.ExchangeBanner}.bundle",
            $"Textures/Banners/Gacha/{pool.Id}.astc.gz"
        };
        foreach (var entry in pool.Entries)
        {
            if (entry.Type == 3) paths.Add($"posters_assets_posters/{entry.MasterId}.bundle");
            else
            {
                var card = Cards.Value[entry.MasterId];
                var asset = card[4] is string named ? named : entry.MasterId.ToString();
                for (var phase = 0; phase < (entry.Rarity >= 4 ? 2 : 1); phase++)
                    paths.Add($"charactercards_assets_charactercard/{asset}_{phase}.bundle");
            }
        }
        // 资源可能分布在多个根目录（assets / static/Resources / root），逐个尝试
        var roots = new[]
        {
            resourcesRoot,
            System.IO.Path.GetFullPath(System.IO.Path.Combine(resourcesRoot, "..", "static", "Resources")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(resourcesRoot, "..", "root")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(resourcesRoot, "static-assets", "Resources")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(resourcesRoot, "..", "assets"))
        };
        return paths.All(path => roots.Any(root => File.Exists(System.IO.Path.Combine(root, path))));
    }
    static readonly Lazy<Dictionary<long,object?[]>> Cards = new(() =>
        GachaRules.ReadRows("InventoryMaster.CharacterMaster.json").Cast<object?[]>().ToDictionary(row => GachaRules.Long(row[0])));
}
