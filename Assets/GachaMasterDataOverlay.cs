using System.Security.Cryptography;
using System.Buffers.Binary;

static class GachaMasterDataOverlay
{
    // 发布标记也递增，兼容只比较发布时间的客户端缓存。
    const long PublishRevision = 5;
    static readonly object Gate = new();
    static readonly Dictionary<string,MasterDataVersionInfo> Cache = new();
    public static MasterDataVersionInfo Apply(MasterDataVersionInfo source, GameOperationsSnapshot? operations = null)
    {
        operations ??= new(0, GameOperationsConfiguration.Default());
        var path=Path.GetFullPath(source.LocalFilePath);
        var info=new FileInfo(path);
        if(!info.Exists)throw new FileNotFoundException("统一奖池需要有效的原始 MasterMemory 文件，请检查 MASTER_DATA_FILE",path);
        var flags=string.Join(",",operations.Configuration.Gachas.Where(pair=>pair.Value).Select(pair=>pair.Key).Order());
        var key=$"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{source.Version}|{source.PublishTimestamp}|{operations.Revision}|{flags}|{EventDateOverlay.CacheTag()}";
        lock(Gate)
        {
            if(Cache.TryGetValue(key,out var result))return result;
            var bytes=Build(File.ReadAllBytes(path), operations.Configuration);
            var hash=Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
            var publishTimestamp=checked(source.PublishTimestamp+PublishRevision+operations.Revision);
            var filename=$"mastermemory_{publishTimestamp}_{hash}.db";
            // 生成文件必须落在源主数据的同目录：客户端按 master-data/production/{版本目录}/文件名 取，
            // 放到子目录会 404，导致「初期化更新失败」。
            var folder=Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            var output=Path.Combine(folder,filename);
            if(!File.Exists(output))
            {
                var temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
                File.WriteAllBytes(temp,bytes);File.Move(temp,output,true);
            }
            // 版本号必须与生成的文件名严格对应：客户端会用 mastermemory_{版本}.db 拼下载地址，
            // 旧实现把版本写成 "{原版本}-{哈希}-r{修订}"，与磁盘文件名不一致，会导致主数据加载失败。
            var version=$"{publishTimestamp}_{hash}";
            result=source with {Version=version,PublishTimestamp=publishTimestamp,
                RemotePath=source.RemotePath[..(source.RemotePath.LastIndexOf('/')+1)]+filename,LocalFilePath=output};
            Cache.Clear();Cache.Add(key,result);return result;
        }
    }
    internal static byte[] Build(byte[] original, GameOperationsConfiguration? configuration = null)
    {
        configuration ??= GameOperationsConfiguration.Default();
        var header=MsgPack.DecodePrefix(original,out var headerLength) as Dictionary<string,object?>
            ?? throw new InvalidDataException("MasterMemory 索引不是字典");
        using var body=new MemoryStream();
        var output=new Dictionary<string,object?>();
        foreach(var pair in header)
        {
            var range=(object?[])pair.Value!;
            var offset=checked((int)GachaRules.Long(range[0]));
            var length=checked((int)GachaRules.Long(range[1]));
            if(offset<0 || length<=0 || (long)headerLength+offset+length>original.Length)
                throw new InvalidDataException("MasterMemory 表索引越界");
            var bytes=original.AsSpan(headerLength+offset,length).ToArray();
            if(pair.Key is "GachaMaster" or "ExchangeShopMaster" or "ItemMaster" or "MissionPassMaster" or "MissionPassDetailMaster" or "MissionPassLoopRewardMaster" or "EventMaster" or "JewelShopCategoryMaster")
            {
                var rows=MagicOnionLz4.Unwrap(MsgPack.Decode(bytes),16*1024*1024) as object?[]
                    ?? throw new InvalidDataException("MasterMemory 表不是数组");
                rows=pair.Key switch {
                    "GachaMaster"=>GachaRules.MasterGachas(rows).Cast<object?[]>().Where(row=>configuration.Gachas[GachaRules.Long(row[0])]).Cast<object?>().ToArray(),
                    "ExchangeShopMaster"=>GachaRules.MasterShops(rows).Cast<object?[]>().Where(row=>GachaRules.Long(row[2])!=4 || configuration.Gachas[GachaRules.Long(row[0])]).Cast<object?>().ToArray(),
                    "ItemMaster"=>GachaRules.MasterItems(rows),
                    "MissionPassMaster"=>MissionPassRules.MasterPasses(rows,configuration.MissionPass),
                    "MissionPassDetailMaster"=>MissionPassRules.MasterStages(rows,configuration.MissionPass),
                    "EventMaster"=>EventRows(rows),
                    "JewelShopCategoryMaster"=>JewelShopMasterOverlay.Categories(rows),
                    _=>MissionPassRules.MasterLoops(rows,configuration.MissionPass)};
                // MasterMemory 使用单块 LZ4 扩展 99，保持客户端现有读取约定。
                var raw=MsgPack.Encode(rows);
                var size=new byte[5];size[0]=0xd2;BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(1),raw.Length);
                bytes=MsgPack.Encode(new MsgPack.Ext(99,size.Concat(MagicOnionLz4.CompressLiteralBlock(raw)).ToArray()));
            }
            output.Add(pair.Key,new object?[] {checked((int)body.Length),bytes.Length});body.Write(bytes);
        }
        if(!new[] {"GachaMaster","ExchangeShopMaster","ItemMaster","MissionPassMaster","MissionPassDetailMaster","MissionPassLoopRewardMaster"}.All(output.ContainsKey))
            throw new InvalidDataException("MasterMemory 缺少抽卡或 Mission Pass 所需表");
        return MsgPack.Encode(output).Concat(body.ToArray()).ToArray();
    }

    // 活动日期补丁：只改已结束的活动，其余行原样保留
    static object?[] EventRows(object?[] rows)
    {
        var patched=EventDateOverlay.PatchRows(rows);
        Console.WriteLine($"[event-overlay] patched {patched} events within gacha overlay");
        return rows;
    }
}
