using Npgsql;

sealed class MasterDataService
{
    readonly LocalConfig config;
    readonly PostgresDatabase database;

    public MasterDataService(LocalConfig config, PostgresDatabase database)
    {
        this.config = config;
        this.database = database;
    }

    // FilterResources 会对每个卡池做几十次 File.Exists（还查多个根目录），
    // 每次 /data/master 请求都跑一遍会把线程池打满、整个服务卡死。
    // 因此缓存「卡池可用性」结果 60 秒。
    static (DateTimeOffset At, Dictionary<long, bool> Available) gachaAvailabilityCache;

    Dictionary<long, bool> GachaAvailability()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - gachaAvailabilityCache.At < TimeSpan.FromSeconds(60))
            return gachaAvailabilityCache.Available;
        var map = new Dictionary<long, bool>();
        foreach (var pool in GachaRules.AllPools)
            map[pool.Id] = HistoricalGachaResources.Available(pool, config.ResourcesRoot);
        gachaAvailabilityCache = (now, map);
        return map;
    }

    public async Task<MasterDataVersionInfo> GetActiveAsync()
    {
        var source = await GetSourceAsync();
        var operations = await new GameOperationsService(database).GetAsync();
        operations = operations with { Configuration = FilterResources(operations.Configuration) };
        if (System.Environment.GetEnvironmentVariable("WDS_SKIP_MASTER_OVERLAY") == "1")
        {
            return source;
        }
        // 只用最小补丁（仅解锁宝石商店分类），其余主数据原样保留，避免客户端初始化失败。
        return MinimalMasterOverlay.Apply(source);
    }

    GameOperationsConfiguration FilterResources(GameOperationsConfiguration configuration) => configuration with {
        Gachas = configuration.Gachas.ToDictionary(pair => pair.Key, pair => pair.Value
            && GachaAvailability().GetValueOrDefault(pair.Key, true))
    };

    async Task<MasterDataVersionInfo> GetSourceAsync()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select version, publish_timestamp, remote_path, local_file_path, sas_token
            from system_master_data_versions
            where is_active
            order by updated_at desc
            limit 1
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return MasterDataVersionInfo.FromConfig(config);
        }

        return new MasterDataVersionInfo(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }
}

readonly record struct MasterDataVersionInfo(
    string Version,
    long PublishTimestamp,
    string RemotePath,
    string LocalFilePath,
    string SasToken)
{
    public static MasterDataVersionInfo FromConfig(LocalConfig config)
    {
        return new MasterDataVersionInfo(
            config.MasterDataVersion,
            config.MasterDataPublishTimestamp,
            config.MasterDataRemotePath,
            config.MasterDataFile,
            "");
    }
}
