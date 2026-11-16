using System.Globalization;

/// <summary>
/// 直接从 MasterMemory 主数据文件读取表数据。
/// 主数据文件结构：msgpack 头（表名 → [offset, length]）+ 紧接其后的表数据块；
/// 部分表（抽卡/Mission Pass/EventMaster 等）块体是 MsgPack.Ext(99) 包裹的 LZ4。
/// 用这套读取器可以在不改动主数据的前提下取得客户端真正看到的表（例如 StoryEventMaster 的活动起止时间）。
/// </summary>
static class MasterDataTableReader
{
    /// <summary>读取一张表的全部行；文件/表不存在时返回 null。</summary>
    public static object?[]? ReadTable(string dbPath, string tableName)
    {
        if (!File.Exists(dbPath)) return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dbPath);
        }
        catch (IOException)
        {
            return null;
        }

        if (MsgPack.DecodePrefix(bytes, out var headerLength) is not Dictionary<string, object?> header) return null;
        if (!header.TryGetValue(tableName, out var rangeObject) || rangeObject is not object?[] range || range.Length < 2) return null;

        var offset = checked((int)Convert.ToInt64(range[0], CultureInfo.InvariantCulture));
        var length = checked((int)Convert.ToInt64(range[1], CultureInfo.InvariantCulture));
        if (offset < 0 || length <= 0 || (long)headerLength + offset + length > bytes.Length) return null;

        var payload = MsgPack.Decode(bytes.AsSpan(headerLength + offset, length).ToArray());
        var unwrapped = MagicOnionLz4.Unwrap(payload, 64 * 1024 * 1024);
        return unwrapped as object?[];
    }

    /// <summary>
    /// 读活动窗口：返回 (eventId, start, end, forceEnd)，时间按“主数据里的本地时间”原样返回（Kind=Unspecified）。
    /// </summary>
    public static List<(long Id, DateTime? Start, DateTime? End, DateTime? ForceEnd)> ReadStoryEventWindows(string dbPath)
    {
        var list = new List<(long, DateTime?, DateTime?, DateTime?)>();
        var rows = ReadTable(dbPath, "StoryEventMaster");
        if (rows is null) return list;
        foreach (var row in rows)
        {
            if (row is not object?[] cells || cells.Length < 9) continue;
            var id = Convert.ToInt64(cells[0], CultureInfo.InvariantCulture);
            list.Add((id, AsLocalDate(cells[6]), AsLocalDate(cells[7]), AsLocalDate(cells[8])));
        }
        return list;
    }

    static DateTime? AsLocalDate(object? value)
    {
        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
            DateTimeOffset offset => DateTime.SpecifyKind(offset.DateTime, DateTimeKind.Unspecified),
            long seconds when seconds > 1500000000 => DateTime.SpecifyKind(
                DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, DateTimeKind.Unspecified),
            _ => null
        };
    }
}
