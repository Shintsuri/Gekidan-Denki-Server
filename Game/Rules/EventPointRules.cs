/// <summary>
/// 活动 PT 计算，按日服公开的官方公式（已用实测数据验证到个位数）：
///
/// PT = 100 × 基本報酬量 × スコア係数 × 編成ボーナス × ブースト報酬量倍率
///
/// 基本報酬量   = 楽曲のスタミナ消費量 / 20
/// スコア係数   = 0 ~ 299 万        : 1 + スコア / 600 万
///                300 万 ~ 1499 万  : 1.5 + (スコア - 300 万) / 6000 万
///                1500 万 ~         : 1.75 + (スコア - 1500 万) / 1 亿
/// 編成ボーナス = 1 + (活动角色 +25% + 活动属性 +25%，两者兼具 +50%)
/// ブースト倍率 = 1→5, 2→10, 3→14, 4→18, 5→21, 6→24, 7→26, 8→28, 9→29, 10→30
///
/// 验证样本：10,623,783 分、体力 34、编成 +175%、10 炊き
///   100 × 1.70 × 1.627 × 2.75 × 30 = 22,819.6 → 实际 22,819
/// </summary>
static class EventPointRules
{
    static readonly int[] BoostMultipliers = { 1, 5, 10, 14, 18, 21, 24, 26, 28, 29, 30 };

    public static int Boost(int magnification) =>
        magnification <= 0 ? 1 : BoostMultipliers[Math.Min(magnification, BoostMultipliers.Length - 1)];

    public static decimal ScoreFactor(long score) => score switch
    {
        < 3_000_000 => 1m + score / 6_000_000m,
        < 15_000_000 => 1.5m + (score - 3_000_000) / 60_000_000m,
        _ => 1.75m + (score - 15_000_000) / 100_000_000m
    };

    /// <summary>一局获得的活动 PT。bonus 是编成加成（0.25 = +25%，1.75 = +175%）。</summary>
    public static long Calculate(long score, int baseStamina, int effectiveMagnification, decimal bonus = 0m)
    {
        var baseReward = Math.Max(1, baseStamina) / 20m;
        var gain = 100m
            * baseReward
            * ScoreFactor(score)
            * (1m + Math.Max(0m, bonus))
            * Boost(effectiveMagnification);
        return Math.Max(1L, (long)Math.Floor(gain));
    }
}
