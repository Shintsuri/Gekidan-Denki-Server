using System.Collections.Concurrent;

/// <summary>
/// GM 从外部改数据库时，客户端内存里还是旧值。客户端本身没有轮询/推送刷新机制，
/// 但它每次发 API 请求时都会处理响应里的 present（DataObject 列表）并更新内存。
/// 所以这里挂一个「补发队列」：GM 改完把最新的 DataObject 记进来，
/// 该用户下一次任意 API 请求的响应里顺带把这些 DataObject 带上，客户端就会刷新。
/// </summary>
static class GmPushQueue
{
    static readonly ConcurrentDictionary<long, ConcurrentQueue<object?[]>> Pending = new();
    static readonly ConcurrentDictionary<string, (long UserId, DateTime At)> TokenCache = new();

    public static void Enqueue(long userId, params object?[][] dataObjects)
    {
        if (dataObjects.Length == 0) return;
        var queue = Pending.GetOrAdd(userId, _ => new ConcurrentQueue<object?[]>());
        foreach (var item in dataObjects) queue.Enqueue(item);
    }

    public static object?[] Drain(long userId)
    {
        if (!Pending.TryRemove(userId, out var queue)) return Array.Empty<object?>();
        var list = new List<object?>();
        while (queue.TryDequeue(out var item)) list.Add(item);
        return list.ToArray();
    }

    public static bool Has(long userId) => Pending.ContainsKey(userId);

    public static long? TryGetCachedUserId(string tokenHash)
        => TokenCache.TryGetValue(tokenHash, out var entry) && entry.At > DateTime.UtcNow.AddMinutes(-5) ? entry.UserId : null;

    public static void CacheUserId(string tokenHash, long userId)
        => TokenCache[tokenHash] = (userId, DateTime.UtcNow);
}
