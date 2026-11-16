using Npgsql;
using System.Text.Json;

sealed class ApiCodec
{
    readonly LocalConfig config;
    readonly LocalRequestLogger logger;
    readonly PostgresDatabase database;

    public ApiCodec(LocalConfig config, LocalRequestLogger logger, PostgresDatabase database)
    {
        this.config = config;
        this.logger = logger;
        this.database = database;
    }

    /// <summary>
    /// GM 改完数据后挂起的补发：把队列里的 DataObject 拼到 present 里，
    /// 客户端处理这个响应时就会更新对应内存，不用重新登录。
    /// </summary>
    async Task<object?[]> TakePendingPushAsync(HttpContext context)
    {
        try
        {
            var token = PayloadReaders.ReadApiToken(context);
            if (string.IsNullOrWhiteSpace(token)) return Array.Empty<object?>();
            var hash = CryptoService.Sha256(token);
            var userId = GmPushQueue.TryGetCachedUserId(hash);
            if (userId is null)
            {
                await using var connection = await database.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(
                    "select \"userId\" from user_auth_sessions where token_hash=$1 and expires_at > now() limit 1", connection);
                command.Parameters.AddWithValue(hash);
                if (await command.ExecuteScalarAsync() is not long id) return Array.Empty<object?>();
                GmPushQueue.CacheUserId(hash, id);
                userId = id;
            }
            if (!GmPushQueue.Has(userId.Value)) return Array.Empty<object?>();
            var pending = GmPushQueue.Drain(userId.Value);
            if (pending.Length > 0)
                await logger.LogAsync($"gm-push present={pending.Length} userId={userId.Value} path={context.Request.Path}");
            return pending;
        }
        catch
        {
            return Array.Empty<object?>();
        }
    }

    static bool IsPrivateRequest(HttpContext context) =>
        context.Request.Path.Value?.Contains("UpdateBirthDate", StringComparison.OrdinalIgnoreCase) == true
        || context.Request.Path.StartsWithSegments("/localap/api/Account")
        || context.Request.Path.StartsWithSegments("/api/Account");

    public async Task<object?> ReadRequestBodyAsync(HttpContext context)
    {
        var bytes = await ReadRequestBytesAsync(context);
        if (bytes.Length == 0) return null;

        return await DecodeAndLogRequestBodyAsync(context, bytes);
    }

    public async Task<byte[]> ReadRequestBytesAsync(HttpContext context)
    {
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var bytes = ms.ToArray();
        await logger.LogAsync($"body length={bytes.Length} path={context.Request.Path}");
        if (bytes.Length > 0 && !IsPrivateRequest(context))
        {
            await logger.LogAsync($"body raw path={context.Request.Path} hex={Bytes.ToHex(bytes, 256)}");
        }
        return bytes;
    }

    public async Task<object?> DecodeAndLogRequestBodyAsync(HttpContext context, byte[] bytes)
    {
        var contentType = context.Request.ContentType ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Deserialize<object>(bytes);
            if (!IsPrivateRequest(context)) await logger.LogAsync($"body decoded path={context.Request.Path} value={ValueFormatter.Format(json)}");
            return json;
        }

        var decoded = MagicOnionLz4.Unwrap(MsgPack.Decode(bytes));
        if (!IsPrivateRequest(context)) await logger.LogAsync($"body decoded path={context.Request.Path} value={ValueFormatter.Format(decoded)}");
        return decoded;
    }

    public async Task WriteApiErrorAsync(HttpContext context, string errorCode, int statusCode)
    {
        SetCommonHeaders(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/vnd.msgpack";
        var payload = Bytes.Concat(MsgPack.Encode(new object?[] { errorCode }), MsgPack.Encode(null),
            MsgPack.Encode(Array.Empty<object?>()), MsgPack.Encode(Array.Empty<object?>()), MsgPack.Encode(Array.Empty<object?>()));
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload);
    }

    // 裸数组响应：整个响应体就是 result 的 msgpack（少部分接口如 /api/Gachas 需要）
    public async Task WriteRawAsync(HttpContext context, object? result, string responseMode = "raw")
    {
        SetCommonHeaders(context);
        context.Response.ContentType = "application/vnd.msgpack";
        var payload = MsgPack.Encode(result ?? Array.Empty<object>());
        context.Response.Headers.ContentLength = payload.Length;
        await logger.LogAsync($"response mode={responseMode} bytes={payload.Length} path={context.Request.Path}");
        await context.Response.Body.WriteAsync(payload);
    }

    public async Task WriteApiResultAsync(HttpContext context, object? result, string? responseMode = null)
    {
        SetCommonHeaders(context);
        context.Response.ContentType = "application/vnd.msgpack";
        var mode = responseMode ?? config.MsgPackResponseMode;
        // GM 补发：客户端处理 present 会刷新内存里的货币/道具
        var extra = await TakePendingPushAsync(context);
        var payload = Bytes.Concat(
            MsgPack.Encode(Array.Empty<object>()),
            MsgPack.Encode(result),
            MsgPack.Encode(extra.Length > 0 ? extra : Array.Empty<object?>()),
            MsgPack.Encode(Array.Empty<object>()),
            MsgPack.Encode(Array.Empty<object>()));
        _ = mode;
        

        context.Response.Headers.ContentLength = payload.Length;
        if (IsPrivateRequest(context))
            await logger.LogAsync($"response mode={mode} bytes={payload.Length} path={context.Request.Path}");
        else
            await logger.LogAsync($"response mode={mode} bytes={payload.Length} prefix={Bytes.ToHex(payload, 48)} path={context.Request.Path} result={ValueFormatter.Format(result)}");
        await context.Response.Body.WriteAsync(payload);
    }

    public async Task WriteApiFramesAsync(
        HttpContext context,
        object? result,
        object? present,
        object? deleted,
        object? notifications,
        string responseMode = "five-frame")
    {
        SetCommonHeaders(context);
        context.Response.ContentType = "application/vnd.msgpack";
        // GM 补发：把挂起的 DataObject 并进 present，客户端随这次响应一起刷新
        var extra = await TakePendingPushAsync(context);
        if (extra.Length > 0)
        {
            var merged = new List<object?>();
            if (present is System.Collections.IEnumerable items and not string)
                foreach (var item in items) merged.Add(item);
            else if (present is not null)
                merged.Add(present);
            merged.AddRange(extra);
            present = merged.ToArray();
        }
        var payload = Bytes.Concat(
            MsgPack.Encode(Array.Empty<object>()),
            MsgPack.Encode(result),
            MsgPack.Encode(present ?? Array.Empty<object>()),
            MsgPack.Encode(deleted ?? Array.Empty<object>()),
            MsgPack.Encode(notifications ?? Array.Empty<object>()));
        

        context.Response.Headers.ContentLength = payload.Length;
        if (IsPrivateRequest(context))
            await logger.LogAsync($"response mode={responseMode} bytes={payload.Length} path={context.Request.Path}");
        else
        await logger.LogAsync($"response mode={responseMode} bytes={payload.Length} prefix={Bytes.ToHex(payload, 48)} path={context.Request.Path} result={ValueFormatter.Format(result)} present={ValueFormatter.Format(present)}");
        await context.Response.Body.WriteAsync(payload);
    }

    static bool IsEmptyArray(object? value)
    {
        return value switch
        {
            null => true,
            Array array => array.Length == 0,
            System.Collections.ICollection collection => collection.Count == 0,
            _ => false
        };
    }

    public void SetCommonHeaders(HttpContext context)
    {
        // acchan 不发送这两个头；它们曾被设成 0.0.1，可能误导客户端的版本判断
        if (System.Environment.GetEnvironmentVariable("WDS_LEGACY_VERSION_HEADERS") == "1")
        {
            context.Response.Headers["X-Client-Version"] = config.MinApplicationVersion;
            context.Response.Headers["X-Assets-Version"] = config.MinAssetVersion;
        }
        context.Response.Headers["X-Server-Version"] = config.ApplicationVersion;
        context.Response.Headers["X-MasterData-Version"] = config.MasterDataVersion;
        context.Response.Headers["X-MasterData-Uri"] = $"{config.PublicBaseUrl}/master/{config.MasterDataRemotePath}";
        context.Response.Headers["X-MasterData-SasToken"] = "";
        context.Response.Headers["X-MasterData-PublishTimestamp"] = config.MasterDataPublishTimestamp.ToString();
        context.Response.Headers["X-FM"] = "0";
        context.Response.Headers["X-Maintenance"] = "false";
        context.Response.Headers["X-Maintenance-Message"] = "";
        context.Response.Headers["X-Token-Expired"] = "false";
    }
}

