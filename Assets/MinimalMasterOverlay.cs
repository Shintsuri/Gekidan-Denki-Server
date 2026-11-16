using System.Security.Cryptography;
using System.Buffers.Binary;

/// <summary>
/// 最小化主数据补丁：除 JewelShopCategoryMaster 外，所有表按原字节原样搬运。
/// 不做任何抽卡/道具过滤，避免客户端因为表内容变化而「初期化更新失败」。
/// </summary>
static class MinimalMasterOverlay
{
    const long PublishDelta = 7;
    static readonly object Gate = new();
    static readonly Dictionary<string, MasterDataVersionInfo> Cache = new();

    public static MasterDataVersionInfo Apply(MasterDataVersionInfo source)
    {
        var path = Path.GetFullPath(source.LocalFilePath);
        var info = new FileInfo(path);
        if (!info.Exists) return source;

        var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{source.Version}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var bytes = Build(File.ReadAllBytes(path));
            if (bytes is null) return source;   // 没有需要补丁的表就原样返回

            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
            var publishTimestamp = checked(source.PublishTimestamp + PublishDelta);
            var filename = $"mastermemory_{publishTimestamp}_{hash}.db";
            var folder = Path.GetDirectoryName(path)!;
            var output = Path.Combine(folder, filename);
            if (!File.Exists(output))
            {
                var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, output, true);
            }

            var version = $"{publishTimestamp}_{hash}";
            var result = source with
            {
                Version = version,
                PublishTimestamp = publishTimestamp,
                RemotePath = source.RemotePath[..(source.RemotePath.LastIndexOf('/') + 1)] + filename,
                LocalFilePath = output
            };
            Cache.Clear();
            Cache.Add(key, result);
            Console.WriteLine($"[minimal-overlay] generated {filename}");
            return result;
        }
    }

    static byte[]? Build(byte[] original)
    {
        var header = MsgPack.DecodePrefix(original, out var headerLength) as Dictionary<string, object?>
            ?? throw new InvalidDataException("MasterMemory 索引不是字典");

        var patched = 0;
        using var body = new MemoryStream();
        var output = new Dictionary<string, object?>();

        foreach (var pair in header)
        {
            var range = (object?[])pair.Value!;
            var offset = checked((int)GachaRules.Long(range[0]));
            var length = checked((int)GachaRules.Long(range[1]));
            if (offset < 0 || length <= 0 || (long)headerLength + offset + length > original.Length)
                throw new InvalidDataException("MasterMemory 表索引越界");

            var bytes = original.AsSpan(headerLength + offset, length).ToArray();

            if (pair.Key is "JewelShopCategoryMaster" or "JewelShopItemMaster")
            {
                // 只改分类解锁标记（bool），不动 JewelShopItemMaster 的日期字段：
                // 生日礼包那套 DateTime 改写会让客户端初期化失败，先不碰。
                if (pair.Key == "JewelShopItemMaster") continue;
                var rows = MagicOnionLz4.Unwrap(MsgPack.Decode(bytes), 16 * 1024 * 1024) as object?[];
                if (rows is null || rows.Length == 0)
                {
                    Console.WriteLine($"[minimal-overlay] {pair.Key} 解包失败，保持原样");
                }
                else
                {
                    var changed = 0;
                    var isItem = pair.Key == "JewelShopItemMaster";
                    foreach (var row in rows)
                    {
                        if (row is not object?[] cells) continue;
                        if (isItem)
                        {
                            // 生日礼包（分类 8）常驻：日期设为有效区间，不能用 null
                            if (cells.Length <= 24) continue;
                            if (cells[6] is null || Convert.ToInt64(cells[6]) != 8) continue;
                            cells[20] = false;
                            cells[23] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                            cells[24] = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                            changed++;
                            continue;
                        }
                        if (cells.Length <= 3) continue;
                        if (cells[3] is bool flag && !flag) continue;
                        cells[3] = false;   // IsDisplayLocking -> false
                        changed++;
                    }
                    var raw = MsgPack.Encode(rows);
                    var size = new byte[5];
                    size[0] = 0xd2;
                    BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(1), raw.Length);
                    bytes = MsgPack.Encode(new MsgPack.Ext(99,
                        size.Concat(MagicOnionLz4.CompressLiteralBlock(raw)).ToArray()));
                    patched = changed;
                    Console.WriteLine($"[minimal-overlay] {pair.Key} patched={changed}");
                }
            }

            output.Add(pair.Key, new object?[] { checked((int)body.Length), bytes.Length });
            body.Write(bytes);
        }

        if (patched == 0) return null;
        return MsgPack.Encode(output).Concat(body.ToArray()).ToArray();
    }
}
