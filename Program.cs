using Microsoft.AspNetCore.HttpOverrides;

DotEnv.Load(Path.Combine(AppContext.BaseDirectory, ".env"));
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), "csharp-server", ".env"));
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

// 线程池饥饿防护：登录/数据接口里有同步 DB 等待和大量 IO，
// 线程池默认按 CPU 核数注入线程（注入速度约 1~2 个/秒），
// 并发一高就会出现「所有线程都在等，连 health 都没人处理」的假死。
// 这里把最小线程数直接抬到 200，避免启动后逐步注入导致的卡死。
System.Threading.ThreadPool.GetMinThreads(out var minWorker, out var minIo);
if (minWorker < 200)
    System.Threading.ThreadPool.SetMinThreads(200, Math.Max(minIo, 200));

var config = LocalConfig.FromEnvironment();
var state = new LocalServerState(config);
await state.Logger.LogAsync($"config publicBaseUrl={config.PublicBaseUrl} msgpackMode={config.MsgPackResponseMode} registerMode={config.RegisterResponseMode}");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new RealtimeFileLoggerProvider(state.RealtimeLogger));
builder.WebHost.ConfigureKestrel(options =>
{
    // 客户端（BestHTTP）会长期复用同一个 TLS 连接。Kestrel 默认 keep-alive 只有 130 秒，
    // 超时后服务端关连接、客户端仍复用旧连接 → 「closed/failed TLS connection」→ 请求失败。
    // 拉长到 30 分钟，避免这种「偶发但某个设备总是复现」的失败。
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    var address = config.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Parse(config.Host);
options.Listen(address, config.Port, listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    // ListenHttps443H2: 客户端接受任意证书；启用 ALPN 协商 HTTP/2
    var httpsPfx2 = System.IO.Path.Combine(AppContext.BaseDirectory, "server.pfx");
    if (System.IO.File.Exists(httpsPfx2))
    {
        options.Listen(address, 443, listener =>
        {
            listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
            listener.UseHttps(httpsPfx2, "wds123456");
        });
    }
    options.Listen(address, config.RealtimePort, listener =>
        listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});
builder.Services.AddSingleton(state);
builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 64 * 1024);
builder.Services.AddSingleton<Grpc.AspNetCore.Server.Model.IServiceMethodProvider<RealtimeHubService>, RealtimeHubMethodProvider>();

var app = builder.Build();

// 兜底请求日志：记录所有到达服务器的请求，用于定位「客户端报错但服务端没日志」的情况
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/localassets") || path.StartsWith("/assets") || path.StartsWith("/master/"))
    {
        await next();
        return;
    }
    try
    {
        System.IO.File.AppendAllText(@"D:\WDS\wk\rawreq.log",
            $"{DateTimeOffset.Now:HH:mm:ss.fff} RAWREQ {context.Request.Method} {path}{context.Request.QueryString} " +
            $"ct={context.Request.ContentType} len={context.Request.ContentLength}\n");
    }
    catch { }
    try
    {
        await next();
    }
    catch (Exception error)
    {
        try
        {
            System.IO.File.AppendAllText(@"D:\WDS\wk\rawreq.log",
                $"{DateTimeOffset.Now:HH:mm:ss.fff} RAWERR {context.Request.Method} {path} " +
                $"{error.GetType().Name}: {error.Message}\n");
        }
        catch { }
        throw;
    }
    try
    {
        System.IO.File.AppendAllText(@"D:\WDS\wk\rawreq.log",
            $"{DateTimeOffset.Now:HH:mm:ss.fff} RAWEND {context.Request.Method} {path} -> {context.Response.StatusCode}\n");
    }
    catch { }
});

// production-rewrite: 官方客户端请求 /production/... 和 /master-data/...，映射到我们的资源路由
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/production/", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/localassets" + path.Substring("/production".Length);
    }
    else if (path.StartsWith("/master-data/production/", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/master/" + path.Substring("/master-data/production/".Length);
    }
    await next();
});


// REPLAY_MODE: 把 acchan 抓到的响应原样回放（用于验证"数据 vs 逻辑"）
app.Use(async (context, next) =>
{
    if (System.Environment.GetEnvironmentVariable("REPLAY_MODE") != "1")
    {
        await next();
        return;
    }
    var replayDir = System.IO.Path.Combine(AppContext.BaseDirectory, "replay");
    var rawPath = context.Request.Path.Value ?? "";
    if (rawPath.StartsWith("/localap/", StringComparison.OrdinalIgnoreCase))
        rawPath = rawPath.Substring("/localap".Length);
    var key = rawPath.Trim('/').Replace('/', '_').Replace('.', '_');
    if (!string.IsNullOrEmpty(key))
    {
        var file = System.IO.Path.Combine(replayDir, key + ".bin");
        if (System.IO.File.Exists(file))
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(file);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/vnd.msgpack";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
            return;
        }
    }
    await next();
});


var realtimeLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Realtime");
realtimeLogger.LogInformation("实时服务初始化 host={Host} port={Port} logPath={LogPath}",
    config.Host, config.RealtimePort, Path.Combine(AppContext.BaseDirectory, "realtime.log"));
app.Lifetime.ApplicationStarted.Register(() =>
    realtimeLogger.LogInformation("实时服务已启动 host={Host} port={Port} publicUrl={PublicUrl}",
        config.Host, config.RealtimePort, config.RealtimeBaseUrl));
app.Lifetime.ApplicationStopping.Register(() => realtimeLogger.LogInformation("实时服务正在停止"));

await DatabaseMigrator.MigrateAsync(state.Database, config);

app.UseRealtimeRequestLogging(realtimeLogger, config.RealtimePort);
// 反代后 RemoteIpAddress 全是回环地址，限流与引继锁定必须依赖转发头还原真实来源。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
    KnownIPNetworks = { },
    KnownProxies = { }
});
app.Use(async (context, next) =>
{
    // 客户端固定使用 /<任意token>/... ，这里剥掉 token 段：
    //   /<token>/api/...   -> /localap/api/...
    //   /<token>/assets/.. -> /assets/..
    var pathValue = context.Request.Path.Value ?? string.Empty;
    var assetMatch = System.Text.RegularExpressions.Regex.Match(pathValue, @"^/[^/]+/(assets|static|master|photo|localap)(/.*)?$");
    var tokenMatch = System.Text.RegularExpressions.Regex.Match(pathValue, @"^/[^/]+/api(/.*)?$");
    if (assetMatch.Success && !pathValue.StartsWith("/localap/", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/" + assetMatch.Groups[1].Value + (assetMatch.Groups[2].Success ? assetMatch.Groups[2].Value : string.Empty);
    }
    else if (tokenMatch.Success
        && !pathValue.StartsWith("/localap/", StringComparison.OrdinalIgnoreCase)
        && !pathValue.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/localap/api" + (tokenMatch.Groups[1].Success ? tokenMatch.Groups[1].Value : string.Empty);
    }
    else if (context.Request.Path.StartsWithSegments("/api", out var remaining))
    {
        context.Request.Path = "/localap/api" + remaining;
    }

    // 原部署用反向代理把 /assets 映射到 /localassets，这里等价内联
    if (context.Request.Path.StartsWithSegments("/assets", out var assetRemaining))
        context.Request.Path = "/localassets" + assetRemaining;

    await next();
});

app.UseRouting();
app.UseRequestLogging(state.Logger);

// 时序实验：官方服务器在公网上，响应有几十到几百毫秒延迟；本机是局域网，几乎瞬时。
// 某些客户端的"加载画面淡出"看起来依赖真实时序，可用 WDS_RESPONSE_DELAY_MS 人为延迟来复现/验证。
var responseDelayMs = int.TryParse(System.Environment.GetEnvironmentVariable("WDS_RESPONSE_DELAY_MS"), out var delayValue)
    ? Math.Clamp(delayValue, 0, 5000)
    : 0;
if (responseDelayMs > 0)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/localap"))
            await Task.Delay(responseDelayMs);
        await next();
    });
}

app.MapApiEndpoints(state);
app.MapAssetEndpoints(state);
app.MapRealtimeDebugEndpoints(state);
app.MapGmEndpoints(state);
app.MapGrpcService<RealtimeHubService>();

app.MapFallback(async context =>
{
    await state.Logger.LogAsync($"?? unmatched {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await state.Codec.WriteApiResultAsync(context, Array.Empty<object>());
});

app.Run();

