using System.Buffers.Binary;
using System.Text.Json;

/// <summary>
/// 活动日期补丁：把主数据里已结束的活动改成「现在开放」，或把指定活动强制关闭。
/// 不单独生成版本，而是由 GachaMasterDataOverlay 在重建 EventMaster 时调用，
/// 保证主数据版本串只有一层后缀（多一层会导致客户端 APP002 / UnsupportedAssetVersion）。
/// </summary>
static class EventDateOverlay
{
    /// <summary>Disabled：被单独关闭的活动 Id 列表；Revision：每次修改自增，用于让主数据缓存失效。</summary>
    public sealed record Options(bool Enabled, int Days, string GeneratedAt, long[]? Disabled = null, int Revision = 0);

    public static string OptionsPath => Path.Combine(AppContext.BaseDirectory, "event-overlay.json");

    public static Options ReadOptions()
    {
        try
        {
            if (File.Exists(OptionsPath))
            {
                var options = JsonSerializer.Deserialize<Options>(File.ReadAllText(OptionsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (options is not null) return options;
            }
        }
        catch
        {
            // 配置坏了就当关闭
        }
        return new Options(false, 30, "");
    }

    public static void WriteOptions(Options options)
    {
        File.WriteAllText(OptionsPath, JsonSerializer.Serialize(options,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>补丁标识，用于主数据缓存键</summary>
    public static string CacheTag()
    {
        var options = ReadOptions();
        var ev = options.Enabled ? $"ev{Math.Clamp(options.Days, 1, 365)}" : "ev0";
        var disabled = options.Disabled is { Length: > 0 } ? $"d{options.Disabled.Length}" : "d0";
        return $"{ev}-{disabled}-r{options.Revision}";
    }

    /// <summary>把 EventMaster 的已结束活动改成现在开放，返回打补丁的行数</summary>
    public static int PatchRows(object?[] rows)
    {
        var options = ReadOptions();
        var disabled = options.Disabled ?? Array.Empty<long>();
        var now = DateTimeOffset.UtcNow;
        var patched = 0;

        // 关闭的活动：无论总开关如何，都强制改成过去
        var closedStart = now.UtcDateTime.AddDays(-30);
        var closedEnd = now.UtcDateTime.AddDays(-1);

        foreach (var item in rows)
        {
            if (item is not object?[] row || row.Length < 5) continue;
            var id = AsLong(row[0]);
            if (id is not null && Array.IndexOf(disabled, id.Value) >= 0)
            {
                row[2] = closedStart;
                row[3] = closedEnd;
                row[4] = closedEnd;
                patched++;
                continue;
            }
            if (!options.Enabled) continue;
            var end = AsDate(row[3]);
            var force = AsDate(row[4]);
            var deadline = force ?? end;
            if (deadline is null || deadline.Value >= now) continue;
            row[2] = now.UtcDateTime.AddHours(-1);
            row[3] = now.UtcDateTime.AddDays(Math.Clamp(options.Days, 1, 365));
            row[4] = now.UtcDateTime.AddDays(Math.Clamp(options.Days, 1, 365) + 1);
            patched++;
        }
        return patched;
    }

    public static bool IsDisabled(long id)
    {
        var options = ReadOptions();
        return options.Disabled is { Length: > 0 } list && Array.IndexOf(list, id) >= 0;
    }

    /// <summary>切换单个活动的开关，返回是否成功</summary>
    public static bool SetDisabled(long id, bool disabled)
    {
        var options = ReadOptions();
        var list = (options.Disabled ?? Array.Empty<long>()).ToList();
        var has = list.Contains(id);
        if (disabled && !has) list.Add(id);
        if (!disabled && has) list.Remove(id);
        if (has == disabled) return true;
        WriteOptions(options with
        {
            Disabled = list.OrderBy(x => x).ToArray(),
            Revision = options.Revision + 1,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        return true;
    }

    static long? AsLong(object? value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            _ => null
        };
    }

    static DateTimeOffset? AsDate(object? value)
    {
        return value switch
        {
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime),
            DateTimeOffset offset => offset,
            MsgPack.Ext ext when ext.Code == -1 => DecodeTimestamp(ext.Bytes),
            long seconds => DateTimeOffset.FromUnixTimeSeconds(seconds),
            _ => null
        };
    }

    static DateTimeOffset DecodeTimestamp(byte[] bytes)
    {
        if (bytes.Length == 4)
            return DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(bytes));
        if (bytes.Length == 8)
        {
            var seconds = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
            var nanos = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
        }
        if (bytes.Length == 12)
        {
            var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
            var nanos = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
        }
        return DateTimeOffset.UnixEpoch;
    }
}
