static class HomeDisplayPreferenceRules
{
    public const string Invalid = "HOME_DISPLAY_PREFERENCE_INVALID";

    public static long Costume(GameInventoryCatalog catalog, long characterBaseId, long? costumeId)
    {
        // 兼容性优先：官方衣装表比本地主数据大得多，未知角色/衣装按原值接受，
        // 只要不是明显非法（负数）就不拒绝，避免客户端整段设置保存失败。
        if (!catalog.Bases.TryGetValue(characterBaseId, out var character))
            return costumeId is > 0 ? costumeId.Value : 0;
        var selected = costumeId ?? character.DefaultCostume;
        if (!catalog.CanWearCostume(selected, characterBaseId))
            return selected > 0 ? selected : character.DefaultCostume;
        return selected;
    }
}
