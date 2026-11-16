using System.Text.Json;
using System.Net;
using Npgsql;

/// <summary>
/// GM 后台：账号列表 / 货币 / 等级 / 教程状态 / 改名 / 全卡解锁 / 全道具。
/// 路径刻意避开 /api（Program.cs 的中间件会把 /&lt;token&gt;/api/... 重写到 /localap/api/...）。
/// 仅允许回环与局域网地址访问。
/// </summary>
static class GmEndpointMappings
{
    public static void MapGmEndpoints(this WebApplication app, LocalServerState state)
    {
        // ---- 应用内登录（不弹浏览器原生框）----
        app.MapPost("/gm/login", async (HttpContext context) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IsLocalOrLan(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var form = await context.Request.ReadFormAsync();
            var user = form["user"].ToString();
            var pass = form["pass"].ToString();
            if (GmAuth.Verify(user, pass))
            {
                context.Response.Cookies.Append(GmAuth.CookieName, GmAuth.IssueToken(), new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/gm",
                    MaxAge = TimeSpan.FromDays(30)
                });
                return Results.Redirect("/gm/");
            }
            return Results.Redirect("/gm/?e=1");
        });

        app.MapGet("/gm/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(GmAuth.CookieName, new CookieOptions { Path = "/gm" });
            return Results.Redirect("/gm/");
        });

        var gm = app.MapGroup("/gm");
        gm.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !IsLocalOrLan(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            // 端口转发会让所有请求都来自 127.0.0.1，因此不做回环免密，一律校验
            if (!GmAuth.Check(http))
            {
                // 不再弹浏览器原生登录框：页面请求返回应用内登录页，接口请求返回 401 JSON。
                var reqPath = http.Request.Path.Value ?? "";
                if (HttpMethods.IsGet(http.Request.Method)
                    && (reqPath == "/gm" || reqPath == "/gm/" || reqPath == "/gm/index.html"))
                {
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Content(GmLoginPage.Html, "text/html; charset=utf-8");
                }
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            return await next(invocation);
        });

        gm.MapGet("/", (HttpContext context) =>
    {
        // GM 页面改动后要立刻可见，禁止浏览器缓存
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Content(GmPage.Html, "text/html; charset=utf-8");
    });

        // 用 Authorization: Bearer <token> 反查账号（联调用）
        gm.MapGet("/whoami", async (HttpContext context) =>
        {
            var token = PayloadReaders.ReadApiToken(context);
            if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
            var hash = CryptoService.Sha256(token);
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                select a.id, a.public_id, a.name
                from user_auth_sessions s join user_accounts a on a.id = s."userId"
                where s.token_hash = $1 and s.expires_at > now()
                order by s.id desc limit 1
                """, connection);
            command.Parameters.AddWithValue(hash);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound(new { error = "token not bound" });
            return Results.Json(new
            {
                id = reader.GetInt64(0),
                publicId = reader.GetString(1),
                name = reader.GetString(2)
            });
        });

        gm.MapGet("/accounts", async (HttpContext context) =>
        {
            var query = context.Request.Query["q"].ToString().Trim();
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                select a.id, a.public_id, a.name, a.role,
                       coalesce(g.rank,1), coalesce(g.exp,0),
                       coalesce(g.free_jewel,0), coalesce(g.paid_jewel,0), coalesce(g.coin,0),
                       coalesce(g.tutorial_status,0), coalesce(g.is_tutorial_finished,false),
                       coalesce(g.stamina,0), coalesce(g.max_stamina,0),
                       p.display_name,
                       (select count(*) from user_character_cards c where c."userId" = a.id),
                       (select count(*) from user_item_possessions i where i."userId" = a.id)
                from user_accounts a
                left join user_game_states g on g."userId" = a.id
                left join user_profiles p on p."userId" = a.id
                where $1 = '' or a.public_id like '%' || $1 || '%' or a.name ilike '%' || $1 || '%'
                   or p.display_name ilike '%' || $1 || '%'
                order by (a.id <= 2) desc, a.id
                """, connection);
            command.Parameters.AddWithValue(query);
            var rows = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    id = reader.GetInt64(0),
                    publicId = reader.GetString(1),
                    accountName = reader.GetString(2),
                    role = reader.GetString(3),
                    rank = reader.GetInt32(4),
                    exp = reader.GetInt32(5),
                    freeJewel = reader.GetInt32(6),
                    paidJewel = reader.GetInt32(7),
                    coin = reader.GetInt32(8),
                    tutorialStatus = reader.GetInt32(9),
                    tutorialFinished = reader.GetBoolean(10),
                    stamina = reader.GetInt32(11),
                    maxStamina = reader.GetInt32(12),
                    displayName = reader.IsDBNull(13) ? null : reader.GetString(13),
                    cards = (int)reader.GetInt64(14),
                    items = (int)reader.GetInt64(15)
                });
            }
            return Results.Json(rows);
        });

        // 发放月卡（ログインパス）：送 480 有偿石并把商店购买记录写成 30 天有效期
        // 活动列表：读取内嵌的活动主数据，供 GM 逐个开启
        gm.MapGet("/events/list", async () =>
        {
            static async Task<List<object[]>> Load(string resource, int nameIndex)
            {
                var rows = new List<object[]>();
                using var stream = typeof(GmEndpointMappings).Assembly.GetManifestResourceStream(resource);
                if (stream is null) return rows;
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != System.Text.Json.JsonValueKind.Array || row.GetArrayLength() < 8) continue;
                    var id = row[0].TryGetInt64(out var v) ? v : 0;
                    var name = nameIndex >= 0 && row.GetArrayLength() > nameIndex
                        && row[nameIndex].ValueKind == System.Text.Json.JsonValueKind.String
                        ? row[nameIndex].GetString() ?? ""
                        : "";
                    long start = 0, end = 0;
                    for (var i = 0; i < row.GetArrayLength(); i++)
                    {
                        if (row[i].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                        if (row[i].TryGetInt64(out var n) && n > 1500000000) { if (start == 0) start = n; end = n; }
                    }
                    rows.Add(new object[] { id, name, start, end });
                }
                return rows;
            }

            var events = await Load("Event.EventMaster.json", -1);
            var stories = await Load("Event.StoryEventMaster.json", 3);
            var specials = await Load("Event.SpecialEventMaster.json", -1);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 剧情活动的日语名按日期区间匹配到普通活动上（EventMaster 本身没有名称字段）
            static string NameOf(List<object[]> events, List<object[]> stories, object[] row)
            {
                var start = (long)row[2];
                var end = (long)row[3];
                foreach (var s in stories)
                {
                    var sname = (string)s[1];
                    if (string.IsNullOrWhiteSpace(sname)) continue;
                    if ((long)s[2] == start || (long)s[3] == end) return sname;
                }
                return "";
            }
            return Results.Json(new
            {
                now,
                events = events.OrderByDescending(r => (long)r[0]).Select(r => new { id = r[0], kind = "event", name = NameOf(events, stories, r), start = r[2], end = r[3] }),
                stories = stories.OrderByDescending(r => (long)r[0]).Select(r => new { id = r[0], kind = "story", name = (string)r[1], start = r[2], end = r[3] }),
                specials = specials.OrderByDescending(r => (long)r[0]).Select(r => new { id = r[0], kind = "special", name = (string)r[1], start = r[2], end = r[3] })
            });
        });

        // 主页看板：服务器与数据概览
        // GM 装饰图：从 D:/WDS/srv/files/gm-art 读取
        gm.MapGet("/art/{name}", (string name) =>
        {
            var safe = System.IO.Path.GetFileName(name);
            string? path = null;
            foreach (var dir in new[] { "D:/WDS/srv/files/gm-chibi", "D:/WDS/srv/files/gm-art4", "D:/WDS/srv/files/gm-art3", "D:/WDS/srv/files/gm-art2", "D:/WDS/srv/files/gm-art" })
            {
                var candidate = System.IO.Path.Combine(dir, safe);
                if (System.IO.File.Exists(candidate)) { path = candidate; break; }
            }
            if (path is null) return Results.NotFound();
            var ext = System.IO.Path.GetExtension(safe).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
            return Results.File(System.IO.File.ReadAllBytes(path), mime);
        });

        // 图标：道具 / 卡牌 / 海报（都是解包出来的游戏原图）
        gm.MapGet("/img/{kind}/{name}", (string kind, string name) =>
        {
            var safe = System.IO.Path.GetFileName(name);
            if (!safe.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) safe += ".png";
            var dir = kind switch
            {
                "item" => @"D:\wds_story\static\itemicon",
                "card" => @"D:\wds_story\adv_cache\image\cardIcon",
                "poster" => @"D:\wds_story\static\postericon",
                _ => null
            };
            if (dir is null) return Results.NotFound();
            var path = System.IO.Path.Combine(dir, safe);
            return System.IO.File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
        });

        // 装饰图列表（供前端随机取用）
        gm.MapGet("/art-list", () =>
        {
            var dir = @"D:/WDS/srv/files/gm-art";
            if (!System.IO.Directory.Exists(dir)) return Results.Json(Array.Empty<string>());
            var names = System.IO.Directory.GetFiles(dir)
                .Select(System.IO.Path.GetFileName)
                .Where(n => n is not null && n.EndsWith(".png"))
                .OrderBy(n => n)
                .ToArray();
            return Results.Json(names);
        });

        gm.MapGet("/dashboard", async () =>
        {
            await using var connection = await state.Database.OpenConnectionAsync();

            async Task<long> Count(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                var value = await command.ExecuteScalarAsync();
                return value is null or DBNull ? 0 : Convert.ToInt64(value);
            }

            var accounts = await Count("select count(*) from user_accounts");
            var cards = await Count("select count(*) from user_character_cards");
            var posters = await Count("select count(*) from user_posters");
            var accessories = await Count("select count(*) from user_accessories");
            var packages = await Count("select count(*) from user_item_inbox_packages where received_at is null");
            var trophies = await Count("select count(*) from user_trophies");
            var events = await Count("select count(*) from user_story_event_states");

            var envPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".env");
            string masterVersion = "";
            string masterFile = "";
            foreach (var line in System.IO.File.ReadAllLines(envPath))
            {
                if (line.StartsWith("MASTER_DATA_VERSION=")) masterVersion = line["MASTER_DATA_VERSION=".Length..];
                else if (line.StartsWith("MASTER_DATA_FILE=")) masterFile = System.IO.Path.GetFileName(line["MASTER_DATA_FILE=".Length..]);
            }
            long masterSize = 0;
            if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, ".env")))
            {
                var full = System.IO.File.ReadAllLines(envPath)
                    .FirstOrDefault(l => l.StartsWith("MASTER_DATA_FILE="));
                if (full is not null)
                {
                    var p = full["MASTER_DATA_FILE=".Length..].Replace('/', '\\');
                    if (System.IO.File.Exists(p)) masterSize = new System.IO.FileInfo(p).Length;
                }
            }

            return Results.Json(new
            {
                server = new
                {
                    baseUrl = "https://192.168.1.40",
                    ports = new[] { 80, 443, 8099, 8788 },
                    startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss")
                },
                master = new { version = masterVersion, file = masterFile, bytes = masterSize },
                data = new { accounts, cards, posters, accessories, packages, trophies, events }
            });
        });

        // 活动状态操作：open / close / end（结算期）
        static async Task<object> RunEventModeAsync(HttpContext context, LocalServerState state, string mode)
        {
            var eventIdRaw = context.Request.Query["eventId"].ToString();
            if (!long.TryParse(eventIdRaw, out _))
                return Results.BadRequest(new { error = "eventId required" });
            var daysRaw = context.Request.Query["days"].ToString();
            var days = int.TryParse(daysRaw, out var d) ? Math.Clamp(d, 1, 365) : 14;
            var settleRaw = context.Request.Query["settleDays"].ToString();
            var settleDays = int.TryParse(settleRaw, out var s) ? Math.Clamp(s, 0, 30) : 2;

            var python = @"C:\Users\Admin\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe";
            var script = @"D:\WDS\wk\event_mode.py";
            var outFile = @"D:\WDS\wk\gm_event.db";
            var args = $@"""{script}"" --mode {mode} --event {eventIdRaw} --days {days} --settle-days {settleDays} --out ""{outFile}""";

            var psi = new System.Diagnostics.ProcessStartInfo(python, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return Results.Json(new { ok = false, error = stderr.Length > 0 ? stderr : stdout });
            if (!System.IO.File.Exists(outFile))
                return Results.Json(new { ok = false, error = "生成主数据失败" });

            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var version = $"{stamp}_{stamp}";
            var fileName = $"mastermemory_{version}.db";
            var masterDir = @"D:\WDS\srv\files\master\2026-10-24";
            System.IO.File.Copy(outFile, System.IO.Path.Combine(masterDir, fileName), overwrite: true);

            var envPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".env");
            var lines = System.IO.File.ReadAllLines(envPath);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("MASTER_DATA_FILE=")) lines[i] = "MASTER_DATA_FILE=D:/WDS/srv/files/master/2026-10-24/" + fileName;
                else if (lines[i].StartsWith("MASTER_DATA_REMOTE_PATH=")) lines[i] = "MASTER_DATA_REMOTE_PATH=2026-10-24/" + fileName;
                else if (lines[i].StartsWith("MASTER_DATA_VERSION=")) lines[i] = "MASTER_DATA_VERSION=" + version;
                else if (lines[i].StartsWith("MASTER_DATA_PUBLISH_TIMESTAMP=")) lines[i] = "MASTER_DATA_PUBLISH_TIMESTAMP=" + stamp;
            }
            System.IO.File.WriteAllLines(envPath, lines);

            // 记录当前开启的活动，供 union 下发 key 101
            try
            {
                var activePath = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
                if (mode == "open")
                    System.IO.File.WriteAllText(activePath, "[" + eventIdRaw + "]");
                else
                    System.IO.File.WriteAllText(activePath, "[]");
            }
            catch { }

            await state.Logger.LogAsync($"gm-events-{mode} eventId={eventIdRaw} settleDays={settleDays} version={version}");

            try
            {
                var shell = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                var restart = "Start-Sleep -Seconds 2; Get-Process SiriusLocalServer -ErrorAction SilentlyContinue | Stop-Process -Force; "
                    + "Start-Sleep -Seconds 2; $env:REPLAY_MODE='0'; $env:WDS_SKIP_MASTER_OVERLAY='1'; $env:WDS_EXTRA_UNION_KEYS='1'; "
                    + "Start-Process -FilePath '" + AppContext.BaseDirectory + "SiriusLocalServer.exe' -WorkingDirectory '"
                    + AppContext.BaseDirectory + "' -WindowStyle Hidden";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell, "-NoProfile -Command " + restart)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception restartError)
            {
                await state.Logger.LogAsync($"gm-events-{mode} restart-failed error={restartError.Message}");
            }

            return Results.Json(new { ok = true, mode, version, settleDays, output = stdout.Trim() });
        }

        gm.MapPost("/events/close", (HttpContext context) => RunEventModeAsync(context, state, "close"));
        gm.MapPost("/events/end", (HttpContext context) => RunEventModeAsync(context, state, "end"));

        // 一键开活动：抬升指定活动的日期，生成新主数据并重启服务
        gm.MapPost("/events/open", async (HttpContext context) =>
        {
            var eventIdRaw = context.Request.Query["eventId"].ToString();
            var daysRaw = context.Request.Query["days"].ToString();
            var all = context.Request.Query["all"].ToString() == "1";
            if (!all && !long.TryParse(eventIdRaw, out _))
                return Results.BadRequest(new { error = "eventId required (或用 all=1 开全部)" });
            var days = int.TryParse(daysRaw, out var parsedDays) ? Math.Clamp(parsedDays, 1, 365) : 30;

            var python = @"C:\Users\Admin\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe";
            var script = @"D:\WDS\wk\lift_event.py";
            var outFile = @"D:\WDS\wk\gm_event.db";

            var args = all
                ? $@"""{script}"" --all --days {days} --out ""{outFile}"""
                : $@"""{script}"" --event {eventIdRaw} --days {days} --out ""{outFile}""";

            var psi = new System.Diagnostics.ProcessStartInfo(python, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return Results.Json(new { ok = false, error = stderr.Length > 0 ? stderr : stdout });

            if (!System.IO.File.Exists(outFile))
                return Results.Json(new { ok = false, error = "生成主数据失败" });

            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var version = $"{stamp}_{stamp}";
            var fileName = $"mastermemory_{version}.db";
            var masterDir = @"D:\WDS\srv\files\master\2026-10-24";
            System.IO.File.Copy(outFile, System.IO.Path.Combine(masterDir, fileName), overwrite: true);

            // 改写 .env
            var envPath = System.IO.Path.Combine(AppContext.BaseDirectory, ".env");
            var lines = System.IO.File.ReadAllLines(envPath);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("MASTER_DATA_FILE=")) lines[i] = "MASTER_DATA_FILE=D:/WDS/srv/files/master/2026-10-24/" + fileName;
                else if (lines[i].StartsWith("MASTER_DATA_REMOTE_PATH=")) lines[i] = "MASTER_DATA_REMOTE_PATH=2026-10-24/" + fileName;
                else if (lines[i].StartsWith("MASTER_DATA_VERSION=")) lines[i] = "MASTER_DATA_VERSION=" + version;
                else if (lines[i].StartsWith("MASTER_DATA_PUBLISH_TIMESTAMP=")) lines[i] = "MASTER_DATA_PUBLISH_TIMESTAMP=" + stamp;
            }
            System.IO.File.WriteAllLines(envPath, lines);

            // 记录当前开启的活动，供 union 下发 key 101 / 82
            try
            {
                var activePath3 = System.IO.Path.Combine(AppContext.BaseDirectory, "active_events.json");
                System.IO.File.WriteAllText(activePath3, all ? "[]" : "[" + eventIdRaw + "]");
                var shopPath3 = System.IO.Path.Combine(AppContext.BaseDirectory, "active_shops.json");
                if (System.IO.File.Exists(shopPath3)) System.IO.File.Delete(shopPath3);
            }
            catch { }

            await state.Logger.LogAsync($"gm-events-open eventId={eventIdRaw} all={all} days={days} version={version} outcome=written");

            // 延迟重启服务（让本次响应先返回）
            var restart = @"
Start-Sleep -Seconds 2
Get-Process SiriusLocalServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$env:REPLAY_MODE='0'; $env:WDS_SKIP_MASTER_OVERLAY='1'; $env:WDS_EXTRA_UNION_KEYS='1'
Start-Process -FilePath '" + AppContext.BaseDirectory + @"SiriusLocalServer.exe' -WorkingDirectory '" + AppContext.BaseDirectory + @"' -WindowStyle Hidden
";
            // 用完整路径启动 powershell；失败不影响主流程（只是需要手动重启）
            try
            {
                var shell = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell,
                    "-NoProfile -Command " + restart.Replace("\r", " ").Replace("\n", " "))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception restartError)
            {
                await state.Logger.LogAsync($"gm-events-open restart-failed error={restartError.Message}");
            }

            return Results.Json(new { ok = true, version, days, all, output = stdout.Trim() });
        });

        gm.MapPost("/monthly-pass", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId))
                return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            // 三种月卡：2001 ログインパス / 3003 スターパス / 3004 ダイスターパス
            var itemRaw = context.Request.Query["itemId"].ToString();
            var loginPassItemId = long.TryParse(itemRaw, out var parsedItem) ? parsedItem : 2001L;
            var paidJewel = loginPassItemId switch
            {
                3003 => 480,
                3004 => 1600,
                _ => 480
            };
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var update = new NpgsqlCommand(
                """
                insert into user_game_states ("userId", paid_jewel) values ($1, $2)
                on conflict ("userId") do update set paid_jewel = user_game_states.paid_jewel + $2, updated_at = now()
                """, connection, transaction))
            {
                update.Parameters.AddWithValue(userId.Value);
                update.Parameters.AddWithValue(paidJewel);
                await update.ExecuteNonQueryAsync();
            }

            await using (var purchase = new NpgsqlCommand(
                """
                insert into user_shop_purchase_states (id, "userId", shop_item_id, purchased_count, purchase_limit, reset_at, payload)
                values ($1, $2, $3, 1, 1, now() + interval '30 days', '{"source":"gm"}'::jsonb)
                on conflict (id) do update set purchased_count = 1,
                                               reset_at = now() + interval '30 days',
                                               updated_at = now()
                """, connection, transaction))
            {
                purchase.Parameters.AddWithValue(userId.Value * 1000000 + loginPassItemId);
                purchase.Parameters.AddWithValue(userId.Value);
                // 月卡状态：客户端通过 union key 126/127 读取有效期
                await using (var passStatus = new NpgsqlCommand(
                    """
                    insert into user_login_pass_status ("userId", id, valid_until, pass_kind)
                    values ($1, $2, now() + interval '30 days', $3)
                    on conflict ("userId", id) do update set
                        valid_until = greatest(user_login_pass_status.valid_until, now()) + interval '30 days',
                        pass_kind = $3
                    """, connection, transaction))
                {
                    passStatus.Parameters.AddWithValue(userId.Value);
                    passStatus.Parameters.AddWithValue(loginPassItemId);
                    passStatus.Parameters.AddWithValue(loginPassItemId == 2001 ? 0 : 1);
                    await passStatus.ExecuteNonQueryAsync();
                }
                purchase.Parameters.AddWithValue(loginPassItemId);
                await purchase.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Results.Json(new { ok = true, userId, itemId = loginPassItemId, paidJewel, days = 30 });
        });

        gm.MapPost("/currency", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            int? free = ReadInt(context, "free");
            int? paid = ReadInt(context, "paid");
            int? coin = ReadInt(context, "coin");
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_game_states set
                  free_jewel = coalesce($2, free_jewel),
                  paid_jewel = coalesce($3, paid_jewel),
                  coin       = coalesce($4, coin),
                  updated_at = now()
                where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue((object?)free ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)paid ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)coin ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
            await using var currency = new NpgsqlCommand("""
                update user_item_currencies set
                  free_jewel = coalesce($2, free_jewel),
                  paid_jewel = coalesce($3, paid_jewel),
                  coin       = coalesce($4, coin)
                where "userId" = $1
                """, connection);
            currency.Parameters.AddWithValue(userId.Value);
            currency.Parameters.AddWithValue((object?)free ?? DBNull.Value);
            currency.Parameters.AddWithValue((object?)paid ?? DBNull.Value);
            currency.Parameters.AddWithValue((object?)coin ?? DBNull.Value);
            await currency.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-currency userId={userId} free={free} paid={paid} coin={coin}");
            // 挂一条补发：客户端下一次任意 API 请求的响应里会带上最新货币，界面即时刷新
            await using (var snapshot = new NpgsqlCommand(
                "select coin, free_jewel, paid_jewel from user_item_currencies where \"userId\"=$1", connection))
            {
                snapshot.Parameters.AddWithValue(userId.Value);
                await using var reader = await snapshot.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                    GmPushQueue.Enqueue(userId.Value,
                        new object?[] { 128, new object?[] { userId.Value, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) } });
            }
            return Results.Json(new { ok = true, userId });
        });

        gm.MapPost("/rank", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            int? rank = ReadInt(context, "rank");
            int? exp = ReadInt(context, "exp");
            int? stamina = ReadInt(context, "stamina");
            int? maxStamina = ReadInt(context, "maxStamina");
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_game_states set
                  rank        = coalesce($2, rank),
                  exp         = coalesce($3, exp),
                  stamina     = coalesce($4, stamina),
                  max_stamina = coalesce($5, max_stamina),
                  updated_at  = now()
                where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue((object?)rank ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)exp ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)stamina ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)maxStamina ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-rank userId={userId} rank={rank} exp={exp} stamina={stamina} max={maxStamina}");
            return Results.Json(new { ok = true, userId });
        });

        gm.MapPost("/tutorial", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var status = ReadInt(context, "status");
            if (string.IsNullOrWhiteSpace(publicId) || status is null)
                return Results.BadRequest(new { error = "publicId and status required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_game_states set
                  tutorial_status = $2,
                  is_tutorial_finished = ($2 = 99),
                  updated_at = now()
                where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(status.Value);
            await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-tutorial userId={userId} status={status}");
            return Results.Json(new { ok = true, userId, status });
        });

        gm.MapPost("/name", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var name = context.Request.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(publicId) || string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "publicId and name required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var p = new NpgsqlCommand("""update user_profiles set display_name=$2 where "userId"=$1""", connection);
            p.Parameters.AddWithValue(userId.Value);
            p.Parameters.AddWithValue(name);
            await p.ExecuteNonQueryAsync();
            await using var a = new NpgsqlCommand("""update user_accounts set name=$2, updated_at=now() where id=$1""", connection);
            a.Parameters.AddWithValue(userId.Value);
            a.Parameters.AddWithValue(name);
            await a.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-name userId={userId} name={name}");
            return Results.Json(new { ok = true, userId, name });
        });

        // 解锁全部角色卡（自动创建角色基础/默认服装/育成状态）
        gm.MapPost("/unlock-cards", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var catalog = new GameInventoryCatalog();
            await using var transaction = await connection.BeginTransactionAsync();
            var granted = 0;
            foreach (var card in catalog.Cards.Values)
            {
                try
                {
                    if (await CardInventoryWriter.GrantAsync(connection, transaction, userId.Value, catalog, card))
                        granted++;
                }
                catch (Exception error)
                {
                    await state.Logger.LogAsync($"gm-unlock-cards skip card={card.Id} error={error.Message}");
                }
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-unlock-cards userId={userId} granted={granted} total={catalog.Cards.Count}");
            return Results.Json(new { ok = true, userId, granted, total = catalog.Cards.Count });
        });

        // 解锁全部服装
        gm.MapPost("/unlock-costumes", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var catalog = new GameInventoryCatalog();
            var granted = 0;
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var costume in catalog.Costumes.Values)
            {
                try
                {
                    await using var command = new NpgsqlCommand("""
                        insert into user_character_costumes(id,"userId",costume_master_id)
                        values($1,$2,$3) on conflict ("userId",costume_master_id) do nothing
                        """, connection, transaction);
                    command.Parameters.AddWithValue(CardInventoryWriter.NewId());
                    command.Parameters.AddWithValue(userId.Value);
                    command.Parameters.AddWithValue(costume.Id);
                    granted += await command.ExecuteNonQueryAsync();
                }
                catch (Exception error)
                {
                    await state.Logger.LogAsync($"gm-unlock-costumes skip costume={costume.Id} error={error.Message}");
                }
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-unlock-costumes userId={userId} granted={granted} total={catalog.Costumes.Count}");
            return Results.Json(new { ok = true, userId, granted, total = catalog.Costumes.Count });
        });

        // 发放全部海报
        gm.MapPost("/unlock-posters", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var posterIds = new List<long>();
            using (var stream = typeof(GmEndpointMappings).Assembly
                       .GetManifestResourceStream("Shop.PosterMaster.json"))
            {
                if (stream is null) return Results.Json(new { error = "PosterMaster 资源缺失" });
                using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (row.ValueKind == System.Text.Json.JsonValueKind.Array
                        && row.GetArrayLength() > 0
                        && row[0].TryGetInt64(out var id))
                        posterIds.Add(id);
                }
            }

            var granted = 0;
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var posterId in posterIds)
            {
                try
                {
                    await using var command = new NpgsqlCommand("""
                        insert into user_posters("userId", poster_master_id)
                        values($1,$2)
                        """, connection, transaction);
                    command.Parameters.AddWithValue(userId.Value);
                    command.Parameters.AddWithValue(posterId);
                    granted += await command.ExecuteNonQueryAsync();
                }
                catch (Exception error)
                {
                    await state.Logger.LogAsync($"gm-unlock-posters skip poster={posterId} error={error.Message}");
                }
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-unlock-posters userId={userId} granted={granted} total={posterIds.Count}");
            return Results.Json(new { ok = true, userId, granted, total = posterIds.Count });
        });

        // 发放全部道具（每个 item 指定数量）
        gm.MapPost("/grant-items", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var quantity = ReadInt(context, "quantity") ?? 99;
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var catalog = new GameInventoryCatalog();
            var granted = 0;
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var item in catalog.Items.Values)
            {
                try
                {
                    await using var command = new NpgsqlCommand("""
                        insert into user_item_possessions(id,"userId",item_master_id,quantity)
                        values($1,$2,$3,$4)
                        on conflict ("userId",item_master_id) do update set quantity = greatest(user_item_possessions.quantity, excluded.quantity), updated_at = now()
                        """, connection, transaction);
                    command.Parameters.AddWithValue(UserScopedId(userId.Value, item.Id));
                    command.Parameters.AddWithValue(userId.Value);
                    command.Parameters.AddWithValue(item.Id);
                    command.Parameters.AddWithValue(quantity);
                    await command.ExecuteNonQueryAsync();
                    granted++;
                }
                catch (Exception error)
                {
                    await state.Logger.LogAsync($"gm-grant-items skip item={item.Id} error={error.Message}");
                }
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-grant-items userId={userId} granted={granted} qty={quantity}");
            return Results.Json(new { ok = true, userId, granted, quantity });
        });

        // 活动配置：读取原文
        gm.MapGet("/operations", async () =>
        {
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("select revision, payload::text from system_game_operations where id=1", connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound(new { error = "operations not migrated" });
            return Results.Json(new { revision = reader.GetInt64(0), payload = reader.GetString(1) });
        });

        // 活动配置：整体替换（走参考服务端自己的反序列化与校验）
        gm.MapPost("/operations", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "empty body" });

            GameOperationsConfiguration? config;
            try
            {
                config = JsonSerializer.Deserialize<GameOperationsConfiguration>(text, GameOperationsJson.Options);
                if (config is null) return Results.BadRequest(new { error = "config is null" });
                GameOperationsValidation.Validate(config);
            }
            catch (Exception error)
            {
                return Results.BadRequest(new { error = error.Message });
            }

            var normalized = JsonSerializer.Serialize(config, GameOperationsJson.Options);
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                update system_game_operations
                   set payload = $1::jsonb, revision = revision + 1, updated_at = now()
                 where id = 1
                 returning revision
                """, connection);
            command.Parameters.AddWithValue(normalized);
            var revision = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
            await state.Logger.LogAsync($"gm-operations updated revision={revision}");
            return Results.Json(new { ok = true, revision });
        });

        // 卡池快捷开关：all=1 全开 / all=0 全关
        gm.MapPost("/operations/gachas", async (HttpContext context) =>
        {
            var all = (ReadInt(context, "all") ?? 1) != 0;
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var read = new NpgsqlCommand("select payload::text from system_game_operations where id=1", connection);
            var text = (string?)await read.ExecuteScalarAsync();
            if (text is null) return Results.NotFound(new { error = "operations not migrated" });
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node?["gachas"] is not System.Text.Json.Nodes.JsonObject gachas)
                return Results.BadRequest(new { error = "gachas section missing" });
            var changed = 0;
            foreach (var key in gachas.Select(pair => pair.Key).ToList())
            {
                gachas[key] = all;
                changed++;
            }
            await using var write = new NpgsqlCommand("""
                update system_game_operations set payload=$1::jsonb, revision=revision+1, updated_at=now() where id=1
                """, connection);
            write.Parameters.AddWithValue(node!.ToJsonString());
            await write.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-gachas all={all} changed={changed}");
            return Results.Json(new { ok = true, all, changed });
        });

        // 理论值计算器：GET /gm/rate?level=25&ach=99.5
        gm.MapGet("/rate", (HttpContext context) =>
        {
            var level = ReadInt(context, "level") ?? 0;
            var raw = context.Request.Query["ach"].ToString();
            if (!double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var achValue))
                return Results.BadRequest(new { error = "ach required (0-101)" });
            var ach = (decimal)achValue;
            var rate = NotationRate(level, ach);
            var grade = Grade(ach);
            var bonus = ach >= 98m ? Bonus(ach) : 0m;
            return Results.Json(new
            {
                level,
                achievement = achValue,
                bonus = (double)bonus,
                notationRate = (double)rate,
                grade
            });
        });

        // 重置账号进度：删掉该用户在全部 user_* 表里的数据（保留账号本身），
        // 下次登录参考服务端会自动重新播种默认数据
        gm.MapPost("/reset", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            if (!context.Request.Query["confirm"].ToString().Equals("yes", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "需要 confirm=yes 才会执行" });

            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var tables = await ListUserTablesAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            var deleted = 0;
            foreach (var table in tables)
            {
                try
                {
                    await using var command = new NpgsqlCommand($"delete from {table} where \"userId\"=$1", connection, transaction);
                    command.Parameters.AddWithValue(userId.Value);
                    deleted += await command.ExecuteNonQueryAsync();
                }
                catch (Exception error)
                {
                    await state.Logger.LogAsync($"gm-reset skip table={table} error={error.Message}");
                }
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-reset userId={userId} tables={tables.Count} rows={deleted}");
            return Results.Json(new { ok = true, userId, tables = tables.Count, rows = deleted });
        });

        // 删除账号（连带 user_* 数据）
        gm.MapPost("/delete", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            if (!context.Request.Query["confirm"].ToString().Equals("yes", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "需要 confirm=yes 才会执行" });

            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("delete from user_accounts where id=$1", connection);
            command.Parameters.AddWithValue(userId.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-delete userId={userId} rows={rows}");
            return Results.Json(new { ok = true, deleted = rows });
        });

        // 封禁 / 解封
        gm.MapPost("/ban", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var days = ReadInt(context, "days") ?? 7;
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_accounts set banned_until = now() + ($2 || ' days')::interval, updated_at = now()
                where id = $1 returning banned_until
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(days);
            var until = await command.ExecuteScalarAsync();
            await state.Logger.LogAsync($"gm-ban userId={userId} days={days}");
            return Results.Json(new { ok = true, userId, bannedUntil = until?.ToString() });
        });

        gm.MapPost("/unban", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_accounts set banned_until = null, updated_at = now() where id = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-unban userId={userId}");
            return Results.Json(new { ok = true, userId });
        });

        // 发收件箱奖励（客户端在「プレゼント」里领取）
        gm.MapPost("/inbox", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var thingType = ReadInt(context, "thingType") ?? 1;
            var masterId = ReadInt(context, "masterId") ?? 0;
            var quantity = ReadInt(context, "quantity") ?? 1;
            var message = context.Request.Query["message"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                bootstrapKey = $"gm-{Guid.NewGuid():N}",
                ThingType = thingType,
                MasterId = masterId,
                Quantity = quantity,
                Message = string.IsNullOrEmpty(message) ? "GM 发放" : message
            });
            await using var command = new NpgsqlCommand("""
                insert into user_item_inbox_packages("userId",payload,expires_at)
                values($1,$2::jsonb,now()+interval '30 days')
                returning id
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(payload);
            var id = await command.ExecuteScalarAsync();
            await state.Logger.LogAsync($"gm-inbox userId={userId} type={thingType} master={masterId} qty={quantity}");
            return Results.Json(new { ok = true, userId, packageId = id });
        });

        // 重置登录奖励领取记录（可重复领）
        gm.MapPost("/reset-loginbonus", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var claims = new NpgsqlCommand("""delete from user_login_bonus_claims where "userId"=$1""", connection);
            claims.Parameters.AddWithValue(userId.Value);
            var rows = await claims.ExecuteNonQueryAsync();
            await using var states = new NpgsqlCommand("""
                update user_login_bonus_states set current_count=0, status=0 where "userId"=$1
                """, connection);
            states.Parameters.AddWithValue(userId.Value);
            await states.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-reset-loginbonus userId={userId} claims={rows}");
            return Results.Json(new { ok = true, userId, clearedClaims = rows });
        });

        // 重置每日限制（自动演奏/稽古/免费挑战次数）
        gm.MapPost("/reset-daily", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_daily_limits
                   set auto_play_times=0, daily_lesson_times=0, music_course_free_challenge_times=0,
                       last_refreshed_at=now(), updated_at=now()
                 where "userId"=$1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-reset-daily userId={userId} rows={rows}");
            return Results.Json(new { ok = true, userId, rows });
        });

        // 一键满级 / 满资源
        gm.MapPost("/max", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var rank = ReadInt(context, "rank") ?? 300;
            var stamina = ReadInt(context, "stamina") ?? 999;
            var jewel = ReadInt(context, "jewel") ?? 300000;
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_game_states set
                  rank = $2, exp = 0, stamina = $3, max_stamina = $3,
                  free_jewel = $4, coin = 30000000, updated_at = now()
                where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(rank);
            command.Parameters.AddWithValue(stamina);
            command.Parameters.AddWithValue(jewel);
            await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-max userId={userId} rank={rank} jewel={jewel}");
            return Results.Json(new { ok = true, userId, rank, stamina, jewel });
        });
        // ---- 公告（system_notifications）----
        gm.MapGet("/notifications", async () =>
        {
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                select id, title, body, banner_path, is_confirmation, notification_tab_category,
                       starts_at, ends_at, display_order
                from system_notifications order by display_order, id
                """, connection);
            var rows = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    id = reader.GetInt64(0),
                    title = reader.GetString(1),
                    body = reader.GetString(2),
                    banner = reader.GetString(3),
                    isConfirmation = reader.GetBoolean(4),
                    tab = reader.GetInt32(5),
                    startsAt = reader.GetDateTime(6).ToString("yyyy-MM-dd HH:mm:ss"),
                    endsAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("yyyy-MM-dd HH:mm:ss"),
                    order = reader.GetInt32(8)
                });
            }
            return Results.Json(rows);
        });

        gm.MapPost("/notification-save", async (HttpContext context) =>
        {
            var id = ReadInt(context, "id");
            var title = context.Request.Query["title"].ToString();
            var body = context.Request.Query["body"].ToString();
            var banner = context.Request.Query["banner"].ToString();
            var isConfirmation = (ReadInt(context, "isConfirmation") ?? 0) != 0;
            var tab = ReadInt(context, "tab") ?? 1;
            var order = ReadInt(context, "order") ?? 0;
            if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "title required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            if (id is null or <= 0)
            {
                await using var insert = new NpgsqlCommand("""
                    insert into system_notifications
                      (title, body, banner_path, is_confirmation, notification_tab_category, display_order)
                    values ($1, $2, $3, $4, $5, $6) returning id
                    """, connection);
                insert.Parameters.AddWithValue(title);
                insert.Parameters.AddWithValue(body);
                insert.Parameters.AddWithValue(banner);
                insert.Parameters.AddWithValue(isConfirmation);
                insert.Parameters.AddWithValue(tab);
                insert.Parameters.AddWithValue(order);
                var newId = Convert.ToInt64(await insert.ExecuteScalarAsync());
                return Results.Json(new { ok = true, id = newId });
            }
            await using var update = new NpgsqlCommand("""
                update system_notifications set
                  title=$2, body=$3, banner_path=$4, is_confirmation=$5,
                  notification_tab_category=$6, display_order=$7, updated_at=now()
                where id=$1
                """, connection);
            update.Parameters.AddWithValue(id.Value);
            update.Parameters.AddWithValue(title);
            update.Parameters.AddWithValue(body);
            update.Parameters.AddWithValue(banner);
            update.Parameters.AddWithValue(isConfirmation);
            update.Parameters.AddWithValue(tab);
            update.Parameters.AddWithValue(order);
            await update.ExecuteNonQueryAsync();
            return Results.Json(new { ok = true, id });
        });

        gm.MapPost("/notification-delete", async (HttpContext context) =>
        {
            var id = ReadInt(context, "id");
            if (id is null) return Results.BadRequest(new { error = "id required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("delete from system_notifications where id=$1", connection);
            command.Parameters.AddWithValue(id.Value);
            await command.ExecuteNonQueryAsync();
            return Results.Json(new { ok = true });
        });

        // 全服补偿：给每个账号的礼物盒各塞一份（附文写进 payload.Message）
        gm.MapPost("/broadcast-gift", async (HttpContext context) =>
        {
            var masterId = ReadInt(context, "masterId");
            var quantity = ReadInt(context, "quantity") ?? 1;
            var thingType = ReadInt(context, "thingType") ?? 1;
            var message = context.Request.Query["message"].ToString();
            // thingType 12/13 是货币（有偿石/无偿石），没有 masterId，允许 0
            if (thingType == 1 && (masterId is null or <= 0)) return Results.BadRequest(new { error = "masterId required" });
            if (quantity <= 0) return Results.BadRequest(new { error = "quantity must be positive" });
            if (string.IsNullOrWhiteSpace(message)) message = "运营补偿";
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                insert into user_item_inbox_packages ("userId", payload)
                select id, jsonb_build_object(
                    'ThingType', $1,
                    'MasterId', $2,
                    'Quantity', $3,
                    'Message', $4,
                    'bootstrapKey', 'gm-broadcast-' || extract(epoch from now())::bigint || '-' || id)
                from user_accounts
                """, connection);
            command.Parameters.AddWithValue(thingType);
            command.Parameters.AddWithValue(masterId.Value);
            command.Parameters.AddWithValue(quantity);
            command.Parameters.AddWithValue(message);
            var sent = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-broadcast-gift masterId={masterId} qty={quantity} accounts={sent} msg={message}");
            return Results.Json(new { ok = true, sent });
        });

        // 公告图片缩略图（列表里显示用）
        gm.MapGet("/bannerimg/{**path}", (string path) =>
        {
            var root = @"D:\\WDS\\srv\\files\\assets\\static-assets\\Resources\\Textures\\Banners";
            var decoded = Uri.UnescapeDataString(path ?? "").TrimStart('/');
            var full = System.IO.Path.Combine(root, decoded.Replace('/', '\\') + ".png");
            return System.IO.File.Exists(full) ? Results.File(full, "image/png") : Results.NotFound();
        });

        // 公告图片：Banners 目录下现有的 png（banner_path 就是它的相对路径去掉 .png）
        gm.MapGet("/banners", () =>
        {
            var root = @"D:\WDS\srv\files\assets\static-assets\Resources\Textures\Banners";
            var list = new List<string>();
            if (System.IO.Directory.Exists(root))
                foreach (var file in System.IO.Directory.GetFiles(root, "*.png", SearchOption.AllDirectories))
                    list.Add(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/').Replace(".png", ""));
            return Results.Json(list.OrderBy(x => x));
        });

        // 礼物盒履历：谁什么时候收到了什么、领没领
        gm.MapGet("/inbox-history", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var limit = Math.Clamp(ReadInt(context, "limit") ?? 200, 1, 1000);
            await using var connection = await state.Database.OpenConnectionAsync();
            long? userId = null;
            if (!string.IsNullOrWhiteSpace(publicId))
            {
                userId = await FindUserIdAsync(connection, publicId);
                if (userId is null) return Results.NotFound(new { error = "account not found" });
            }
            var catalog = new GameInventoryCatalog();
            var rows = new List<object>();
            var sql = userId is null
                ? "select id, \"userId\", payload::text, received_at, created_at from user_item_inbox_packages order by id desc limit $1"
                : "select id, \"userId\", payload::text, received_at, created_at from user_item_inbox_packages where \"userId\"=$2 order by id desc limit $1";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(limit);
            if (userId is not null) command.Parameters.AddWithValue(userId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long masterId = 0; var quantity = 0; var thingType = 0; var message = "";
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(reader.GetString(2));
                    if (doc.RootElement.TryGetProperty("MasterId", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Number) masterId = m.GetInt64();
                    if (doc.RootElement.TryGetProperty("Quantity", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number) quantity = q.GetInt32();
                    if (doc.RootElement.TryGetProperty("ThingType", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Number) thingType = t.GetInt32();
                    if (doc.RootElement.TryGetProperty("Message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.String) message = msg.GetString() ?? "";
                }
                catch { }
                var name = thingType == 1 && catalog.Items.TryGetValue(masterId, out var item)
                    ? item.Name
                    : $"类型 {thingType} · #{masterId}";
                rows.Add(new
                {
                    id = reader.GetInt64(0),
                    userId = reader.GetInt64(1),
                    thingType,
                    masterId,
                    name,
                    quantity,
                    message,
                    received = !reader.IsDBNull(3),
                    receivedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm:ss"),
                    createdAt = reader.GetDateTime(4).ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            return Results.Json(rows);
        });


        // 才能开花一键拉满：按稀有度查 CharacterBloomItemMaster 的最高段阶，直接写 talent_stage
        gm.MapPost("/bloom-max", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var rarities = new List<int>();
            await using (var read = new NpgsqlCommand(
                "select distinct rarity from user_character_cards where \"userId\"=$1", connection))
            {
                read.Parameters.AddWithValue(userId.Value);
                await using var reader = await read.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rarities.Add(reader.GetInt32(0));
            }

            var applied = new List<object>();
            foreach (var rarity in rarities)
            {
                long sampleMaster;
                await using (var one = new NpgsqlCommand(
                    "select character_master_id from user_character_cards where \"userId\"=$1 and rarity=$2 limit 1", connection))
                {
                    one.Parameters.AddWithValue(userId.Value);
                    one.Parameters.AddWithValue(rarity);
                    var scalar = await one.ExecuteScalarAsync();
                    if (scalar is not long cm) continue;
                    sampleMaster = cm;
                }
                var maxStage = CharacterBloomRules.MaxPhase(sampleMaster) + 1;
                if (maxStage <= 1) continue;
                await using var upd = new NpgsqlCommand(
                    "update user_character_cards set talent_stage=$3 where \"userId\"=$1 and rarity=$2", connection);
                upd.Parameters.AddWithValue(userId.Value);
                upd.Parameters.AddWithValue(rarity);
                upd.Parameters.AddWithValue(maxStage);
                await upd.ExecuteNonQueryAsync();
                applied.Add(new { rarity, maxStage });
            }
            await state.Logger.LogAsync($"gm-bloom-max userId={userId} rarities={applied.Count}");
            return Results.Json(new { ok = true, userId, applied });
        });

        // 服务器状态
        gm.MapGet("/status", async () =>
        {
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                select
                  (select count(*) from user_accounts) as accounts,
                  (select count(*) from user_auth_sessions) as sessions,
                  (select count(*) from user_item_inbox_packages where received_at is null) as inboxUnread,
                  (select revision from system_game_operations where id=1) as revision,
                  (select payload->'gachas' from system_game_operations where id=1)::text as gachas,
                  (select count(*) from user_live_play_results) as liveResults
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.Json(new { error = "no data" });
            var gachasText = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
            var on = 0; var total = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(gachasText);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    total++;
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.True) on++;
                }
            }
            catch { }
            return Results.Json(new
            {
                accounts = reader.GetInt64(0),
                sessions = reader.GetInt64(1),
                inboxUnread = reader.GetInt64(2),
                operationsRevision = reader.GetInt64(3),
                gachasOn = on,
                gachasTotal = total,
                liveResults = reader.GetInt64(5),
                publicBaseUrl = state.Config.PublicBaseUrl
            });
        });

        // 单卡发放：POST /gm/card?publicId=&masterId=110010
        gm.MapPost("/card", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var masterId = ReadInt(context, "masterId");
            if (string.IsNullOrWhiteSpace(publicId) || masterId is null)
                return Results.BadRequest(new { error = "publicId and masterId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            var catalog = new GameInventoryCatalog();
            if (!catalog.Cards.TryGetValue(masterId.Value, out var card))
                return Results.NotFound(new { error = "card master not found" });
            await using var transaction = await connection.BeginTransactionAsync();
            var granted = await CardInventoryWriter.GrantAsync(connection, transaction, userId.Value, catalog, card);
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-card userId={userId} master={masterId} granted={granted}");
            return Results.Json(new { ok = true, userId, masterId, granted });
        });

        // 角色育成编辑：POST /gm/card-set?publicId=&masterId=&level=&skillLevel=&awakening=&friendship=&rank=
        gm.MapPost("/card-set", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var masterId = ReadInt(context, "masterId");
            if (string.IsNullOrWhiteSpace(publicId) || masterId is null)
                return Results.BadRequest(new { error = "publicId and masterId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_character_cards set
                  level            = coalesce($3, level),
                  skill_level      = coalesce($4, skill_level),
                  awakening        = coalesce($5, awakening),
                  friendship_level = coalesce($6, friendship_level),
                  rank             = coalesce($7, rank)
                where "userId" = $1 and character_master_id = $2
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(masterId.Value);
            command.Parameters.AddWithValue((object?)ReadInt(context, "level") ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)ReadInt(context, "skillLevel") ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)ReadInt(context, "awakening") ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)ReadInt(context, "friendship") ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)ReadInt(context, "rank") ?? DBNull.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-card-set userId={userId} master={masterId} rows={rows}");
            return Results.Json(new { ok = true, userId, masterId, rows });
        });

        // 重置任务状态
        gm.MapPost("/reset-missions", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""delete from user_mission_statuses where "userId"=$1""", connection);
            command.Parameters.AddWithValue(userId.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-reset-missions userId={userId} rows={rows}");
            return Results.Json(new { ok = true, userId, rows });
        });

        // 重置商店购买次数
        gm.MapPost("/reset-shop", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                update user_shop_purchase_states
                   set purchased_count = 0, reset_at = now() + interval '1 day', updated_at = now()
                 where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-reset-shop userId={userId} rows={rows}");
            return Results.Json(new { ok = true, userId, rows });
        });

        // 模拟一次打歌成绩：POST /gm/play-result?publicId=&liveMasterId=&difficulty=&ach=&level=
        gm.MapPost("/play-result", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var liveMasterId = ReadInt(context, "liveMasterId");
            var difficulty = ReadInt(context, "difficulty") ?? 1;
            var rawAch = context.Request.Query["ach"].ToString();
            if (string.IsNullOrWhiteSpace(publicId) || liveMasterId is null
                || !double.TryParse(rawAch, System.Globalization.CultureInfo.InvariantCulture, out var achValue))
                return Results.BadRequest(new { error = "publicId, liveMasterId, ach required" });

            var level = ReadInt(context, "level") ?? 20;
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var ach = (decimal)achValue;
            var notation = NotationRate(level, ach);
            var grade = Grade(ach);
            var clearLamp = ach >= 100.99m ? 4 : ach >= 100m ? 3 : 2;
            var score = achValue * 1000.0;

            await using var command = new NpgsqlCommand("""
                insert into user_live_play_results(
                    id,"userId",live_master_id,difficulty,best_score,best_technical_score,
                    clear_count,full_combo_count,all_perfect_count,payload,played_at,updated_at,
                    achievement_rate,notation_rate,clear_lamp,rate_grade)
                values($1,$2,$3,$4,$5,$5,1,$6,$7,'{}'::jsonb,now(),now(),$8,$9,$10,$11)
                on conflict ("userId",live_master_id,difficulty) do update set
                    best_score = greatest(user_live_play_results.best_score, excluded.best_score),
                    clear_count = user_live_play_results.clear_count + 1,
                    full_combo_count = greatest(user_live_play_results.full_combo_count, excluded.full_combo_count),
                    all_perfect_count = greatest(user_live_play_results.all_perfect_count, excluded.all_perfect_count),
                    achievement_rate = greatest(user_live_play_results.achievement_rate, excluded.achievement_rate),
                    notation_rate = greatest(user_live_play_results.notation_rate, excluded.notation_rate),
                    clear_lamp = greatest(user_live_play_results.clear_lamp, excluded.clear_lamp),
                    rate_grade = greatest(user_live_play_results.rate_grade, excluded.rate_grade),
                    updated_at = now()
                returning id
                """, connection);
            command.Parameters.AddWithValue(CardInventoryWriter.NewId());
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(liveMasterId.Value);
            command.Parameters.AddWithValue(difficulty);
            command.Parameters.AddWithValue(score);
            command.Parameters.AddWithValue(ach >= 100m ? 1 : 0);
            command.Parameters.AddWithValue(ach >= 100.99m ? 1 : 0);
            command.Parameters.AddWithValue(achValue);
            command.Parameters.AddWithValue((double)notation);
            command.Parameters.AddWithValue(clearLamp);
            command.Parameters.AddWithValue(grade);
            var id = await command.ExecuteScalarAsync();
            await state.Logger.LogAsync($"gm-play-result userId={userId} live={liveMasterId} diff={difficulty} ach={achValue} rate={notation}");
            return Results.Json(new
            {
                ok = true, userId, liveMasterId, difficulty, level,
                achievement = achValue, notationRate = (double)notation, grade, clearLamp, id
            });
        });

        // 理论值汇总（取最好的 30 首相加）
        gm.MapGet("/rating", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            await using var command = new NpgsqlCommand("""
                select live_master_id, difficulty, notation_rate, achievement_rate, rate_grade
                from user_live_play_results where "userId"=$1
                order by notation_rate desc limit 30
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            var rows = new List<object>();
            double total = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var rate = reader.GetDouble(2);
                total += rate;
                rows.Add(new
                {
                    liveMasterId = reader.GetInt64(0),
                    difficulty = reader.GetInt32(1),
                    notationRate = rate,
                    achievement = reader.GetDouble(3),
                    grade = reader.GetInt32(4)
                });
            }
            return Results.Json(new { ok = true, userId, count = rows.Count, totalRate = Math.Round(total, 2), top = rows });
        });

        // 批量发收件箱奖励：body = [{"thingType":1,"masterId":110001,"quantity":5,"message":"..."}]
        gm.MapPost("/inbox-batch", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "empty body" });

            List<Dictionary<string, System.Text.Json.JsonElement>>? items;
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(text);
            }
            catch (Exception error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            if (items is null || items.Count == 0) return Results.BadRequest(new { error = "no items" });

            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var created = 0;
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var item in items)
            {
                int Read(string key, int fallback) =>
                    item.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? v.GetInt32() : fallback;
                var message = item.TryGetValue("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? m.GetString() : "GM 发放";
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    bootstrapKey = $"gm-{Guid.NewGuid():N}",
                    ThingType = Read("thingType", 1),
                    MasterId = Read("masterId", 0),
                    Quantity = Read("quantity", 1),
                    Message = message
                });
                await using var command = new NpgsqlCommand("""
                    insert into user_item_inbox_packages("userId",payload,expires_at)
                    values($1,$2::jsonb,now()+interval '30 days')
                    """, connection, transaction);
                command.Parameters.AddWithValue(userId.Value);
                command.Parameters.AddWithValue(payload);
                await command.ExecuteNonQueryAsync();
                created++;
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-inbox-batch userId={userId} created={created}");
            return Results.Json(new { ok = true, userId, created });
        });

        // 只读 SQL 查询器：POST /gm/sql   body = SELECT ...
        gm.MapPost("/sql", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var sql = (await reader.ReadToEndAsync()).Trim();
            if (string.IsNullOrWhiteSpace(sql)) return Results.BadRequest(new { error = "empty sql" });
            var lowered = sql.TrimStart().ToLowerInvariant();
            if (!lowered.StartsWith("select") && !lowered.StartsWith("with") && !lowered.StartsWith("table"))
                return Results.BadRequest(new { error = "只允许 SELECT / WITH / TABLE 查询" });
            var limit = ReadInt(context, "limit") ?? 200;
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 20 };
            try
            {
                await using var result = await command.ExecuteReaderAsync();
                var columns = new List<string>();
                for (var i = 0; i < result.FieldCount; i++) columns.Add(result.GetName(i));
                var rows = new List<object?[]>();
                while (await result.ReadAsync() && rows.Count < limit)
                {
                    var values = new object?[result.FieldCount];
                    for (var i = 0; i < result.FieldCount; i++)
                        values[i] = result.IsDBNull(i) ? null : result.GetValue(i)?.ToString();
                    rows.Add(values);
                }
                await state.Logger.LogAsync($"gm-sql rows={rows.Count}");
                return Results.Json(new { ok = true, columns, rows, truncated = rows.Count >= limit });
            }
            catch (Exception error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // 日志查看：GET /gm/logs?file=requests&lines=100
        gm.MapGet("/logs", (HttpContext context) =>
        {
            var name = context.Request.Query["file"].ToString();
            if (string.IsNullOrWhiteSpace(name)) name = "requests";
            var safe = new string(name.Where(char.IsLetterOrDigit).ToArray());
            if (safe.Length == 0) return Results.BadRequest(new { error = "bad file name" });
            var path = Path.Combine(AppContext.BaseDirectory, safe + ".log");
            if (!File.Exists(path)) return Results.NotFound(new { error = "log not found", path });
            var lines = ReadInt(context, "lines") ?? 100;
            var all = File.ReadAllLines(path);
            var tail = all.Skip(Math.Max(0, all.Length - lines)).ToArray();
            return Results.Json(new { ok = true, file = safe + ".log", total = all.Length, lines = tail });
        });

        // 数据库备份：GET /gm/backup
        gm.MapGet("/backup", async () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"local_game_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
            var pgDump = @"D:\WDS\pg\pgsql\bin\pg_dump.exe";
            if (!File.Exists(pgDump)) return Results.BadRequest(new { error = "pg_dump not found", pgDump });

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pgDump,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-h", "127.0.0.1", "-p", "5432", "-U", "local_user", "-d", "local_game", "--no-owner" })
                psi.ArgumentList.Add(arg);
            psi.Environment["PGPASSWORD"] = "localpass";
            using var process = System.Diagnostics.Process.Start(psi)!;
            await using (var output = File.Create(file))
                await process.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var size = new FileInfo(file).Length;
            await state.Logger.LogAsync($"gm-backup file={file} bytes={size}");
            return Results.Json(new { ok = size > 0, file, bytes = size, error = stderr.Trim() });
        });

        // 解锁全部主页皮肤（22 个）
        gm.MapPost("/unlock-home-skins", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            var granted = 0;
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var skin in HomeSkinIds)
            {
                await using var command = new NpgsqlCommand("""
                    insert into user_home_skin_possessions("userId",home_skin_master_id)
                    values($1,$2) on conflict ("userId",home_skin_master_id) do nothing
                    """, connection, transaction);
                command.Parameters.AddWithValue(userId.Value);
                command.Parameters.AddWithValue(skin);
                granted += await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-unlock-home-skins userId={userId} granted={granted}");
            return Results.Json(new { ok = true, userId, granted, total = HomeSkinIds.Length });
        });

        // 设置名片样式：POST /gm/cosmetics?publicId=&iconFrame=190001&nameplate=&homeSkin=&poster=
        // 解锁全部歌曲（Live 测试/玩家便利）
        gm.MapPost("/unlock-music", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });
            var path = Path.Combine(AppContext.BaseDirectory, "masterdata-export", "tables", "MusicMaster.json");
            var alt = Path.Combine(AppContext.BaseDirectory, "masterdata", "MusicMaster.json");
            var file = File.Exists(path) ? path : alt;
            var ids = new List<long>();
            if (File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind == System.Text.Json.JsonValueKind.Array && row.GetArrayLength() > 0
                        && row[0].ValueKind == System.Text.Json.JsonValueKind.Number)
                        ids.Add(row[0].GetInt64());
                }
            }
            if (ids.Count == 0) return Results.NotFound(new { error = "MusicMaster.json 缺失", file });
            var inserted = 0;
            foreach (var musicId in ids)
            {
                await using var cmd = new NpgsqlCommand("""
                    insert into user_live_music_states(id, "userId", music_master_id, is_unlocked, unlocked_at, payload, updated_at)
                    values ((select coalesce(max(id), 0) + 1 from user_live_music_states), $1, $2, true, now(), '{}'::jsonb, now())
                    on conflict ("userId", music_master_id) do update
                      set is_unlocked = true, unlocked_at = coalesce(user_live_music_states.unlocked_at, now()), updated_at = now()
                    """, connection);
                cmd.Parameters.AddWithValue(userId.Value);
                cmd.Parameters.AddWithValue(musicId);
                inserted += await cmd.ExecuteNonQueryAsync();
            }
            await state.Logger.LogAsync($"gm-unlock-music userId={userId} count={ids.Count} rows={inserted}");
            return Results.Json(new { ok = true, userId, musicCount = ids.Count, rows = inserted });
        });

        gm.MapPost("/cosmetics", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            if (string.IsNullOrWhiteSpace(publicId)) return Results.BadRequest(new { error = "publicId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            var iconFrame = ReadInt(context, "iconFrame");
            var nameplate = ReadInt(context, "nameplate");
            var homeSkin = ReadInt(context, "homeSkin");
            var poster = ReadInt(context, "poster");
            if (iconFrame is null && nameplate is null && homeSkin is null && poster is null)
                return Results.BadRequest(new { error = "至少给一个 iconFrame/nameplate/homeSkin/poster" });

            if (homeSkin is not null)
            {
                await using var possess = new NpgsqlCommand("""
                    insert into user_home_skin_possessions("userId",home_skin_master_id)
                    values($1,$2) on conflict ("userId",home_skin_master_id) do nothing
                    """, connection);
                possess.Parameters.AddWithValue(userId.Value);
                possess.Parameters.AddWithValue(homeSkin.Value);
                await possess.ExecuteNonQueryAsync();
            }

            // poster 指向 user_character_cards.id（外键），这里按角色卡 masterId 解析
            long? posterCardId = null;
            if (poster is not null)
            {
                await using var find = new NpgsqlCommand("""
                    select id from user_character_cards where "userId"=$1 and character_master_id=$2 limit 1
                    """, connection);
                find.Parameters.AddWithValue(userId.Value);
                find.Parameters.AddWithValue(poster.Value);
                var value = await find.ExecuteScalarAsync();
                if (value is null)
                    return Results.BadRequest(new { error = "该账号没有这张卡，无法设为海报", cardMasterId = poster });
                posterCardId = Convert.ToInt64(value);
            }

            await using var command = new NpgsqlCommand("""
                update user_profiles set
                  icon_frame_master_id = coalesce($2, icon_frame_master_id),
                  nameplate_master_id  = coalesce($3, nameplate_master_id),
                  home_skin_master_id  = coalesce($4, home_skin_master_id),
                  selected_poster_id   = coalesce($5, selected_poster_id)
                where "userId" = $1
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue((object?)iconFrame ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)nameplate ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)homeSkin ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)posterCardId ?? DBNull.Value);
            var rows = await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-cosmetics userId={userId} icon={iconFrame} nameplate={nameplate} skin={homeSkin} poster={poster}");
            return Results.Json(new { ok = true, userId, rows });
        });

        // 主数据浏览：卡牌 / 道具 / 服装 / 角色基础
        gm.MapGet("/masters/cards", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var baseId = ReadInt(context, "base");
            var rarity = ReadInt(context, "rarity");
            var limit = ReadInt(context, "limit") ?? 200;
            var catalog = new GameInventoryCatalog();
            var rows = catalog.Cards.Values
                .Where(c => baseId is null || c.BaseId == baseId)
                .Where(c => rarity is null || c.Rarity == rarity)
                .Where(c => string.IsNullOrEmpty(q)
                    || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Id.ToString().Contains(q))
                .OrderByDescending(c => c.Rarity)
                .ThenBy(c => c.BaseId)
                .Take(limit)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    baseId = c.BaseId,
                    baseName = catalog.Bases.TryGetValue(c.BaseId, out var b) ? b.Name : "",
                    rarity = c.Rarity
                });
            return Results.Json(rows);
        });

        gm.MapGet("/masters/items", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var category = context.Request.Query["category"].ToString().Trim();
            var limit = ReadInt(context, "limit") ?? 200;
            var catalog = new GameInventoryCatalog();
            var rows = catalog.Items.Values
                .Where(i => !HiddenItem(i.Id))
                .Where(i => string.IsNullOrEmpty(q)
                    || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Id.ToString().Contains(q))
                .Select(i => new { id = i.Id, name = i.Name, maxQuantity = i.MaxQuantity, category = ItemCategory(i.Id, i.Name) })
                .Where(i => string.IsNullOrEmpty(category) || i.category == category)
                .OrderBy(i => CategoryRank(i.category))
                .ThenBy(i => i.id)
                .Take(limit);
            return Results.Json(rows);
        });

        // 饰品（劇団電姫のおまもり，139xxx）
        gm.MapGet("/masters/accessories", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var catalog = new GameInventoryCatalog();
            var rows = catalog.Items.Values
                .Where(i => i.Id >= 139000 && i.Id < 140000)
                .Where(i => string.IsNullOrEmpty(q)
                    || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Id.ToString().Contains(q))
                .OrderBy(i => i.Id)
                .Select(i => new { id = i.Id, name = i.Name, limited = i.Name.Contains('【', StringComparison.Ordinal) });
            return Results.Json(rows);
        });

        gm.MapGet("/masters/costumes", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var limit = ReadInt(context, "limit") ?? 200;
            var catalog = new GameInventoryCatalog();
            var rows = catalog.Costumes.Values
                .Where(c => string.IsNullOrEmpty(q)
                    || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Id.ToString().Contains(q))
                .OrderBy(c => c.Id)
                .Take(limit)
                .Select(c => new { id = c.Id, name = c.Name });
            return Results.Json(rows);
        });

        gm.MapGet("/masters/bases", () =>
        {
            var catalog = new GameInventoryCatalog();
            return Results.Json(catalog.Bases.Values
                .OrderBy(b => b.Id)
                .Select(b => new { id = b.Id, name = b.Name, defaultCostume = b.DefaultCostume }));
        });

        // 登录奖励的中文名称与奖励文字，避免 GM 页面只显示数字编号
        static string LoginBonusTitle(string id) => id switch
        {
            "10006" => "每日签到",
            "168" => "「最终章」纪念・演员十连券",
            "169" => "「最终章」纪念・海报十连券",
            "170" => "「最终章」纪念签到",
            "179" => "3 周年倒计时签到",
            "35013" => "「最终章」签到",
            _ => "登录奖励 " + id
        };

        static string LoginBonusThingText(int thingType, long masterId, int quantity) => thingType switch
        {
            13 => $"无偿石 ×{quantity}",
            1 => $"道具 #{masterId} ×{quantity}",
            12 => $"有偿石 ×{quantity}",
            _ => $"类型 {thingType} · #{masterId} ×{quantity}"
        };

        // 活动列表：登录奖励 / 任务通行证 / 卡池
        gm.MapGet("/ops/list", async () =>
        {
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var read = new NpgsqlCommand("select revision, payload::text from system_game_operations where id=1", connection);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound(new { error = "operations not migrated" });
            var revision = reader.GetInt64(0);
            var text = reader.GetString(1);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;

            var logins = new List<object>();
            if (node["loginBonuses"] is System.Text.Json.Nodes.JsonObject lb)
                foreach (var (key, value) in lb)
                    logins.Add(new
                    {
                        id = key,
                        title = LoginBonusTitle(key),
                        reward = LoginBonusThingText(
                            value?["thingType"]?.GetValue<int>() ?? 0,
                            value?["masterId"]?.GetValue<long>() ?? 0,
                            value?["quantity"]?.GetValue<int>() ?? 0),
                        enabled = value?["enabled"]?.GetValue<bool>() ?? false,
                        days = value?["days"]?.GetValue<int>() ?? 0,
                        startsAt = value?["startsAt"]?.GetValue<string>(),
                        endsAt = value?["endsAt"]?.GetValue<string>(),
                        thingType = value?["thingType"]?.GetValue<int>() ?? 0,
                        masterId = value?["masterId"]?.GetValue<long>() ?? 0,
                        quantity = value?["quantity"]?.GetValue<int>() ?? 0,
                        isLoop = value?["isLoop"]?.GetValue<bool>() ?? false
                    });

            var gachas = new List<object>();
            if (node["gachas"] is System.Text.Json.Nodes.JsonObject g)
                foreach (var (key, value) in g)
                    gachas.Add(new { id = key, enabled = value?.GetValue<bool>() ?? false });

            var mp = node["missionPass"];
            return Results.Json(new
            {
                revision,
                loginBonuses = logins,
                gachas,
                missionPass = new
                {
                    enabled = mp?["enabled"]?.GetValue<bool>() ?? false,
                    spEnabled = mp?["spEnabled"]?.GetValue<bool>() ?? false,
                    startsAt = mp?["startsAt"]?.GetValue<string>(),
                    endsAt = mp?["endsAt"]?.GetValue<string>()
                }
            });
        });

        // 单个登录奖励开关/改期
        gm.MapPost("/ops/loginbonus", async (HttpContext context) =>
        {
            var id = context.Request.Query["id"].ToString();
            if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "id required" });
            return await MutateOperationsAsync(state, node =>
            {
                if (node["loginBonuses"] is not System.Text.Json.Nodes.JsonObject lb || lb[id] is null)
                    return "登录奖励不存在: " + id;
                var item = lb[id]!;
                var enabled = ReadInt(context, "enabled");
                if (enabled is not null) item["enabled"] = enabled.Value != 0;
                var days = ReadInt(context, "days");
                if (days is not null) item["days"] = days.Value;
                var startsAt = context.Request.Query["startsAt"].ToString();
                if (!string.IsNullOrWhiteSpace(startsAt)) item["startsAt"] = startsAt;
                var endsAt = context.Request.Query["endsAt"].ToString();
                if (!string.IsNullOrWhiteSpace(endsAt)) item["endsAt"] = endsAt;
                var thingType = ReadInt(context, "thingType");
                if (thingType is not null) item["thingType"] = thingType.Value;
                var masterId = ReadInt(context, "masterId");
                if (masterId is not null) item["masterId"] = masterId.Value;
                var quantity = ReadInt(context, "quantity");
                if (quantity is not null) item["quantity"] = quantity.Value;
                var isLoop = ReadInt(context, "isLoop");
                if (isLoop is not null) item["isLoop"] = isLoop.Value != 0;
                return null;
            });
        });

        // 任务通行证开关/改期
        gm.MapPost("/ops/missionpass", async (HttpContext context) =>
        {
            return await MutateOperationsAsync(state, node =>
            {
                if (node["missionPass"] is not System.Text.Json.Nodes.JsonObject mp)
                    return "任务通行证配置不存在";
                var enabled = ReadInt(context, "enabled");
                if (enabled is not null) mp["enabled"] = enabled.Value != 0;
                var sp = ReadInt(context, "spEnabled");
                if (sp is not null) mp["spEnabled"] = sp.Value != 0;
                var startsAt = context.Request.Query["startsAt"].ToString();
                if (!string.IsNullOrWhiteSpace(startsAt)) mp["startsAt"] = startsAt;
                var endsAt = context.Request.Query["endsAt"].ToString();
                if (!string.IsNullOrWhiteSpace(endsAt)) mp["endsAt"] = endsAt;
                return null;
            });
        });

        // 单个卡池开关
        gm.MapPost("/ops/gacha", async (HttpContext context) =>
        {
            var id = context.Request.Query["id"].ToString();
            var enabled = ReadInt(context, "enabled");
            if (string.IsNullOrWhiteSpace(id) || enabled is null)
                return Results.BadRequest(new { error = "id and enabled required" });
            return await MutateOperationsAsync(state, node =>
            {
                if (node["gachas"] is not System.Text.Json.Nodes.JsonObject g || g[id] is null)
                    return "卡池不存在: " + id;
                g[id] = enabled.Value != 0;
                return null;
            });
        });

        // 海报列表（从解码出的 PosterMaster.json 读取）
        gm.MapGet("/masters/posters", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var limit = ReadInt(context, "limit") ?? 300;
            var path = Path.Combine(AppContext.BaseDirectory, "masterdata", "PosterMaster.json");
            if (!File.Exists(path)) return Results.NotFound(new { error = "PosterMaster.json 缺失", path });
            var catalog = new GameInventoryCatalog();
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var rows = new List<object>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Array || row.GetArrayLength() < 18) continue;
                var id = row[0].ValueKind == System.Text.Json.JsonValueKind.Number ? row[0].GetInt64() : 0;
                var name = row[1].ValueKind == System.Text.Json.JsonValueKind.String ? row[1].GetString() ?? "" : "";
                if (id == 0) continue;
                if (!string.IsNullOrEmpty(q) && !name.Contains(q, StringComparison.OrdinalIgnoreCase) && !id.ToString().Contains(q))
                    continue;
                var chars = new List<object>();
                if (row[17].ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in row[17].EnumerateArray())
                        if (c.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            var baseId = c.GetInt64();
                            chars.Add(new { id = baseId, name = catalog.Bases.TryGetValue(baseId, out var b) ? b.Name : "" });
                        }
                var start = row.GetArrayLength() > 19 && row[19].ValueKind == System.Text.Json.JsonValueKind.String ? row[19].GetString() : null;
                var end = row.GetArrayLength() > 20 && row[20].ValueKind == System.Text.Json.JsonValueKind.String ? row[20].GetString() : null;
                rows.Add(new { id, name, characters = chars, startsAt = start, endsAt = end });
                if (rows.Count >= limit) break;
            }
            return Results.Json(rows);
        });

        // 发放海报：POST /gm/poster?publicId=&masterId=&phase=
        gm.MapPost("/poster", async (HttpContext context) =>
        {
            var publicId = context.Request.Query["publicId"].ToString();
            var masterId = ReadInt(context, "masterId");
            var phase = ReadInt(context, "phase") ?? 0;
            if (string.IsNullOrWhiteSpace(publicId) || masterId is null)
                return Results.BadRequest(new { error = "publicId and masterId required" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var userId = await FindUserIdAsync(connection, publicId);
            if (userId is null) return Results.NotFound(new { error = "account not found" });

            // 与参考服务端一致：[id, masterId, 1, phase, 0, 0, false, 0]
            var payload = new object?[] { CardInventoryWriter.NewId(), masterId.Value, 1, phase, 0, 0, false, 0 };
            await using var command = new NpgsqlCommand("""
                insert into user_raw_union_states("userId",union_key,object_key,payload)
                values($1,28,$2,$3::jsonb)
                on conflict ("userId",union_key,object_key) do update set payload=excluded.payload, updated_at=now()
                """, connection);
            command.Parameters.AddWithValue(userId.Value);
            command.Parameters.AddWithValue(masterId.Value.ToString());
            command.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(payload));
            await command.ExecuteNonQueryAsync();
            await state.Logger.LogAsync($"gm-poster userId={userId} master={masterId} phase={phase}");
            return Results.Json(new { ok = true, userId, masterId, phase });
        });

        // 活动一览（EventMaster）
        gm.MapGet("/masters/events", (HttpContext context) =>
        {
            var q = context.Request.Query["q"].ToString().Trim();
            var onlyActive = (ReadInt(context, "active") ?? 1) != 0;
            var path = Path.Combine(AppContext.BaseDirectory, "masterdata", "EventMaster.json");
            if (!File.Exists(path)) return Results.NotFound(new { error = "EventMaster.json 缺失", path });
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var now = DateTimeOffset.UtcNow;
            // 剧情活动名：EventMaster 自身没有名称字段，用内嵌的 StoryEventMaster 按起止时间匹配。
            var storyNames = new List<(long Start, long End, string Name)>();
            using (var storyStream = typeof(GmEndpointMappings).Assembly.GetManifestResourceStream("Event.StoryEventMaster.json"))
            {
                if (storyStream is not null)
                {
                    using var storyDoc = System.Text.Json.JsonDocument.Parse(storyStream);
                    foreach (var srow in storyDoc.RootElement.EnumerateArray())
                    {
                        if (srow.ValueKind != System.Text.Json.JsonValueKind.Array || srow.GetArrayLength() < 8) continue;
                        long sstart = 0, send = 0;
                        for (var i = 0; i < srow.GetArrayLength(); i++)
                        {
                            if (srow[i].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                            if (srow[i].TryGetInt64(out var n) && n > 1500000000) { if (sstart == 0) sstart = n; send = n; }
                        }
                        var sname = srow[3].ValueKind == System.Text.Json.JsonValueKind.String ? srow[3].GetString() ?? "" : "";
                        if (sname.Length > 0) storyNames.Add((sstart, send, sname));
                    }
                }
            }
            var rows = new List<object>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                long GetLong(string k) => row.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : 0;
                string? GetStr(string k) => row.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                DateTimeOffset? GetDate(string k) => DateTimeOffset.TryParse(GetStr(k), out var d) ? d : null;

                var id = GetLong("Id");
                var name = GetStr("Name") ?? "";
                var type = GetStr("EventType") ?? "";
                var start = GetDate("StartDate");
                var end = GetDate("EndDate");
                var forceEnd = GetDate("ForceEndDate");
                if (string.IsNullOrWhiteSpace(name) && start is not null)
                {
                    var s0 = start.Value.ToUnixTimeSeconds();
                    var s1 = end?.ToUnixTimeSeconds() ?? 0;
                    foreach (var sn in storyNames)
                    {
                        if (sn.Start == s0 || (s1 != 0 && sn.End == s1)) { name = sn.Name; break; }
                    }
                }
                if (!string.IsNullOrEmpty(q) && !name.Contains(q, StringComparison.OrdinalIgnoreCase) && !id.ToString().Contains(q))
                    continue;
                var state = start is null || end is null ? "unknown"
                    : now < start ? "upcoming"
                    : now > (forceEnd ?? end) ? "ended" : "open";
                if (onlyActive && state == "ended") continue;
                rows.Add(new
                {
                    id,
                    name,
                    type,
                    startsAt = start?.ToString("yyyy-MM-dd HH:mm"),
                    endsAt = end?.ToString("yyyy-MM-dd HH:mm"),
                    forceEndsAt = forceEnd?.ToString("yyyy-MM-dd HH:mm"),
                    state,
                    disabled = EventDateOverlay.IsDisabled(id)
                });
            }
            return Results.Json(rows.OrderByDescending(r => r.GetType().GetProperty("startsAt")?.GetValue(r)?.ToString()).ToList());
        });

        // 单个活动开关（关闭的活动会在主数据里被改成已结束）
        gm.MapPost("/event-toggle", async (HttpContext context) =>
        {
            var id = ReadInt(context, "id");
            var enabled = (ReadInt(context, "enabled") ?? 1) != 0;
            if (id is null) return Results.BadRequest(new { error = "缺少 id" });
            EventDateOverlay.SetDisabled(id.Value, !enabled);
            await state.Logger.LogAsync($"gm-event-toggle id={id} enabled={enabled}");
            return Results.Json(new { ok = true, id, enabled });
        });

        // 卡池 ID -> 名称（GachaMaster）
        gm.MapGet("/masters/gachas", (HttpContext context) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "masterdata", "GachaMaster.json");
            if (!File.Exists(path)) return Results.NotFound(new { error = "GachaMaster.json 缺失", path });
            var map = new Dictionary<string, string>();
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                if (row[0].ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                var id = row[0].GetInt64().ToString();
                var name = row[1].ValueKind == System.Text.Json.JsonValueKind.String ? row[1].GetString() ?? "" : "";
                map[id] = name;
            }
            return Results.Json(map);
        });

        // 活动日期覆盖开关
        gm.MapGet("/event-overlay", () =>
        {
            var options = EventDateOverlay.ReadOptions();
            return Results.Json(new { enabled = options.Enabled, days = options.Days, path = EventDateOverlay.OptionsPath });
        });

        gm.MapPost("/event-overlay", (HttpContext context) =>
        {
            var enabled = ReadInt(context, "enabled");
            var days = ReadInt(context, "days");
            var current = EventDateOverlay.ReadOptions();
            var options = new EventDateOverlay.Options(
                enabled is null ? current.Enabled : enabled.Value != 0,
                days is null ? current.Days : days.Value,
                DateTimeOffset.UtcNow.ToString("O"));
            EventDateOverlay.WriteOptions(options);
            state.Logger.LogAsync($"gm-event-overlay enabled={options.Enabled} days={options.Days}").GetAwaiter().GetResult();
            return Results.Json(new { ok = true, enabled = options.Enabled, days = options.Days });
        });

        // 通用奖励发放：thingType/thingId/quantity 直接发放（调试 + 补发都可用）
        gm.MapGet("/reward/grant", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var thingType = ReadInt(context, "thingType") ?? 1;
            var thingId = (long)(ReadInt(context, "thingId") ?? 0);
            var quantity = ReadInt(context, "quantity") ?? 1;
            if (thingId <= 0 || quantity <= 0) return Results.Json(new { ok = false, error = "bad-args" });

            await using var connection = await state.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var present = await UserDataService.GrantRewardThingAsync(connection, transaction, userId, thingType, thingId, quantity);
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-reward-grant user={userId} type={thingType} id={thingId} qty={quantity}");
            return Results.Json(new { ok = true, userId, thingType, thingId, quantity, hasPresent = present is not null });
        });

        // 星等级奖励补发：把 base 的 1..rank 未领档位全部补发
        gm.MapGet("/reward/star-rank", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var baseId = long.Parse(context.Request.Query["baseId"].ToString());
            var rank = ReadInt(context, "rank") ?? 0;
            if (baseId <= 0 || rank <= 0) return Results.Json(new { ok = false, error = "bad-args" });

            await using var connection = await state.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var grant = await UserDataService.GrantStarRankRewardsAsync(connection, transaction, userId, baseId, rank);
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-reward-star-rank user={userId} base={baseId} rank={rank} ranks={grant.Ranks} things={grant.Things.Count}");
            return Results.Json(new { ok = true, userId, baseId, rank, grantedRanks = grant.Ranks, grantedThings = grant.Things.Count });
        });

        // 编成加成明细（服务端口径）：用于和客户端显示对照
        gm.MapGet("/event-bonus", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var eventId = (long)(ReadInt(context, "eventId") ?? 0);
            var partyId = (long)(ReadInt(context, "partyId") ?? 1004001);
            if (eventId == 0) eventId = ActiveEventIdFromFile();
            if (eventId == 0) return Results.Json(new { ok = false, error = "no-event" });
            var detail = await state.UserDataService.DescribeEventBonusAsync(userId, eventId, partyId);
            return Results.Json(new { ok = true, userId, eventId, partyId, bonus = detail[0], actors = detail[1] });
        });

        // 相册：重置编成（调试用）
        gm.MapGet("/album/reset", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var albumGroupId = (long)(ReadInt(context, "albumGroupId") ?? 1);
            var arrangementType = ReadInt(context, "arrangementType") ?? 1;
            var result = await state.UserDataService.ResetPhotoAlbumAsync(userId, albumGroupId, arrangementType);
            return Results.Json(new { ok = true, userId, albumGroupId, arrangementType, result });
        });

        // 三連劇：加积分 / 结算
        gm.MapGet("/triple-cast/point", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var tripleCastId = (long)(ReadInt(context, "tripleCastId") ?? 0);
            var points = ReadInt(context, "points") ?? 0;
            if (tripleCastId <= 0 || points == 0) return Results.Json(new { ok = false, error = "bad-args" });
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var total = await UserDataService.AddTripleCastPointsAsync(connection, transaction, userId, tripleCastId, points);
            await transaction.CommitAsync();
            return Results.Json(new { ok = true, userId, tripleCastId, points, total });
        });

        gm.MapGet("/triple-cast/settle", async (HttpContext context) =>
        {
            var tripleCastId = (long)(ReadInt(context, "tripleCastId") ?? 0);
            if (tripleCastId <= 0) return Results.Json(new { ok = false, error = "bad-args" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var (settled, ranked, rewarded) = await UserDataService.SettleTripleCastAsync(connection, tripleCastId);
            return Results.Json(new { ok = true, tripleCastId, settled, ranked, rewarded });
        });

        // リーグ：加积分 / 结算 / 查看状态（调试与补发用）
        gm.MapGet("/league/point", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var leagueId = (long)(ReadInt(context, "leagueId") ?? 0);
            var points = ReadInt(context, "points") ?? 0;
            if (leagueId <= 0 || points == 0) return Results.Json(new { ok = false, error = "bad-args" });
            await using var connection = await state.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var total = await UserDataService.AddLeaguePointsAsync(connection, transaction, userId, leagueId, points);
            await transaction.CommitAsync();
            return Results.Json(new { ok = true, userId, leagueId, points, total });
        });

        gm.MapGet("/league/settle", async (HttpContext context) =>
        {
            var leagueId = (long)(ReadInt(context, "leagueId") ?? 0);
            var classType = ReadInt(context, "class") ?? 3;
            if (leagueId <= 0) return Results.Json(new { ok = false, error = "bad-args" });
            await using var connection = await state.Database.OpenConnectionAsync();
            var (settled, ranked, rewarded) = await UserDataService.SettleLeagueAsync(connection, leagueId, classType);
            return Results.Json(new { ok = true, leagueId, classType, settled, ranked, rewarded });
        });

        gm.MapGet("/league/state", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            await using var connection = await state.Database.OpenConnectionAsync();
            await UserDataService.EnsureLeagueRewardTablesAsync(connection);
            var list = new List<object>();
            await using var command = new NpgsqlCommand(
                """
                select league_master_id, class_type, point, rank
                from user_league_states
                where "userId" = $1
                order by league_master_id desc
                """, connection);
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(new { leagueId = reader.GetInt64(0), classType = reader.GetInt32(1), point = reader.GetInt64(2), rank = reader.GetInt32(3) });
            return Results.Json(new { ok = true, userId, states = list });
        });

        // 活动奖励：手动补发累计 PT 档位 / 强制结算排名（GM 调试与修复用）
        gm.MapGet("/event-reward/grant", async (HttpContext context) =>
        {
            var userId = (long)(ReadInt(context, "userId") ?? 1);
            var eventId = (long)(ReadInt(context, "eventId") ?? 0);
            if (eventId == 0) eventId = ActiveEventIdFromFile();
            if (eventId == 0) return Results.Json(new { ok = false, error = "no-event" });

            await using var connection = await state.Database.OpenConnectionAsync();
            long point;
            await using (var command = new NpgsqlCommand(
                """
                select point from user_story_event_states
                where "userId" = $1 and event_master_id = $2
                """, connection))
            {
                command.Parameters.AddWithValue(userId);
                command.Parameters.AddWithValue(eventId);
                point = await command.ExecuteScalarAsync() is long value ? value : 0L;
            }

            await using var transaction = await connection.BeginTransactionAsync();
            var grant = await UserDataService.GrantEventTotalPointRewardsAsync(connection, transaction, userId, eventId, point);
            await transaction.CommitAsync();
            await state.Logger.LogAsync($"gm-event-reward-grant userId={userId} eventId={eventId} point={point} received={grant.ReceivedOrder}");
            return Results.Json(new
            {
                ok = true, userId, eventId, point,
                receivedOrder = grant.ReceivedOrder, dropCount = grant.Drops.Length
            });
        });

        gm.MapPost("/event-reward/settle", async (HttpContext context) =>
        {
            var eventId = (long)(ReadInt(context, "eventId") ?? 0);
            if (eventId == 0) eventId = ActiveEventIdFromFile();
            if (eventId == 0) return Results.Json(new { ok = false, error = "no-event" });

            await using var connection = await state.Database.OpenConnectionAsync();
            var result = await UserDataService.SettleStoryEventRankingAsync(connection, eventId, DateTime.UtcNow);
            await state.Logger.LogAsync($"gm-event-reward-settle eventId={eventId} settled={result.Settled} {result.Detail}");
            return Results.Json(new { ok = true, eventId, settled = result.Settled, detail = result.Detail });
        });

    }

    static long ActiveEventIdFromFile()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "active_events.json");
            if (!File.Exists(path)) return 0;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.TryGetInt64(out var eventId)) return eventId;
        }
        catch
        {
            // 配置坏了就当没有活动
        }
        return 0;
    }
    static async Task<List<string>> ListUserTablesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            select distinct c.table_name
            from information_schema.columns c
            join information_schema.tables t
              on t.table_schema = c.table_schema and t.table_name = c.table_name
            where c.table_schema = 'public' and c.column_name = 'userId' and t.table_type = 'BASE TABLE'
            order by c.table_name
            """, connection);
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    // ===== 理论值公式（规格来自参考服务端 PlayerProgressionRules）=====
    static decimal Bonus(decimal a) => a switch
    {
        >= 100.95m => 6m + (a - 100.95m),
        >= 100.75m => 4.5m + (a - 100.75m) * 7.5m,
        >= 100.5m => 3m + (a - 100.5m) * 6m,
        >= 100.25m => 2.25m + (a - 100.25m) * 3m,
        >= 100m => 1.5m + (a - 100m) * 3m,
        _ => (a - 98m) * 0.75m
    };

    static int Grade(decimal a) => a switch
    {
        >= 100.95m => 9, >= 100.75m => 8, >= 100.5m => 7, >= 100.25m => 6,
        >= 100m => 5, >= 98m => 4, >= 95m => 3, >= 90m => 2, >= 80m => 1, _ => 0
    };

    static decimal NotationRate(int level, decimal achievement)
    {
        if (level <= 0 || achievement < 0m || achievement > 101m) return 0m;
        decimal lv = level;
        decimal rate = achievement switch
        {
            < 80m => 0m,
            < 90m => lv * achievement / 180m,
            < 95m => lv * (0.5m + (achievement - 90m) / 20m),
            < 98m => lv * (0.75m + (achievement - 95m) / 12m),
            _ => lv + Bonus(achievement)
        };
        return Math.Floor(rate * 100m) / 100m;
    }

    // 读取 -> 修改 -> 校验 -> 写回 system_game_operations
    static async Task<IResult> MutateOperationsAsync(LocalServerState state,
        Func<System.Text.Json.Nodes.JsonNode, string?> mutate)
    {
        await using var connection = await state.Database.OpenConnectionAsync();
        await using var read = new NpgsqlCommand("select payload::text from system_game_operations where id=1", connection);
        var text = (string?)await read.ExecuteScalarAsync();
        if (text is null) return Results.NotFound(new { error = "operations not migrated" });

        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var error = mutate(node);
        if (error is not null) return Results.BadRequest(new { error });

        var candidate = node.ToJsonString();
        GameOperationsConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<GameOperationsConfiguration>(candidate, GameOperationsJson.Options);
            if (config is null) return Results.BadRequest(new { error = "配置为空" });
            GameOperationsValidation.Validate(config);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        await using var write = new NpgsqlCommand("""
            update system_game_operations set payload=$1::jsonb, revision=revision+1, updated_at=now()
            where id=1 returning revision
            """, connection);
        write.Parameters.AddWithValue(JsonSerializer.Serialize(config, GameOperationsJson.Options));
        var revision = Convert.ToInt64(await write.ExecuteScalarAsync() ?? 0L);
        await state.Logger.LogAsync($"gm-ops-mutate revision={revision}");
        return Results.Json(new { ok = true, revision });
    }

    // 主数据 id 列表（从 mastermemory 导出清单）
    static readonly long[] HomeSkinIds =
    {
        200001, 200101, 200102, 200103, 200104, 200105, 200106,
        200201, 200202, 200203, 200204, 200205,
        200301, 200302, 200303, 200304, 200305,
        200401, 200402, 200403, 200404, 200405
    };

    static readonly long[] IconFrameIds =
    {
        190001, 190002, 190003, 190004, 190005,
        190101, 190102, 190103, 190104, 190105, 190106,
        190201, 190202, 190203, 190204, 190205,
        190301, 190302, 190303, 190304, 190305,
        190401, 190402, 190403, 190404, 190405
    };

    static readonly long[] PosterIds =
    {
        210010, 210020, 210030, 210040, 210050, 210060, 210070, 210080, 210090, 210100,
        210110, 210120, 210130, 210140, 210150, 210160, 210170, 210180, 210190, 210200
    };

    // 道具分类：养成材料 / 饰品 / 票券 / 体力 / 限时 / 其他
    // GM 道具列表里不显示的东西：星rank加成和每期都会变的点数
    static bool HiddenItem(long id) =>
        (id >= 610000 && id < 630000)      // 演技力ボーナス / 稽古編成数+1
        || (id >= 4110000 && id < 4120000); // ガチャポイント（每期不同）

    static string ItemCategory(long id, string name)
    {
        // 饰品和碎片先判：它们的名字里常带「～」（如「★3【食べちゃうぞ～！】千寿暦のピース」），
        // 之前拿 ～ 当限时特征，会把这类正常道具误归到限时。
        // 活动专属的点数/券先归活动道具（名字里带 イベント）
        if (name.Contains("イベント", StringComparison.Ordinal)) return "event";
        if (name.Contains("チケット", StringComparison.Ordinal)) return "ticket";   // 所有券，含 139998/139999
        if (id >= 139000 && id < 140000) return "accessory";
        if (id >= 141000 && id < 146000) return "piece";      // ★1-★4 角色碎片
        if (id >= 151000 && id < 156000) return "piece";      // 万能/ダブルピース
        if (name.Contains("期限", StringComparison.Ordinal) || name.Contains("期間限定", StringComparison.Ordinal))
            return "limited";
        // 名称优先：带「ポイント」的是点数
        if (name.Contains("ポイント", StringComparison.Ordinal)) return "point";
        if (id >= 141000 && id < 146000) return "piece";      // ★1-★4 角色碎片
        if (id >= 151000 && id < 156000) return "piece";      // 万能/ダブルピース
        if (id >= 161000 && id < 164000) return "material";   // ブロマイド/カンバス/スタチュー
        if (id >= 120000 && id < 121000) return "material";   // 練習台本
        if (id >= 210000 && id < 223000) return "ticket";     // ガチャ券/交換券
        if (id >= 132000 && id < 133000) return "ticket";     // ルーレット券
        if (id >= 137000 && id < 138000) return "ticket";     // 福引券
        if (id >= 110000 && id < 113000) return "stamina";    // スタミナドリンク
        // 130021-130091 是养成材料：かけら/結晶（五色晶片）、名鑑、スターの煌き、
        // 額縁、導きのかけら、星のしずく、木材/カラーストーン、星の砂
        // 130011-130306（メダル/バトン/デコペン/オイル）按需求归养成材料；
        // 活动道具只留首(130002 シール)尾(130401 ゴールドバー)与活动专属点数/券
        if (id >= 130011 && id <= 130306) return "material";
        if (id == 130001) return "ticket";                    // 歌劇目録（换乐曲的券）
        if (id >= 130000 && id < 131000) return "event";      // メダル/シール等
        return "other";
    }

    static int CategoryRank(string category) => category switch
    {
        "material" => 0,
        "accessory" => 1,
        "event" => 2,
        "ticket" => 3,
        "stamina" => 4,
        "other" => 5,
        "limited" => 9,   // 限时的排最后
        _ => 8
    };

    static long UserScopedId(long userId, long masterId)
    {
        // 参考服务端的 id 是 64 位复合值：高 32 位用户、低 32 位主数据
        return (userId << 32) | (uint)masterId;
    }

    static bool IsLocalOrLan(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    static async Task<long?> FindUserIdAsync(NpgsqlConnection connection, string publicId)
    {
        await using var command = new NpgsqlCommand("""select id from user_accounts where public_id=$1 or name=$1""", connection);
        command.Parameters.AddWithValue(publicId);
        var value = await command.ExecuteScalarAsync();
        return value is null ? null : Convert.ToInt64(value);
    }

    static int? ReadInt(HttpContext context, string key)
    {
        var raw = context.Request.Query[key].ToString();
        return int.TryParse(raw, out var v) ? v : null;
    }
}

static class GmPage
{
    public const string Html = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>WDS GM</title>
<style>
 :root{color-scheme:dark}
/* --wds-theme2 : 世界大明星 私服控制台主题 -- */
/* --wds-theme3 : 活动图背景 + 劇団電姫 */
body{font-family:system-ui,-apple-system,'Segoe UI',sans-serif;margin:0;padding:18px 24px 70px;color:#eef2f8;
    background:#06080d fixed;position:relative;min-height:100vh}
body::before{content:"";position:fixed;inset:0;z-index:-2;
    background-image:var(--ev-bg, none);background-size:cover;background-position:center;
    opacity:.26;filter:saturate(1.05) blur(1.5px);transition:background-image .6s}
body::after{content:"";position:fixed;inset:0;z-index:-1;pointer-events:none;
    background:
      radial-gradient(1200px 800px at 10% -10%, rgba(20,40,70,.55), transparent 60%),
      radial-gradient(1000px 700px at 100% 0%, rgba(70,20,64,.5), transparent 58%),
      radial-gradient(1100px 900px at 50% 120%, rgba(8,44,42,.55), transparent 60%),
      linear-gradient(rgba(6,8,13,.72), rgba(6,8,13,.88))}
h1{font-size:0;margin:0 0 6px;height:0;overflow:hidden}
h2{font-size:13px;color:#9fb0c9;font-weight:700;margin:20px 0 8px;letter-spacing:.5px;
   display:flex;align-items:center;gap:8px}
h2::before{content:"";width:4px;height:14px;border-radius:2px;
  background:linear-gradient(180deg,#7fd8ff,#c07bff)}
.stat{color:#6d7484;font-size:12px;margin-bottom:14px}
 nav{display:flex;gap:4px;flex-wrap:wrap;border-bottom:1px solid #2a2e39;margin-bottom:16px}
 details.fold{margin:10px 0;border:1px solid #2a2e3a;border-radius:8px;padding:0}
details.fold>summary{cursor:pointer;padding:10px 14px;font-weight:600;color:#9ec4e8;list-style:none;user-select:none}
details.fold>summary::-webkit-details-marker{display:none}
details.fold>summary::before{content:"▶ ";font-size:11px}
details.fold[open]>summary::before{content:"▼ "}
details.fold>.card{margin:0;border:0;border-top:1px solid #2a2e3a}
nav button{background:none;border:none;border-bottom:2px solid transparent;color:#9aa0ae;
   padding:8px 14px;font-size:13px;cursor:pointer;border-radius:6px 6px 0 0}
 nav button:hover{color:#e8e8ee;background:#1a1d26}
 nav button.active{color:#9ec4e8;border-bottom-color:#9ec4e8}
 .tab{display:none}
 .tab.active{display:block}
 .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:10px 0}
 .lbl{color:#9aa0ae;font-size:12px;min-width:52px}
 input,button,textarea{background:#1e222c;color:#e8e8ee;border:1px solid #333a47;border-radius:6px;padding:6px 10px;font-size:13px}
 button{cursor:pointer}
 button:hover{background:#28303f}
 button.warn{border-color:#7a3b3b;color:#e79a9a}
 button.warn:hover{background:#2f1d1d}
 table{border-collapse:collapse;width:100%;font-size:12px;margin-top:8px}
 th,td{padding:5px 7px;border-bottom:1px solid #232733;text-align:left;white-space:nowrap}
 th{color:#8b93a3;font-weight:500;position:sticky;top:0;background:#11151a}
 tr:hover td{background:#1a1d26}
 code{color:#9ec4e8}
 .hint{color:#6d7484;font-size:12px}
 .ok{color:#7ddc9b}
 .err{color:#e08a8a}
 textarea{width:100%;box-sizing:border-box;font-family:ui-monospace,monospace;font-size:12px;padding:8px}
 .card{background:#151920;border:1px solid #242a35;border-radius:5px;padding:12px 14px;margin:10px 0}
 .pinrow{display:flex;gap:10px;flex-wrap:wrap;margin:8px 0 4px}
 .pincard{background:#161b23;border:1px solid #2b3444;border-left:3px solid #6f8fb5;border-radius:4px;
   padding:9px 14px;min-width:180px;cursor:pointer;line-height:1.45}
 .pincard:hover{background:#1b2230;border-left-color:#8fb3dd}
 .pincard b{display:block;font-size:13px;font-weight:600;color:#e6ebf3}
 .pincard span{font-size:11px;color:#7d8697;font-family:ui-monospace,monospace}
 .pincard em{font-style:normal;font-size:11px;color:#6f7a8b;margin-left:8px}
.card{background:linear-gradient(180deg,rgba(28,34,46,.92),rgba(20,24,33,.92));
  border:1px solid #2b3446;border-radius:12px;padding:14px 16px;margin:10px 0;
  box-shadow:0 2px 14px rgba(0,0,0,.35)}
.card:hover{border-color:#3b4a66}
input,select,textarea{background:#0e1119;border:1px solid #2b3446;color:#e8ecf3;
  border-radius:8px;padding:7px 10px;font-size:13px;outline:none;transition:.15s}
input:focus,select:focus,textarea:focus{border-color:#5eb4ff;box-shadow:0 0 0 2px rgba(94,180,255,.18)}
button{background:linear-gradient(180deg,#2a3548,#212a38);border:1px solid #35415a;color:#dbe4f0;
  border-radius:8px;padding:7px 14px;font-size:13px;cursor:pointer;transition:.15s}
button:hover{background:linear-gradient(180deg,#33415a,#28323f);border-color:#4a5c7d;color:#fff}
button:active{transform:translateY(1px)}
table{width:100%;border-collapse:collapse;font-size:12px}
th{text-align:left;color:#8fa0b8;font-weight:600;padding:7px 8px;border-bottom:1px solid #2b3446}
td{padding:6px 8px;border-bottom:1px solid #1d2330}
tr:hover td{background:rgba(94,180,255,.06)}
h2{color:#a7b3c9;letter-spacing:.4px}
nav button.active{color:#8fd0ff;border-bottom-color:#5eb4ff;
  background:linear-gradient(180deg,rgba(94,180,255,.18),transparent)}
details.fold{border-color:#2b3446;border-radius:10px;
  background:linear-gradient(180deg,rgba(24,29,40,.9),rgba(17,21,29,.9))}
details.fold>summary{color:#9edbf5}
details.fold>summary:hover{background:rgba(94,180,255,.07)}
/* 道具图标块（替代 emoji）与分组标题 */
.itico{display:inline-flex;width:42px;height:42px;border-radius:9px;align-items:center;justify-content:center;
  font-size:15px;font-weight:700;color:#fff;vertical-align:middle;
  box-shadow:0 2px 6px rgba(0,0,0,.5),inset 0 1px 0 rgba(255,255,255,.18);
  background:linear-gradient(135deg,#5a6b86,#333d52)}
img.itico{background:transparent;box-shadow:none;object-fit:contain;image-rendering:auto}
.itico[data-cat=material]{background:linear-gradient(135deg,#5a8fd6,#2f4f7a)}
.itico[data-cat=accessory]{background:linear-gradient(135deg,#c98be0,#6b3b8c)}
.itico[data-cat=event]{background:linear-gradient(135deg,#f0a45a,#8c5326)}
.itico[data-cat=ticket]{background:linear-gradient(135deg,#7ad1a8,#2f7a55)}
.itico[data-cat=piece]{background:linear-gradient(135deg,#8fb8f0,#3a5f96)}
.itico[data-cat=point]{background:linear-gradient(135deg,#f0d07a,#8c6a28)}
.itico[data-cat=stamina]{background:linear-gradient(135deg,#f08aa0,#8c3850)}
.itico[data-cat=limited]{background:linear-gradient(135deg,#e0c060,#8c7228)}
.gift-flex{display:flex;gap:18px;flex-wrap:wrap;align-items:flex-start}
.gift-form{flex:1;min-width:330px}
.gift-side{width:300px}
.gift-presets{display:grid;grid-template-columns:1fr 1fr;gap:6px;max-height:220px;overflow:auto}
.gift-presets button{font-size:11px;padding:4px 6px;text-align:left;white-space:nowrap;
  overflow:hidden;text-overflow:ellipsis}
.banner-thumb{width:132px;height:58px;object-fit:cover;border-radius:6px;border:1px solid #2b3446;
  background:#0e1119;display:block}
.banner-preview{max-width:260px;max-height:120px;object-fit:contain;border-radius:8px;
  border:1px solid #3b4a66;background:#0e1119;margin-top:6px}
tr.grp td{background:rgba(94,180,255,.10);color:#9edbf5;font-weight:700;font-size:12px;
  letter-spacing:.05em;padding:6px 8px;border-top:1px solid #2b3446}


/* ---- 游戏美术装饰 ---- */
.artwall{position:fixed;right:0;top:0;bottom:0;width:230px;pointer-events:none;z-index:0;
  display:flex;flex-direction:column;gap:8px;padding:10px 8px;overflow:hidden;
  mask-image:linear-gradient(180deg,transparent,#000 8%,#000 92%,transparent)}
.artwall img{width:100%;border-radius:10px;opacity:.5;object-fit:cover;
  box-shadow:0 4px 18px rgba(0,0,0,.5);transition:.4s}
.artwall img:hover{opacity:.95;transform:scale(1.03)}
body{padding-right:250px}
.hero{position:relative;height:96px;border-radius:14px;overflow:hidden;margin-bottom:14px;
  border:1px solid #2b3446;box-shadow:0 6px 24px rgba(0,0,0,.45)}
.hero img{width:100%;height:100%;object-fit:cover;object-position:center 32%;opacity:.72}
.hero .hero-text{position:absolute;inset:0;display:flex;align-items:center;padding:0 20px;
  background:linear-gradient(90deg,rgba(8,10,16,.88),rgba(8,10,16,.35),transparent)}
.hero .hero-text b{font-size:20px;letter-spacing:1px;
  background:linear-gradient(90deg,#ffd479,#ff8fb1,#7fd8ff);-webkit-background-clip:text;
  background-clip:text;color:transparent}
.card{position:relative;overflow:hidden}
.grade{display:inline-block;padding:1px 8px;border-radius:999px;font-size:11px;font-weight:600}
.g-pink{background:rgba(255,120,170,.18);color:#ff9ec4}
.g-blue{background:rgba(94,180,255,.18);color:#8fd0ff}
.g-gold{background:rgba(255,200,80,.18);color:#ffd479}
.g-green{background:rgba(90,220,160,.18);color:#7ee8b4}

/* ==== 顶栏 ==== */
.topbar{position:relative;display:flex;align-items:center;gap:14px;padding:14px 20px;
  border-radius:16px;overflow:hidden;margin-bottom:16px;
  border:1px solid rgba(140,180,255,.18);
  background:linear-gradient(100deg,rgba(30,48,84,.92),rgba(58,26,72,.9) 52%,rgba(16,58,60,.9));
  box-shadow:0 8px 30px rgba(0,0,0,.5)}
.topbar::after{content:"";position:absolute;inset:0;pointer-events:none;
  background:radial-gradient(600px 200px at 20% 0,rgba(127,216,255,.16),transparent 60%)}
.topbar .brand{position:relative;z-index:1}
.topbar .brand b{display:block;font-size:19px;letter-spacing:1.5px;
  background:linear-gradient(90deg,#ffd479,#ff8fb1 45%,#7fd8ff 80%);
  -webkit-background-clip:text;background-clip:text;color:transparent}
.topbar .brand span{font-size:11px;color:#93a4bd;letter-spacing:.8px}
.topbar .chibis{margin-left:auto;display:flex;align-items:flex-end;gap:2px;position:relative;z-index:1}
.topbar .chibis img{height:58px;filter:drop-shadow(0 4px 10px rgba(0,0,0,.5));transition:.25s}
.topbar .chibis img:hover{transform:translateY(-6px) scale(1.08)}

/* ==== 导航 ==== */
nav{display:flex;gap:8px;flex-wrap:wrap;border:0;margin-bottom:16px;padding:0}
nav button{background:linear-gradient(180deg,rgba(40,52,74,.9),rgba(26,33,46,.9));
  border:1px solid #2e3a52;color:#a9b8cd;padding:9px 20px;font-size:13px;font-weight:600;
  cursor:pointer;border-radius:999px;transition:.2s;letter-spacing:.4px}
nav button:hover{color:#fff;border-color:#5eb4ff;transform:translateY(-1px);
  box-shadow:0 6px 18px rgba(94,180,255,.22)}
nav button.active{color:#08121c;border-color:transparent;
  background:linear-gradient(135deg,#7fd8ff,#a9e8ff 45%,#ffd479);
  box-shadow:0 6px 22px rgba(127,216,255,.35)}

/* ==== 卡片 ==== */
.card{position:relative;background:linear-gradient(180deg,rgba(26,32,44,.9),rgba(17,21,29,.92));
  border:1px solid #26303f;border-radius:14px;padding:15px 17px;margin:10px 0;
  box-shadow:0 4px 20px rgba(0,0,0,.4);transition:.2s}
.card:hover{border-color:#3d5476;box-shadow:0 8px 28px rgba(60,120,200,.16)}
.card .deco{position:absolute;right:10px;bottom:6px;height:46px;opacity:.16;pointer-events:none}

/* ==== 控件 ==== */
input,select,textarea{background:#0c1018;border:1px solid #2b3648;color:#eef2f8;
  border-radius:9px;padding:8px 11px;font-size:13px;outline:none;transition:.16s}
input:focus,select:focus,textarea:focus{border-color:#7fd8ff;
  box-shadow:0 0 0 3px rgba(127,216,255,.16)}
button{background:linear-gradient(180deg,#2c3852,#1e2634);border:1px solid #36435c;
  color:#dce6f4;border-radius:9px;padding:8px 15px;font-size:13px;cursor:pointer;transition:.16s}
button:hover{background:linear-gradient(180deg,#374763,#26303f);border-color:#5b7bab;color:#fff;
  box-shadow:0 4px 14px rgba(91,123,171,.25)}
button:active{transform:translateY(1px)}
table{width:100%;border-collapse:collapse;font-size:12px}
th{text-align:left;color:#8fa2bb;font-weight:700;padding:8px;border-bottom:1px solid #2b3648}
td{padding:7px 8px;border-bottom:1px solid #1b2230}
tr:hover td{background:rgba(127,216,255,.06)}
details.fold{margin:10px 0;border:1px solid #26303f;border-radius:12px;padding:0;
  background:linear-gradient(180deg,rgba(23,28,38,.92),rgba(15,19,26,.92))}
details.fold>summary{cursor:pointer;padding:12px 16px;font-weight:700;color:#8fe0ff;
  list-style:none;user-select:none;border-radius:12px;transition:.15s}
details.fold>summary:hover{background:rgba(127,216,255,.07)}
details.fold>summary::-webkit-details-marker{display:none}
details.fold>summary::before{content:"\25B8 ";color:#5eb4ff}
details.fold[open]>summary::before{content:"\25BE "}
details.fold>.card{margin:0;border:0;border-top:1px solid #26303f;border-radius:0 0 12px 12px;box-shadow:none}
.stat{color:#7d8ba0;font-size:12px;margin-bottom:12px}
</style>
</head>
<body>
<div class="topbar">
  <div class="brand"><b>WORLD DAI STAR · GM</b><span>私服控制台 / LOCAL SERVER CONSOLE</span></div>
  <div class="chibis">
    <img src="/gm/art/200401.png" alt="">
    <img src="/gm/art/200402.png" alt="">
    <img src="/gm/art/200403.png" alt="">
    <img src="/gm/art/200404.png" alt="">
    <img src="/gm/art/200405.png" alt="">
  </div>
</div>
<h1>WDS GM</h1>
<div id="stat" class="stat"></div>

<nav>
  <button data-tab="home" class="active">主页</button>
    <button data-tab="account">账号</button>
    <button data-tab="grant">发放</button>
  <button data-tab="notice">公告</button>
  <button data-tab="ops">运营</button>
  <button data-tab="tools">工具</button>
  <button data-tab="danger">危险</button>
</nav>

<!-- ========== 账号 ========== -->
<section id="tab-account" class="tab">
  <div class="card">
    <div class="row">
      <span class="lbl">目标</span>
      <input id="pid" placeholder="publicId（如 5347680765）" size="26">
      <span class="hint">以下操作均作用于该账号</span>
    </div>
  </div>

  <h2>数值</h2>
  <div class="row">
    <span class="lbl">货币</span>
    <input id="free" placeholder="无偿石" size="10">
    <input id="paid" placeholder="有偿石" size="10">
    <input id="coin" placeholder="金币" size="12">
    <button onclick="setCurrency()">设为</button>
    <button onclick="fillCurrency()">读取当前</button>
    <span class="hint">直接改成输入值（留空=不改）</span>
  </div>
  <div class="row">
    <span class="lbl">等级</span>
    <input id="rank" placeholder="Rank" size="6">
    <button onclick="setRank()">改等级</button>
    <button onclick="maxAccount()">一键满级</button>
    <button onclick="bloomMax()">开花拉满</button>
  </div>
  <div class="row">
    <span class="lbl">教程</span>
    <input id="tut" placeholder="0=开始 99=完成" size="14">
    <button onclick="setTutorial()">改教程</button>
  </div>
  <div class="row">
    <span class="lbl">月卡</span>
    


<button onclick="monthlyPass(2001)">月卡 480 石</button>
    <button onclick="monthlyPass(3003)">スターパス</button>
    <button onclick="monthlyPass(3004)">ダイスターパス</button>
    <span class="hint">30 天，送对应有偿石</span>
  </div>
  <div class="row">
    <span class="lbl">改名</span>
    <input id="newname" placeholder="新名字" size="18">
    <button onclick="rename()">改名</button>
  </div>

  <h2>风控</h2>
  <div class="row">
    <button onclick="ban(1)">封禁 1 天</button>
    <button onclick="ban(7)">封禁 7 天</button>
    <button onclick="ban(30)">封禁 30 天</button>
    <button onclick="unban()">解封</button>
  </div>

  <details class="fold"><summary>常用账号</summary>

  <div id="pinned" class="pinrow"></div>
</details>
<details class="fold"><summary>账号列表</summary>

  <div class="row">
    <input id="aq" placeholder="搜索 publicId（留空=全部）" size="30">
    <button onclick="load()">搜索</button>
    <span class="hint">共 <span id="acount">-</span> 个</span>
  </div>
  <table id="list"><thead><tr>
    <th>ID</th><th>publicId</th><th>名字</th><th>Rank</th><th>无偿</th><th>有偿</th><th>金币</th>
    <th>教程</th><th>体力</th><th>卡</th><th>道具</th>
  </tr></thead><tbody></tbody></table>

  <!-- TABS_MERGED: 卡牌/道具/饰品已并入账号 -->
</details>
</section>

<!-- ========== 发放 / 物品 ========== -->
<section id="tab-grant" class="tab">
  <h2>发放 / 物品</h2>

<details class="fold"><summary>批量解锁</summary>

  <div class="row">
    <button onclick="unlockCards()">解锁全部角色卡（384 张）</button>
    <button onclick="unlockCostumes()">解锁全部服装（410 件）</button>
<button onclick="unlockPosters()">解锁全部海报（297 张）</button>
  </div>
  <div class="row">
    <span class="lbl">道具</span>
    <input id="qty" placeholder="数量" size="8" value="99">
    <button onclick="grantItems()">发放全部道具（1984 种）</button>
  </div>
</details>
<details class="fold"><summary>卡牌列表</summary>

  <div class="row">
    <input id="cq" placeholder="搜索卡名 / 角色 / ID" size="28">
    <select id="crarity" onchange="loadCards()">
      <option value="">全部星级</option>
      <option value="4">★★★★</option>
      <option value="3">★★★</option>
      <option value="2">★★</option>
      <option value="1">★</option>
    </select>
    <button onclick="loadCards()">搜索</button>
    <span class="hint">共 <span id="ccount">-</span> 张</span>
  </div>
  <table id="cards"><thead><tr>
    <th>图标</th><th>★</th><th>ID</th><th>卡名</th><th>角色</th><th>基础ID</th><th></th>
  </tr></thead><tbody><tr><td colspan="6" class="hint">加载中…</td></tr></tbody></table>
</details>
<details class="fold"><summary>按 ID 发放</summary>

  <div class="row">
    <span class="lbl">卡 ID</span>
    <input id="cmaster" placeholder="卡 masterId" size="12" value="110010">
    <button onclick="grantCard()">发这张卡</button>
  </div>
  <div class="row">
    <span class="lbl">育成</span>
    <input id="clv" placeholder="等级" size="6">
    <input id="csk" placeholder="技能等级" size="8">
    <input id="caw" placeholder="觉醒" size="6">
    <input id="cfr" placeholder="亲密度" size="8">
    <button onclick="setCard()">设置（留空不改）</button>
  </div>
</details>
<details class="fold"><summary>海报列表</summary>

  <div class="row">
    <input id="pq" placeholder="搜索海报名 / ID" size="26">
    <input id="pphase" placeholder="突破阶段" size="8" value="0">
    <button onclick="loadPosters()">搜索</button>
    <span class="hint">共 <span id="pcount">-</span> 张</span>
  </div>
  <table id="posters"><thead><tr>
    <th>图标</th><th>ID</th><th>海报名</th><th>角色</th><th>期间</th><th></th>
  </tr></thead><tbody><tr><td colspan="7" class="hint">加载中…</td></tr></tbody></table>
</details>
<details class="fold"><summary>外观</summary>

  <div class="row">
    <button onclick="unlockHomeSkins()">解锁全部主页皮肤（22）</button>
  </div>
  <div class="row">
    <span class="lbl">名片</span>
    <input id="cicon" placeholder="头像框 190001" size="14">
    <input id="cnp" placeholder="名片框（可选）" size="14">
    <input id="cskin" placeholder="主页皮肤 200101" size="15">
    <input id="cposter" placeholder="海报=角色卡ID 110010" size="18">
    <button onclick="setCosmetics()">设置外观</button>
  </div>
</details>
<details class="fold"><summary>货币</summary>

  <div class="row">
    <span class="lbl">💎 无偿石</span>
    <input id="cfree" placeholder="数量" size="10" value="300000">
    <button onclick="addCurrency('free')">设为</button>
    <span class="lbl">💠 有偿石</span>
    <input id="cpaid" placeholder="数量" size="10" value="0">
    <button onclick="addCurrency('paid')">设为</button>
  </div>
  <div class="row">
    <span class="lbl">🪙 金币</span>
    <input id="ccoin" placeholder="数量" size="12" value="30000000">
    <button onclick="addCurrency('coin')">设为</button>
    <span class="hint">直接写数据库，重新登录后生效</span>
  </div>
</details>
<details class="fold"><summary>道具</summary>

  <div class="row">
    <input id="iq" placeholder="搜索道具名或 ID（如 スタミナ / 110001）" size="30">
    <select id="icat" onchange="loadItems()">
      <option value="material" selected>养成材料</option>
      <option value="">全部分类</option>
      <option value="piece">碎片</option>
      <option value="ticket">券</option>
      <option value="point">点数</option>
      <option value="accessory">饰品</option>
      <option value="event">活动道具</option>
      <option value="stamina">体力</option>
      <option value="other">其他</option>
      <option value="limited">限时（排最后）</option>
    </select>
    <button onclick="loadItems()">搜索</button>
    <input id="iqty2" placeholder="数量" size="8" value="99">
    <span class="hint">共 <span id="icount">-</span> 个</span>
  </div>
  <table id="items"><thead><tr>
    <th>图标</th><th>ID</th><th>分类</th><th>名称</th><th>上限</th><th></th>
  </tr></thead><tbody><tr><td colspan="6" class="hint">加载中…</td></tr></tbody></table>
</details>
<details class="fold"><summary>批量（JSON）</summary>

  <textarea id="batch" rows="6">[
  {"thingType":1,"masterId":110001,"quantity":10,"message":"GM 批量"},
  {"thingType":1,"masterId":110002,"quantity":5}
]</textarea>
  <div class="row">
    <button onclick="sendInboxBatch()">批量发送</button>
    <span class="hint">也可以直接在上面的道具表里点「发放」</span>
  </div>
</details>
<details class="fold"><summary>饰品（劇団電姫のおまもり）</summary>

  <div class="row">
    <input id="aq2" placeholder="搜索饰品名或 ID" size="28">
    <button onclick="loadAccessories()">搜索</button>
    <input id="aqty" placeholder="数量" size="8" value="1">
    <button onclick="sendSelectedAccessories()">发放选中</button>
    <button onclick="selectAllAccessories()">全选</button>
    <button onclick="clearAccessories()">清空</button>
    <span class="hint">共 <span id="acount2">-</span> 个（☑ 可多选）</span>
  </div>
  <table id="accs"><thead><tr>
    <th>选</th><th>ID</th><th>名称</th><th>限时</th><th></th>
  </tr></thead><tbody><tr><td colspan="5" class="hint">加载中…</td></tr></tbody></table>
</details>
</section>

<!-- ========== 卡牌 ========== -->


<!-- ========== 奖励 ========== -->


<!-- ========== 饰品 ========== -->


<!-- ========== 主页 ========== -->
<section id="tab-home" class="tab active">
  <h2>服务器概览</h2>
  <div class="card">
    <div class="row" style="justify-content:space-between">
      <div><b>地址</b> <span id="hm-addr" class="hint">加载中…</span></div>
      <div><b>启动时间</b> <span id="hm-start" class="hint">—</span></div>
    </div>
    <div class="row" style="justify-content:space-between">
      <div><b>端口</b> <span id="hm-ports" class="hint">—</span></div>
      <div><button onclick="loadDashboard()">刷新</button></div>
    </div>
  </div>

  <h2>主数据</h2>
  <div class="card">
    <div class="row" style="justify-content:space-between">
      <div><b>版本</b> <span id="hm-ver" class="hint">—</span></div>
      <div><b>大小</b> <span id="hm-size" class="hint">—</span></div>
    </div>
    <div class="row"><span class="hint" id="hm-file"></span></div>
  </div>

  </section>

<!-- ========== 公告 ========== -->
<section id="tab-notice" class="tab">
  <h2>公告管理</h2>
  <div class="card">
    <div class="row">
      <span class="lbl">ID</span>
      <input id="nid" placeholder="留空=新建" size="8">
      <span class="lbl">标题</span>
      <input id="ntitle" placeholder="标题" size="34">
    </div>
    <div class="row">
      <span class="lbl">正文</span>
      <input id="nbody" placeholder="正文（可空）" size="70">
    </div>
    <div class="row">
      <span class="lbl">图片</span>
      <select id="nbanner" style="min-width:280px" onchange="previewBanner()"></select>
      <button onclick="loadBanners()">刷新图片</button>
      <span class="hint">Banners 目录下的图</span>
    </div>
    <div class="row">
      <span class="lbl">预览</span>
      <img id="nbanner-preview" class="banner-preview" style="display:none" alt="">
    </div>
    <div class="row">
      <span class="lbl">分类</span>
      <select id="ntab">
        <option value="1">通知</option>
        <option value="2">公告</option>
        <option value="3">礼物</option>
      </select>
      <span class="lbl">顺序</span>
      <input id="norder" placeholder="0" size="6" value="0">
      <label class="hint"><input type="checkbox" id="nconfirm"> 登录弹窗（只弹一次）</label>
      <button onclick="saveNotification()">保存</button>
      <button onclick="resetNotification()">清空表单</button>
    </div>
  </div>
  <h2>全服补偿（发到每个账号的礼物盒）</h2>
  <div class="card">
    <div class="gift-flex">
      <div class="gift-form">
        <div class="row">
          <span class="lbl">物品</span>
          <input id="gmid" placeholder="道具 ID（如 140000）" size="14">
          <input id="gtype" type="hidden" value="1">
          <button onclick="lookupGiftItem()">查名字</button>
          <span id="gname" class="hint">—</span>
        </div>
        <div class="row">
          <span class="lbl">数量</span>
          <input id="gqty" placeholder="数量" size="8" value="1">
        </div>
        <div class="row">
          <span class="lbl">附文</span>
          <input id="gmsg" placeholder="显示在礼物盒里的说明" size="42" value="运营补偿">
        </div>
        <div class="row">
          <button onclick="broadcastGift()">发送给全服</button>
          <span class="hint">所有账号各一份，登录后在礼物盒领取</span>
        </div>
      </div>
      <div class="gift-side">
        <div class="hint" style="margin-bottom:6px">常用 ID（点一下填入）</div>
        <div id="gift-presets" class="gift-presets"></div>
      </div>
    </div>
  </div>

  <div class="row">
    <button onclick="loadNotifications()">刷新列表</button>
    <span class="hint">共 <span id="ncount">-</span> 条</span>
  </div>
  <table id="ntable"><thead><tr>
    <th>ID</th><th>图片</th><th>标题</th><th>分类</th><th>弹窗</th><th>顺序</th><th></th>
  </tr></thead><tbody><tr><td colspan="7" class="hint">加载中…</td></tr></tbody></table>
</section>

<!-- ========== 运营 ========== -->
<section id="tab-ops" class="tab">
  <h2>活动管理</h2>
  <div class="card">
    <div class="row" style="flex-wrap:wrap;gap:8px;align-items:center">
      <button onclick="openAllEvents()" style="background:linear-gradient(90deg,#ff7a59,#ff4d6d);color:#fff;font-weight:700;padding:8px 16px;border-radius:8px;border:0;cursor:pointer">🎉 一键开启全部活动</button>
      活动天数 <input id="eventDays" type="number" value="14" min="1" max="90" style="width:60px;padding:6px">
      结算天数 <input id="settleDays" type="number" value="2" min="0" max="30" style="width:60px;padding:6px">
      <select id="eventPicker" style="min-width:320px;padding:8px;border-radius:8px"></select>
<button onclick="openOneEvent()">开启选中的活动</button>
<button onclick="closeOneEvent()">关闭活动</button>
<button onclick="endOneEvent()">结束活动（进结算）</button>
    </div>
  </div>

  <details class="fold"><summary>活动开关</summary>
<div class="card">
    <div class="row" style="justify-content:space-between">
      <div><b>卡池</b> <span id="sumGacha" class="hint">加载中…</span></div>
      <div><button onclick="gachas(1)">全部开启</button> <button onclick="gachas(0)">全部关闭</button></div>
    </div>
    <div class="row" style="justify-content:space-between">
      <div><b>登录奖励</b> <span id="sumLogin" class="hint">加载中…</span></div>
      <div><button onclick="allLoginBonus(1)">全部开启</button> <button onclick="allLoginBonus(0)">全部关闭</button></div>
    </div>
    <div class="row" style="justify-content:space-between">
      <div><b>任务通行证</b> <span id="sumPass" class="hint">加载中…</span></div>
      <div><button onclick="toggleMissionPass()">切换</button></div>
    </div>
    <p class="hint">改动立即写入数据库，客户端重新登录/刷新后生效。</p>
  </div>
</details>

  <details class="fold"><summary>活动日期覆盖（主数据）</summary>
<div class="card">
    <div class="row" style="justify-content:space-between">
      <div>状态 <span id="ovState" class="hint">读取中…</span></div>
      <div>
        持续天数 <input id="ovDays" size="5" value="30">
        <button onclick="saveOverlay(1)">开启并保存</button>
        <button onclick="saveOverlay(0)">关闭</button>
      </div>
    </div>
    <p class="hint">把主数据里已结束的活动改成「现在开放」，客户端重新下载主数据后生效。只改 EventMaster，其他表不变。</p>
  </div>
</details>

  <details class="fold"><summary>详情</summary>

  <details class="card">
    <summary>卡池清单（<span id="dCountGacha">-</span> 个）</summary>
    <div class="row">
      <input id="gq" placeholder="搜索卡池 ID" size="18" oninput="filterGacha()">
      <span class="hint">只列出已开启的卡池可以点「关闭」</span>
    </div>
    <table id="gtable"><thead><tr><th>卡池 ID</th><th>名称</th><th>状态</th><th></th></tr></thead>
      <tbody><tr><td colspan="4" class="hint">展开后加载…</td></tr></tbody></table>
  </details>

  <details class="card">
    <summary>登录奖励清单（<span id="dCountLogin">-</span> 个，可编辑）</summary>
    <p class="hint">
      登录奖励 = 玩家连续登录每天领一次的奖励（比如 7 日 / 15 日签到）。
      「天数」是整期有几天；「类型 / 奖励ID / 数量」是每天发的东西；「循环」= 领完自动从头再来。
      改完点该行的「保存」。
    </p>
    <table id="lgtable"><thead><tr>
      <th>签到</th><th>开始</th><th>结束</th><th>天数</th>
      <th>类型</th><th>奖励ID</th><th>数量</th><th>循环</th><th>启用</th><th></th>
    </tr></thead><tbody><tr><td colspan="10" class="hint">展开后加载…</td></tr></tbody></table>
  </details>

  <details class="card">
    <summary>活动一览（来自主数据 EventMaster）</summary>
    <div class="row">
      <input id="eq" placeholder="搜索活动名 / ID" size="22">
      <button onclick="loadEvents()">搜索</button>
      <label class="hint"><input type="checkbox" id="eactive" checked onchange="loadEvents()"> 只看未结束</label>
      <span class="hint">共 <span id="ecount">-</span> 期 · 活动系统尚未实现，这里只展示官方期间</span>
    </div>
    <table id="etable"><thead><tr>
      <th>ID</th><th>类型</th><th>名称</th><th>开始</th><th>结束</th><th>状态</th><th>开关</th>
    </tr></thead><tbody><tr><td colspan="7" class="hint">展开后加载…</td></tr></tbody></table>
  </details>

  <details class="card">
    <summary>活动配置 JSON（高级）</summary>
    <div class="row">
      <button onclick="loadOps()">读取</button>
      <button onclick="saveOps()">保存</button>
      <span id="opsrev" class="hint"></span>
      <span class="hint">整块配置，含卡池/登录奖励/通行证，保存前会校验</span>
    </div>
    <textarea id="ops" rows="12"></textarea>
  </details>
</details>
<details class="fold"><summary>重置</summary>

  <div class="row">
    <button onclick="resetLoginBonus()">登录奖励领取记录</button>
    <button onclick="resetDaily()">每日限制</button>
    <button onclick="resetMissions()">任务状态</button>
    <button onclick="resetShop()">商店购买次数</button>
  </div>
</details>
</section>

<!-- ========== 工具 ========== -->
<section id="tab-tools" class="tab">
  <h2>理论值计算器</h2>
  <div class="row">
    <input id="rlevel" placeholder="谱面等级" size="9">
    <input id="rach" placeholder="达成率 %" size="10">
    <button onclick="calcRate()">计算</button>
    <span id="rout" class="hint"></span>
  </div>

  <h2>模拟打歌成绩</h2>
  <div class="row">
    <input id="plive" placeholder="曲目 masterId" size="12" value="101">
    <input id="pdiff" placeholder="难度 1-4" size="8" value="3">
    <input id="pach" placeholder="达成率 %" size="10" value="100">
    <input id="plv" placeholder="谱面等级" size="9" value="25">
    <button onclick="simulatePlay()">写入成绩</button>
  </div>
  <div class="row">
    <button onclick="showRating()">刷新理论值汇总（top30）</button>
    <span id="ratingOut" class="hint"></span>
  </div>
</section>

<!-- ========== 危险 ========== -->
<section id="tab-danger" class="tab">
  <div class="card">
    <p class="hint">这里的操作<b>不可撤销</b>。为避免误触，此页使用<b>独立的账号输入框</b>，与「账号」页的目标账号互不影响。</p>
    <div class="row">
      <span class="lbl">危险目标</span>
      <input id="dpid" placeholder="要操作的 publicId（独立于账号页）" size="30">
    </div>
    <div class="row">
      <button class="warn" onclick="resetAccount()">重置账号进度</button>
      <button class="warn" onclick="deleteAccount()">删除账号</button>
    </div>
    <p class="hint">重置 = 清空全部游戏数据但保留账号；删除 = 连账号一起删掉。</p>
  </div>
</section>

<script>
const $ = id => document.getElementById(id);
const q = id => encodeURIComponent($(id).value.trim());
const pid = () => $('pid').value.trim();

document.querySelectorAll('nav button').forEach(b => b.onclick = () => {
  document.querySelectorAll('nav button').forEach(x => x.classList.remove('active'));
  document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
  b.classList.add('active');
  $('tab-' + b.dataset.tab).classList.add('active');
  if (b.dataset.tab === 'home') loadDashboard();
if (b.dataset.tab === 'account') { load(); loadStatus(); }
  // 卡牌 / 海报 / 道具 / 饰品 都在「发放」页，切过去时按默认分类加载
  if (b.dataset.tab === 'grant') { loadCards(); loadPosters(); loadItems(); loadAccessories(); }
  if (b.dataset.tab === 'notice') { loadNotifications(); loadBanners(); renderGiftPresets(); }
  if (b.dataset.tab === 'ops') { loadOps(); loadOpsList(); loadEvents(); loadOverlay(); }
  if (b.dataset.tab === 'tools') showRating();
});

async function loadStatus(){
  try {
    const j = await (await fetch('/gm/status')).json();
    $('stat').textContent = `账号 ${j.accounts} · 会话 ${j.sessions} · 未读包裹 ${j.inboxUnread} · 卡池 ${j.gachasOn}/${j.gachasTotal} 开 · 活动 revision ${j.operationsRevision} · 成绩 ${j.liveResults}`;
  } catch(e){}
}
function pick(publicId){
  $('pid').value = publicId;
}
async function load(){
  const r = await fetch('/gm/accounts?q=' + encodeURIComponent($('aq').value.trim()));
  const rows = await r.json();
  $('acount').textContent = rows.length;
  const pinned = rows.filter(a => a.id <= 2);
  const rest   = rows.filter(a => a.id > 2);
  $('pinned').innerHTML = pinned.map(a =>
    `<div class="pincard" onclick="pick('${a.publicId}')">
       <b>${a.displayName ?? a.accountName}</b>
       <span>${a.publicId}</span><em>Rank ${a.rank} · ${a.cards} 卡</em>
     </div>`).join('');
  document.querySelector('#list tbody').innerHTML = rest.map(a =>
    `<tr><td>${a.id}</td><td><code>${a.publicId}</code></td><td>${a.displayName ?? a.accountName}</td>
     <td>${a.rank}</td><td>${a.freeJewel}</td><td>${a.paidJewel}</td><td>${a.coin}</td>
     <td>${a.tutorialStatus}${a.tutorialFinished ? ' ✓' : ''}</td><td>${a.stamina}/${a.maxStamina}</td>
     <td>${a.cards}</td><td>${a.items}</td></tr>`).join('') || '<tr><td colspan="11" class="hint">没有匹配的账号</td></tr>';
}
async function post(path, silent){
  const r = await fetch(path, {method:'POST'});
  let msg = r.ok ? '已执行' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  if(!silent) alert(msg);
  load(); loadStatus();
  return r.ok;
}
function needPid(){ if(!pid()){ alert('先在「账号」页填 publicId'); return false; } return true; }

/* 账号 */
function setCurrency(){
  if(!needPid()) return;
  const p=[];
  for(const k of ['free','paid','coin']){ const v=$(k).value.trim(); if(v) p.push(k+'='+v); }
  if(!p.length){ alert('至少填一项'); return; }
  post(`/gm/currency?publicId=${q('pid')}&`+p.join('&'));
}
/* 把该账号当前的货币读进输入框，方便直接改数字 */
async function fillCurrency(){
  if(!needPid()) return;
  try{
    const rows = await (await fetch('/gm/accounts?q=' + encodeURIComponent(pid()))).json();
    const row = rows.find(r => String(r.publicId) === String(pid())) || rows[0];
    if(!row){ alert('找不到账号'); return; }
    $('free').value = row.freeJewel ?? 0;
    $('paid').value = row.paidJewel ?? 0;
    $('coin').value = row.coin ?? 0;
    if($('cfree')) $('cfree').value = row.freeJewel ?? 0;
    if($('cpaid')) $('cpaid').value = row.paidJewel ?? 0;
    if($('ccoin')) $('ccoin').value = row.coin ?? 0;
  }catch(e){ alert('读取失败: ' + e); }
}
function setRank(){
  if(!needPid()) return;
  const v = $('rank').value.trim();
  if(!v){ alert('填 Rank'); return; }
  post(`/gm/rank?publicId=${q('pid')}&rank=` + v);
}
function setTutorial(){ if(needPid()) post(`/gm/tutorial?publicId=${q('pid')}&status=${q('tut')}`); }
function rename(){ if(needPid()) post(`/gm/name?publicId=${q('pid')}&name=${q('newname')}`); }
function ban(days){ if(needPid() && confirm(`封禁 ${pid()} ${days} 天？`)) post(`/gm/ban?publicId=${q('pid')}&days=${days}`); }
function unban(){ if(needPid()) post(`/gm/unban?publicId=${q('pid')}`); }
async function monthlyPass(itemId){
  if(!needPid()) return;
  const name = itemId === 2001 ? 'ログインパス' : itemId === 3003 ? 'スターパス' : 'ダイスターパス';
  if(confirm(`给 ${pid()} 发放「${name}」？`)) post(`/gm/monthly-pass?publicId=${q('pid')}&itemId=${itemId}`);
}
function maxAccount(){ if(needPid() && confirm(`把 ${pid()} 一键满级？`)) post(`/gm/max?publicId=${q('pid')}`); }
function bloomMax(){ if(needPid() && confirm(`把 ${pid()} 的所有角色才能开花拉满？`)) post(`/gm/bloom-max?publicId=${q('pid')}`); }
function openAllEvents(){
  if(!confirm('开启全部活动（209 期）+ 全部奖励期？\n\n会重新生成主数据并重启服务，客户端需要重启下载。')) return;
  const btn = event.target; if(btn){ btn.disabled = true; btn.textContent = '生成中…'; }
  fetch('/gm/events/open?all=1&days=30', {method:'POST'})
    .then(r => r.json())
    .then(j => {
      alert(j.ok ? ('已生成活动主数据\n版本: ' + j.version + '\n\n请重启客户端，等待主数据下载完成。\n（服务端会自动重启，约 5 秒）')
                 : ('失败: ' + (j.error || '未知错误')));
    })
    .catch(e => alert('请求失败: ' + e))
    .finally(() => { if(btn){ btn.disabled = false; btn.textContent = '🎉 一键开启全部活动'; } });
}

/* 卡牌 */
function unlockCards(){ if(needPid() && confirm('解锁全部角色卡？')) post(`/gm/unlock-cards?publicId=${q('pid')}`); }
function unlockCostumes(){ if(needPid() && confirm('解锁全部服装？')) post(`/gm/unlock-costumes?publicId=${q('pid')}`); }
function grantItems(){ if(needPid()) post(`/gm/grant-items?publicId=${q('pid')}&quantity=${q('qty')}`); }
function grantCard(){ if(needPid()) post(`/gm/card?publicId=${q('pid')}&masterId=${q('cmaster')}`); }
function unlockHomeSkins(){
  if(needPid() && confirm('解锁全部主页皮肤？')) post(`/gm/unlock-home-skins?publicId=${q('pid')}`);
}
function setCosmetics(){
  if(!needPid()) return;
  const p = [`publicId=${q('pid')}`];
  for(const [id,k] of [['cicon','iconFrame'],['cnp','nameplate'],['cskin','homeSkin'],['cposter','poster']]){
    const v = $(id).value.trim(); if(v) p.push(k+'='+v);
  }
  if(p.length === 1){ alert('至少填一项'); return; }
  post('/gm/cosmetics?' + p.join('&'));
}
function setCard(){
  if(!needPid()) return;
  const p = [`publicId=${q('pid')}`, `masterId=${q('cmaster')}`];
  for(const [id,k] of [['clv','level'],['csk','skillLevel'],['caw','awakening'],['cfr','friendship']]){
    const v = $(id).value.trim(); if(v) p.push(k+'='+v);
  }
  post('/gm/card-set?' + p.join('&'));
}

/* 奖励 */
async function addCurrency(kind){
  if(!needPid()) return;
  const map = {free:['cfree','free'], paid:['cpaid','paid'], coin:['ccoin','coin']};
  const [inputId, param] = map[kind];
  const v = $(inputId).value.trim();
  if(!v){ alert('填数量'); return; }
  const r = await fetch(`/gm/currency?publicId=${encodeURIComponent(pid())}&${param}=${v}`, {method:'POST'});
  let msg = r.ok ? '已设为输入值' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg); load(); loadStatus();
}
let posterRows = [];
async function loadPosters(){
  const q = $('pq').value.trim();
  const r = await fetch('/gm/masters/posters?limit=300&q=' + encodeURIComponent(q));
  posterRows = await r.json();
  $('pcount').textContent = posterRows.length;
  document.querySelector('#posters tbody').innerHTML = posterRows.map((p, idx) => {
    const chars = (p.characters || []).map(c => c.name).join('、') || '—';
    const period = p.endsAt ? `${(p.startsAt||'').slice(0,10)} → ${p.endsAt.slice(0,10)}` : '—';
    return `<tr><td><img class="itico" src="/gm/img/poster/${p.id}.png" alt="" loading="lazy"
        onerror="this.style.visibility='hidden'"></td>
      <td><code>${p.id}</code></td><td>${p.name}</td><td>${chars}</td><td>${period}</td>
      <td><button onclick="grantPosterRow(${idx})">发放</button></td></tr>`;
  }).join('') || '<tr><td colspan="6" class="hint">没有匹配的海报</td></tr>';
}
function grantPosterRow(idx){
  if(!needPid()) return;
  const p = posterRows[idx];
  const phase = Number($('pphase').value.trim()) || 0;
  if(!confirm(`给 ${pid()} 发海报「${p.name}」（突破 ${phase}）？`)) return;
  post(`/gm/poster?publicId=${q('pid')}&masterId=${p.id}&phase=${phase}`);
}
let cardRows = [];
async function loadCards(){
  const q = $('cq').value.trim();
  const rar = $('crarity').value;
  const url = `/gm/masters/cards?limit=400&q=${encodeURIComponent(q)}` + (rar ? `&rarity=${rar}` : '');
  const r = await fetch(url);
  cardRows = await r.json();
  $('ccount').textContent = cardRows.length;
  document.querySelector('#cards tbody').innerHTML = cardRows.map((c, idx) =>
    `<tr><td><img class="itico" src="/gm/img/card/${c.id}_0.png" alt="" loading="lazy"
        onerror="this.style.visibility='hidden'"></td>
      <td>${'★'.repeat(c.rarity)}</td><td><code>${c.id}</code></td><td>${c.name}</td>
      <td>${c.baseName}</td><td>${c.baseId}</td>
      <td><button onclick="grantCardRow(${idx})">发放</button></td></tr>`
  ).join('') || '<tr><td colspan="7" class="hint">没有匹配的卡牌</td></tr>';
}
function grantCardRow(idx){
  if(!needPid()) return;
  const c = cardRows[idx];
  if(!confirm(`给 ${pid()} 发「${c.name}」(${c.baseName})？`)) return;
  post(`/gm/card?publicId=${q('pid')}&masterId=${c.id}`);
}
let accRows = [];
const accSelected = new Set();
const GIFT_PRESETS = [
  { id: 140000, name: 'ジュゴン像（开花）', type: 1 },
  { id: 150000, name: '金のジュゴン像', type: 1 },
  { id: 130021, name: '愛憐のかけら', type: 1 },
  { id: 130022, name: '蒼凛のかけら', type: 1 },
  { id: 130023, name: '翠彩のかけら', type: 1 },
  { id: 130024, name: '陽光のかけら', type: 1 },
  { id: 130025, name: '夢幻のかけら', type: 1 },
  { id: 130031, name: '愛憐の結晶', type: 1 },
  { id: 130032, name: '蒼凛の結晶', type: 1 },
  { id: 130033, name: '翠彩の結晶', type: 1 },
  { id: 130034, name: '陽光の結晶', type: 1 },
  { id: 130035, name: '夢幻の結晶', type: 1 },
  { id: 130041, name: '役者名鑑', type: 1 },
  { id: 130042, name: '花形名鑑', type: 1 },
  { id: 130043, name: 'スター名鑑', type: 1 },
  { id: 130051, name: 'スターの煌き', type: 1 },
  { id: 130061, name: '銅の額縁', type: 1 },
  { id: 130062, name: '銀の額縁', type: 1 },
  { id: 130063, name: '金の額縁', type: 1 },
  { id: 130091, name: '星の砂', type: 1 },
  { id: 130001, name: '歌劇目録', type: 1 },
  { id: 130011, name: '蒼玉のメダル', type: 1 },
  { id: 130012, name: '紅玉のメダル', type: 1 },
  { id: 130013, name: '翠玉のメダル', type: 1 },
  { id: 130014, name: '黄玉のメダル', type: 1 },
  { id: 130015, name: '紫玉のメダル', type: 1 },
  { id: 0, name: '无偿石', type: 13 },
  { id: 0, name: '有偿石', type: 12 }
];
function renderGiftPresets(){
  const box = $('gift-presets');
  if (!box) return;
  box.innerHTML = GIFT_PRESETS.map((p, i) =>
    `<button onclick="pickGiftPreset(${i})" title="${p.name} ${p.id || ''}">${p.name}</button>`).join('');
}
function pickGiftPreset(i){
  const p = GIFT_PRESETS[i];
  $('gtype').value = String(p.type);
  $('gmid').value = p.id ? String(p.id) : '';
  $('gmid').disabled = p.type !== 1;
  $('gname').textContent = p.type === 1 ? p.name : `${p.name}（货币，不用 ID）`;
}
async function lookupGiftItem(){
  const id = $('gmid').value.trim();
  if (!id) { $('gname').textContent = '—'; return; }
  const rows = await (await fetch('/gm/masters/items?limit=20&q=' + encodeURIComponent(id))).json();
  const hit = rows.find(r => String(r.id) === id) || rows[0];
  $('gname').textContent = hit ? `${hit.name}（${hit.id}）` : '没找到';
}
async function broadcastGift(){
  const id = $('gmid').value.trim();
  const qty = $('gqty').value.trim() || '1';
  const msg = $('gmsg').value.trim() || '运营补偿';
  const type = $('gtype') ? ($('gtype').value || '1') : '1';
  if (type === '1' && !id) { alert('先填道具 ID'); return; }
  if (!confirm(`给全服每个账号发「${id}」× ${qty}？\n附文：${msg}`)) return;
  const r = await fetch('/gm/broadcast-gift?masterId=' + encodeURIComponent(id || '0') +
    '&quantity=' + encodeURIComponent(qty) + '&thingType=' + encodeURIComponent(type) +
    '&message=' + encodeURIComponent(msg), {method:'POST'});
  let sent = '?';
  try { const j = await r.json(); if (j && j.sent != null) sent = j.sent; } catch(e){}
  alert(r.ok ? `已发放到 ${sent} 个账号的礼物盒` : ('失败 ' + r.status));
}
let notificationRows = [];
let bannerList = [];
async function loadNotifications(){
  const rows = await (await fetch('/gm/notifications')).json();
  notificationRows = rows;
  $('ncount').textContent = rows.length;
  document.querySelector('#ntable tbody').innerHTML = rows.map((n, i) =>
    `<tr><td><code>${n.id}</code></td>
      <td><img class="banner-thumb" src="/gm/bannerimg/${encodeURIComponent(n.banner)}.png" alt=""
           onerror="this.style.visibility='hidden'"></td>
      <td>${n.title}</td><td>${n.tab}</td>
      <td>${n.isConfirmation ? '<span class="ok">是</span>' : '否'}</td>
      <td>${n.order}</td>
      <td><button onclick="editNotification(${i})">编辑</button>
          <button class="warn" onclick="deleteNotification(${n.id})">删除</button></td></tr>`
  ).join('') || '<tr><td colspan="7" class="hint">没有公告</td></tr>';
}
function editNotification(i){
  const n = notificationRows[i];
  $('nid').value = n.id;
  $('ntitle').value = n.title;
  $('nbody').value = n.body || '';
  $('ntab').value = String(n.tab || 1);
  $('norder').value = n.order || 0;
  $('nconfirm').checked = !!n.isConfirmation;
  loadBanners().then(() => { $('nbanner').value = n.banner || ''; });
}
function resetNotification(){
  $('nid').value = ''; $('ntitle').value = ''; $('nbody').value = '';
  $('norder').value = 0; $('nconfirm').checked = false; $('nbanner').value = '';
}
function previewBanner(){
  const v = $('nbanner').value;
  const img = $('nbanner-preview');
  if (!img) return;
  if (!v) { img.style.display = 'none'; return; }
  img.style.display = '';
  img.src = '/gm/bannerimg/' + encodeURIComponent(v) + '.png';
}
async function loadBanners(){
  if (!bannerList.length) bannerList = await (await fetch('/gm/banners')).json();
  const sel = $('nbanner');
  const keep = sel.value;
  sel.innerHTML = '<option value="">（无）</option>' +
    bannerList.map(b => `<option value="${b}">${b}</option>`).join('');
  if (keep) sel.value = keep;
  previewBanner();
}
async function saveNotification(){
  const id = $('nid').value.trim();
  const p = new URLSearchParams({
    title: $('ntitle').value.trim(),
    body: $('nbody').value.trim(),
    banner: $('nbanner').value,
    tab: $('ntab').value,
    order: $('norder').value || '0',
    isConfirmation: $('nconfirm').checked ? '1' : '0'
  });
  if (id) p.set('id', id);
  const r = await fetch('/gm/notification-save?' + p.toString(), {method:'POST'});
  if (!r.ok) { alert('保存失败 ' + r.status); return; }
  alert('已保存');
  loadNotifications();
}
async function deleteNotification(id){
  if (!confirm(`删除公告 ${id}？`)) return;
  await fetch('/gm/notification-delete?id=' + id, {method:'POST'});
  loadNotifications();
}
async function loadInboxHistory(){
  const u = '/gm/inbox-history?limit=200' + (pid() ? '&publicId=' + encodeURIComponent(pid()) : '');
  const rows = await (await fetch(u)).json();
  document.querySelector('#inboxhist tbody').innerHTML = rows.map(x =>
    `<tr><td>${x.createdAt}</td><td>${x.name}</td><td>${x.quantity}</td>
      <td>${x.received ? '<span class="ok">已领取</span>' : '<span class="err">未领取</span>'}</td>
      <td class="hint">${x.message || ''}</td></tr>`
  ).join('') || '<tr><td colspan="5" class="hint">没有记录</td></tr>';
}
async function loadAccessories(){
  const q = $('aq2').value.trim();
  const r = await fetch('/gm/masters/accessories?q=' + encodeURIComponent(q));
  accRows = await r.json();
  $('acount2').textContent = accRows.length;
  document.querySelector('#accs tbody').innerHTML = accRows.map((a, idx) =>
    `<tr><td><input type="checkbox" ${accSelected.has(a.id) ? 'checked' : ''}
        onchange="toggleAcc(${a.id}, this.checked)"></td>
      <td><code>${a.id}</code></td><td>${a.name}</td>
      <td>${a.limited ? '<span class="err">限时</span>' : ''}</td>
      <td><button onclick="sendAcc(${idx})">发放</button></td></tr>`
  ).join('') || '<tr><td colspan="5" class="hint">没有匹配的饰品</td></tr>';
}
function toggleAcc(id, on){ if(on) accSelected.add(id); else accSelected.delete(id); }
function selectAllAccessories(){ accRows.forEach(a => accSelected.add(a.id)); loadAccessories(); }
function clearAccessories(){ accSelected.clear(); loadAccessories(); }
function sendAcc(idx){
  if(!needPid()) return;
  const a = accRows[idx];
  post(`/gm/inbox?publicId=${q('pid')}&thingType=1&masterId=${a.id}&quantity=${$('aqty').value.trim()}&message=GM 饰品`);
}
async function sendSelectedAccessories(){
  if(!needPid()) return;
  if(accSelected.size === 0){ alert('先勾选饰品'); return; }
  const qty = Number($('aqty').value.trim()) || 1;
  const items = [...accSelected].map(id => ({thingType:1, masterId:id, quantity:qty, message:'GM 饰品'}));
  if(!confirm(`给 ${pid()} 发 ${items.length} 个饰品 × ${qty}？`)) return;
  const r = await fetch('/gm/inbox-batch?publicId=' + encodeURIComponent(pid()),
    {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(items)});
  let msg = r.ok ? '已发送' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg); loadStatus();
}
let itemRows = [];
async function loadItems(){
  const q = $('iq').value.trim();
  const cat = $('icat').value;
  const r = await fetch('/gm/masters/items?limit=4000&q=' + encodeURIComponent(q) + (cat ? '&category=' + cat : ''));
  itemRows = await r.json();
  $('icount').textContent = itemRows.length;
  const catName = {material:'养成材料', accessory:'饰品', event:'活动道具', ticket:'券', point:'点数', piece:'碎片', stamina:'体力', limited:'限时', other:'其他'};
  const catIcon = {material:'材', accessory:'饰', event:'活', ticket:'券', point:'点', piece:'片', stamina:'体', limited:'限', other:'道'};
  const iconCell = (it) =>
    `<img class="itico" src="/gm/img/item/${it.id}.png" alt="" loading="lazy"
        onerror="this.outerHTML='&lt;span class=\\'itico\\' data-cat=\\'${it.category}\\'&gt;${catIcon[it.category] || '道'}&lt;/span&gt;'">`;
  // 按分类分组显示，避免养成材料和普通道具混成一片格子
  const groups = {};
  itemRows.forEach((it, idx) => { (groups[it.category] = groups[it.category] || []).push({ it, idx }); });
  const order = ['material','piece','other','ticket','point','stamina','limited','accessory','event'];
  const html = order.filter(c => groups[c]).map(c => {
    const head = `<tr class="grp"><td colspan="6">${catName[c] || c} · ${groups[c].length} 个</td></tr>`;
    const body = groups[c].map(({ it, idx }) =>
      `<tr><td>${iconCell(it)}</td>
        <td><code>${it.id}</code></td><td>${catName[it.category] || ''}</td><td>${it.name}</td>
        <td>${it.maxQuantity || ''}</td>
        <td><button onclick="sendItem(${idx})">发放</button></td></tr>`).join('');
    return head + body;
  }).join('');
  document.querySelector('#items tbody').innerHTML = html || '<tr><td colspan="6" class="hint">没有匹配的道具</td></tr>';
}
function sendItem(idx){
  if(!needPid()) return;
  const it = itemRows[idx];
  const n = prompt(`给 ${pid()} 发「${it.name}」多少个？`, $('iqty2').value.trim() || '99');
  if(n === null) return;
  const qty = Math.floor(Number(n));
  if(!Number.isFinite(qty) || qty <= 0){ alert('数量要填正整数'); return; }
  post(`/gm/inbox?publicId=${q('pid')}&thingType=1&masterId=${it.id}&quantity=${qty}&message=GM 发放`);
}
async function sendInboxBatch(){
  if(!needPid()) return;
  let items;
  try { items = JSON.parse($('batch').value); } catch(e){ alert('JSON 语法错误: ' + e.message); return; }
  const r = await fetch('/gm/inbox-batch?publicId=' + encodeURIComponent(pid()),
    {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(items)});
  let msg = r.ok ? '已发送' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg); loadStatus();
}

/* 运营 */
let opsData = null;
async function loadOverlay(){
  try {
    const j = await (await fetch('/gm/event-overlay')).json();
    $('ovState').textContent = j.enabled ? `开启（+${j.days} 天）` : '关闭';
    $('ovDays').value = j.days;
  } catch(e){ $('ovState').textContent = '读取失败'; }
}
async function saveOverlay(enabled){
  const days = Number($('ovDays').value.trim()) || 30;
  const r = await fetch(`/gm/event-overlay?enabled=${enabled}&days=${days}`, {method:'POST'});
  let msg = r.ok ? '已保存' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg);
  loadOverlay();
}
let gachaNames = {};
async function ensureGachaNames(){
  if (Object.keys(gachaNames).length) return;
  try { gachaNames = await (await fetch('/gm/masters/gachas')).json(); } catch(e){ gachaNames = {}; }
}
async function loadEvents(){
  const q = $('eq').value.trim();
  const active = $('eactive').checked ? 1 : 0;
  const r = await fetch(`/gm/masters/events?active=${active}&q=${encodeURIComponent(q)}`);
  const rows = await r.json();
  $('ecount').textContent = rows.length;
  const stateText = {open:'<span class="ok">开放中</span>', upcoming:'<span class="hint">未开始</span>', ended:'<span class="err">已结束</span>', unknown:'?'};
  document.querySelector('#etable tbody').innerHTML = rows.map(e =>
    `<tr><td><code>${e.id}</code></td><td>${e.type}</td><td>${e.name || '—'}</td>
      <td>${e.startsAt || '—'}</td><td>${e.endsAt || '—'}</td>
      <td>${stateText[e.state] || e.state}${e.disabled ? ' <span class="err">[已关闭]</span>' : ''}</td>
      <td><button onclick="toggleEvent(${e.id}, ${e.disabled ? 1 : 0})">${e.disabled ? '开启' : '关闭'}</button></td></tr>`
  ).join('') || '<tr><td colspan="7" class="hint">没有匹配的活动</td></tr>';
}
async function toggleEvent(id, enabled){
  const r = await fetch(`/gm/event-toggle?id=${id}&enabled=${enabled}`, {method:'POST'});
  if (!r.ok) { alert('操作失败 ' + r.status); return; }
  loadEvents();
}
let gachaFilter = '';
async function loadOpsList(){
  await ensureGachaNames();
  const r = await fetch('/gm/ops/list');
  if(!r.ok){ alert('读取失败 ' + r.status); return; }
  opsData = await r.json();

  const gOn = opsData.gachas.filter(g => g.enabled).length;
  const lOn = opsData.loginBonuses.filter(l => l.enabled).length;
  $('sumGacha').textContent = `${gOn} / ${opsData.gachas.length} 开启`;
  $('sumLogin').textContent = `${lOn} / ${opsData.loginBonuses.length} 开启`;
  $('sumPass').textContent = opsData.missionPass.enabled ? '开启' : '关闭';
  $('dCountGacha').textContent = opsData.gachas.length;
  $('dCountLogin').textContent = opsData.loginBonuses.length;

  renderGachaTable();
  renderLoginTable();
}
function renderGachaTable(){
  const list = opsData.gachas.filter(g => !gachaFilter || g.id.includes(gachaFilter));
  document.querySelector('#gtable tbody').innerHTML = list.map(g =>
    `<tr><td><code>${g.id}</code></td><td>${gachaNames[g.id] || '—'}</td>
      <td>${g.enabled ? '<span class="ok">开启</span>' : '<span class="err">关闭</span>'}</td>
      <td><button onclick="toggleGacha('${g.id}', ${g.enabled ? 0 : 1})">${g.enabled ? '关闭' : '开启'}</button></td></tr>`
  ).join('') || '<tr><td colspan="4" class="hint">没有匹配</td></tr>';
}
function filterGacha(){ gachaFilter = $('gq').value.trim(); renderGachaTable(); }
function fmtPeriod(startsAt, endsAt, days){
  const s = startsAt ? startsAt.replace('T', ' ').slice(0, 16) : '—';
  if (endsAt) return `${s} → ${endsAt.replace('T',' ').slice(0,16)}`;
  return `${s} 起`;
}
function renderLoginTable(){
  document.querySelector('#lgtable tbody').innerHTML = opsData.loginBonuses.map(lb => {
    const d = lb.startsAt || '';
    return `<tr>
      <td><b>${lb.title || lb.id}</b><br><span class="hint">${lb.reward || ''}</span></td>
      <td><input id="lb-${lb.id}-starts" value="${d}" size="20" title="开始时间 ISO"></td>
      <td><input id="lb-${lb.id}-ends" value="${lb.endsAt || ''}" size="20" title="留空=不限"></td>
      <td><input id="lb-${lb.id}-days" value="${lb.days}" size="4"></td>
      <td><input id="lb-${lb.id}-type" value="${lb.thingType}" size="4"></td>
      <td><input id="lb-${lb.id}-master" value="${lb.masterId}" size="8"></td>
      <td><input id="lb-${lb.id}-qty" value="${lb.quantity}" size="6"></td>
      <td><input type="checkbox" id="lb-${lb.id}-loop" ${lb.isLoop ? 'checked' : ''}></td>
      <td><input type="checkbox" id="lb-${lb.id}-en" ${lb.enabled ? 'checked' : ''}></td>
      <td><button onclick="saveLoginBonus('${lb.id}')">保存</button></td>
    </tr>`;
  }).join('') || '<tr><td colspan="10" class="hint">没有登录奖励</td></tr>';
}
async function saveLoginBonus(id){
  const p = new URLSearchParams({
    id,
    startsAt: $(`lb-${id}-starts`).value.trim(),
    endsAt: $(`lb-${id}-ends`).value.trim(),
    days: $(`lb-${id}-days`).value.trim(),
    thingType: $(`lb-${id}-type`).value.trim(),
    masterId: $(`lb-${id}-master`).value.trim(),
    quantity: $(`lb-${id}-qty`).value.trim(),
    isLoop: $(`lb-${id}-loop`).checked ? 1 : 0,
    enabled: $(`lb-${id}-en`).checked ? 1 : 0
  });
  const r = await fetch('/gm/ops/loginbonus?' + p, {method:'POST'});
  let msg = r.ok ? `登录奖励 ${id} 已保存` : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg);
  loadOpsList(); loadStatus();
}
function renderLoginTableOld(){
  document.querySelector('#lgtable tbody').innerHTML = opsData.loginBonuses.map(lb =>
    `<tr><td><code>${lb.id}</code></td><td>${fmtPeriod(lb.startsAt, lb.endsAt, lb.days)}</td>
      <td>${lb.days} 天</td>
      <td>${lb.enabled ? '<span class="ok">开启</span>' : '<span class="err">关闭</span>'}</td>
      <td><button onclick="toggleLoginBonus('${lb.id}', ${lb.enabled ? 0 : 1})">${lb.enabled ? '关闭' : '开启'}</button></td></tr>`
  ).join('');
}
async function toggleLoginBonus(id, enabled){
  const r = await fetch(`/gm/ops/loginbonus?id=${id}&enabled=${enabled}`, {method:'POST'});
  if(!r.ok) alert('失败 ' + r.status);
  loadOpsList(); loadStatus();
}
async function allLoginBonus(enabled){
  if(!opsData) return;
  if(!confirm(`把 ${opsData.loginBonuses.length} 个登录奖励全部${enabled ? '开启' : '关闭'}？`)) return;
  for (const lb of opsData.loginBonuses){
    if ((lb.enabled ? 1 : 0) === enabled) continue;
    await fetch(`/gm/ops/loginbonus?id=${lb.id}&enabled=${enabled}`, {method:'POST'});
  }
  loadOpsList(); loadStatus();
}
async function toggleGacha(id, enabled){
  const r = await fetch(`/gm/ops/gacha?id=${id}&enabled=${enabled}`, {method:'POST'});
  if(!r.ok) alert('失败 ' + r.status);
  loadOpsList(); loadStatus();
}
async function toggleMissionPass(){
  const target = opsData?.missionPass?.enabled ? 0 : 1;
  const r = await fetch(`/gm/ops/missionpass?enabled=${target}`, {method:'POST'});
  if(!r.ok) alert('失败 ' + r.status);
  loadOpsList(); loadStatus();
}
function gachas(all){ if(confirm(all ? '开启全部卡池？' : '关闭全部卡池？')) post('/gm/operations/gachas?all=' + all, true).then(()=>loadOps()); }
function resetLoginBonus(){ if(needPid() && confirm('重置登录奖励领取记录？')) post(`/gm/reset-loginbonus?publicId=${q('pid')}`); }
function resetDaily(){ if(needPid()) post(`/gm/reset-daily?publicId=${q('pid')}`); }
function resetMissions(){ if(needPid() && confirm('重置全部任务状态？')) post(`/gm/reset-missions?publicId=${q('pid')}`); }
function resetShop(){ if(needPid()) post(`/gm/reset-shop?publicId=${q('pid')}`); }
async function loadOps(){
  const r = await fetch('/gm/operations');
  if(!r.ok){ $('ops').value = '读取失败 ' + r.status; return; }
  const d = await r.json();
  $('ops').value = d.payload;
  $('opsrev').textContent = 'revision ' + d.revision;
}
async function saveOps(){
  const text = $('ops').value;
  try { JSON.parse(text); } catch(e){ alert('JSON 语法错误: ' + e.message); return; }
  if(!confirm('保存活动配置？所有活动会立即按新配置生效')) return;
  const r = await fetch('/gm/operations', {method:'POST', headers:{'Content-Type':'application/json'}, body:text});
  let msg = r.ok ? '已保存' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg); loadOps();
}

/* 工具 */
async function calcRate(){
  const r = await fetch(`/gm/rate?level=${$('rlevel').value.trim()}&ach=${$('rach').value.trim()}`);
  const j = await r.json();
  $('rout').textContent = r.ok ? `理论值 ${j.notationRate} · 加成 ${j.bonus} · 评级 ${j.grade}` : ('失败 ' + (j.error || r.status));
}
async function simulatePlay(){
  if(!needPid()) return;
  const p = new URLSearchParams({publicId: pid(), liveMasterId: $('plive').value.trim(),
    difficulty: $('pdiff').value.trim(), ach: $('pach').value.trim(), level: $('plv').value.trim()});
  const r = await fetch('/gm/play-result?' + p, {method:'POST'});
  let msg = r.ok ? '已写入' : ('失败 ' + r.status);
  try { msg += '\n' + JSON.stringify(await r.json()); } catch(e){}
  alert(msg); showRating(); loadStatus();
}
async function showRating(){
  if(!pid()){ $('ratingOut').textContent = '先填 publicId'; return; }
  const r = await fetch('/gm/rating?publicId=' + encodeURIComponent(pid()));
  if(!r.ok){ $('ratingOut').textContent = '查询失败 ' + r.status; return; }
  const j = await r.json();
  $('ratingOut').textContent = `已录入 ${j.count} 首 · 理论值合计 ${j.totalRate}`;
}

/* 危险 */
function dangerTarget(){
  const v = $('dpid').value.trim();
  if(!v){ alert('先在「危险」页填目标 publicId'); return null; }
  return v;
}
function resetAccount(){
  const t = dangerTarget();
  if(!t) return;
  const typed = prompt(`即将重置 ${t} 的全部进度（账号保留）。\n确认请输入目标 publicId：`);
  if(typed === null) return;
  if(typed.trim() !== t){ alert('输入不匹配，已取消'); return; }
  post(`/gm/reset?publicId=${encodeURIComponent(t)}&confirm=yes`);
}
function deleteAccount(){
  const t = dangerTarget();
  if(!t) return;
  const typed = prompt(`即将【删除】${t}，不可恢复！\n确认请输入 DELETE ${t}`);
  if(typed === null) return;
  if(typed.trim() !== 'DELETE ' + t){ alert('输入不匹配，已取消'); return; }
  post(`/gm/delete?publicId=${encodeURIComponent(t)}&confirm=yes`);
}

load(); loadStatus();

async function loadEvents(){
  const sel = document.getElementById('eventPicker');
  if(!sel) return;
  const r = await fetch('/gm/events/list', {headers:{}});
  const j = await r.json();
  const fmt = ts => ts ? new Date(ts*1000).toISOString().slice(0,10) : '—';
  const all = []
    .concat((j.events||[]).map(e => ({...e, label:'活动 #'+e.id})))
    .concat((j.stories||[]).map(e => ({...e, label:'剧情 #'+e.id+' '+(e.name||'')})))
    .concat((j.specials||[]).map(e => ({...e, label:'特别 #'+e.id})));
  sel.innerHTML = all.sort((a,b)=>b.id-a.id).map(e =>
    `<option value="${e.id}">${e.label}　${fmt(e.start)} → ${fmt(e.end)}</option>`).join('');
}
async function loadDashboard(){
  try{
    const r = await fetch('/gm/dashboard');
    const j = await r.json();
    const set = (id,v) => { const e=document.getElementById(id); if(e) e.textContent = v; };
    set('hm-addr', j.server.baseUrl);
    set('hm-start', j.server.startedAt);
    set('hm-ports', (j.server.ports||[]).join(' / '));
    set('hm-ver', j.master.version || '—');
    set('hm-size', j.master.bytes ? (j.master.bytes/1048576).toFixed(2)+' MB' : '—');
    set('hm-file', j.master.file || '');
    set('hm-acc', j.data.accounts);
    set('hm-cards', j.data.cards);
    set('hm-posters', j.data.posters);
    set('hm-acc2', j.data.accessories);
    set('hm-pkg', j.data.packages);
    set('hm-tro', j.data.trophies);
    set('hm-evt', j.data.events);
  }catch(e){ console.error(e); }
}
function switchTab(name){
  document.querySelectorAll('nav button').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab').forEach(s => s.classList.toggle('active', s.id === 'tab-' + name));
  if(name === 'home') loadDashboard();
  if(name === 'ops' && typeof loadOps === 'function') loadOps();
}
document.addEventListener('DOMContentLoaded', loadDashboard);
async function closeOneEvent(){
  const sel = document.getElementById('eventPicker');
  if(!sel || !sel.value) return alert('先选一个活动');
  if(!confirm('直接关闭活动 #' + sel.value + '？\n（关闭后立即消失，无结算期）')) return;
  const r = await fetch('/gm/events/close?eventId=' + sel.value, {method:'POST'});
  const j = await r.json();
  alert(j.ok ? ('已关闭，版本 ' + j.version + '\n请重启客户端（服务端 5 秒后自动重启）') : ('失败: ' + (j.error||'')));
}
async function endOneEvent(){
  const sel = document.getElementById('eventPicker');
  if(!sel || !sel.value) return alert('先选一个活动');
  const days = parseInt(document.getElementById('settleDays').value || '2', 10);
  if(!confirm('结束活动 #' + sel.value + '，进入 ' + days + ' 天结算期？')) return;
  const r = await fetch('/gm/events/end?eventId=' + sel.value + '&settleDays=' + days, {method:'POST'});
  const j = await r.json();
  alert(j.ok ? ('已进入结算期（' + j.settleDays + ' 天），版本 ' + j.version + '\n请重启客户端（服务端 5 秒后自动重启）') : ('失败: ' + (j.error||'')));
}
async function openOneEvent(){
  const sel = document.getElementById('eventPicker');
  if(!sel || !sel.value) return alert('先选一个活动');
  if(!confirm('开启活动 #' + sel.value + '（30 天）？\n会重新生成主数据并重启服务。')) return;
  const dEl = document.getElementById('eventDays');
  const days = parseInt(dEl && dEl.value || '14', 10);
  const r = await fetch('/gm/events/open?eventId=' + sel.value + '&days=' + days, {method:'POST'});
  const j = await r.json();
  alert(j.ok ? ('已开启，版本 ' + j.version + '\n请重启客户端等待主数据下载（服务端 5 秒后自动重启）') : ('失败: ' + (j.error||'')));
}
document.addEventListener('DOMContentLoaded', loadEvents);


(async function decorate(){
  try{
    const r = await fetch('/gm/art-list');
    const list = await r.json();
    if(!Array.isArray(list) || !list.length) return;
    const wall = document.getElementById('artwall');
    const hero = document.getElementById('heroImg');
    // 顶部大图：优先卡面
    const cards = list.filter(n => n.startsWith('cards_'));
    const logos = list.filter(n => n.startsWith('logos_'));
    const posters = list.filter(n => n.startsWith('posters_'));
    const banners = list.filter(n => n.startsWith('banners_'));
    if(cards.length && hero) hero.src = '/gm/art/' + cards[Math.floor(Math.random()*cards.length)];
    // 右栏：logo/海报/横幅
    if(wall){
      const pick = [...logos, ...banners, ...posters, ...cards].slice(0, 8);
      wall.innerHTML = pick.map(n => `<img src="/gm/art/${n}">`).join('');
    }
  }catch(e){ console.error(e); }
})();

(function eventBackground(){
  const list = ['ev_20001.jpg','ev_20002.jpg','ev_20003.jpg','ev_20004.jpg','ev_20005.jpg',
                'ev_20006.jpg','ev_20007.jpg','ev_20008.jpg','ev_20009.jpg','ev_20010.jpg'];
  const pick = list[Math.floor(Math.random()*list.length)];
  document.body.style.setProperty('--ev-bg', `url('/gm/art/${pick}')`);
})();
</script>
</body>
</html>

""";
}

/// <summary>GM 后台的 HTTP Basic 认证。用户名/密码来自环境变量 GM_USER / GM_PASSWORD。</summary>
static class GmAuth
{
    static string User => System.Environment.GetEnvironmentVariable("GM_USER") ?? "admin";
    static string Password => System.Environment.GetEnvironmentVariable("GM_PASSWORD") ?? "";

    /// <summary>应用内登录用的 Cookie 名。</summary>
    public const string CookieName = "wds_gm";

    /// <summary>校验用户名/密码（登录表单用）。</summary>
    public static bool Verify(string user, string pass)
    {
        var configured = Password;
        if (string.IsNullOrEmpty(configured)) return true;
        return string.Equals(user, User, StringComparison.Ordinal)
            && string.Equals(pass, configured, StringComparison.Ordinal);
    }

    /// <summary>由账号密码派生的会话令牌（改了密码令牌自然失效）。</summary>
    public static string IssueToken()
    {
        var raw = $"{User}:{Password}:wds-gm-cookie-v1";
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static bool Check(HttpContext context)
    {
        var configured = Password;
        if (string.IsNullOrEmpty(configured)) return true;   // 没设密码则不校验（仅本地开发）

        // ① 应用内登录的 Cookie
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie)
            && !string.IsNullOrEmpty(cookie)
            && string.Equals(cookie, IssueToken(), StringComparison.Ordinal))
        {
            return true;
        }

        // ② 兼容旧的 Basic 认证（脚本/curl 仍可用）
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()));
            var idx = raw.IndexOf(':');
            if (idx < 0) return false;
            var user = raw.Substring(0, idx);
            var pass = raw.Substring(idx + 1);
            return string.Equals(user, User, StringComparison.Ordinal) && string.Equals(pass, configured, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>GM 的应用内登录页（不再使用浏览器原生 Basic 登录框）。</summary>
static class GmLoginPage
{
    public static readonly string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>WDS GM 登录</title>
<style>
  *{box-sizing:border-box}
  body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
       font-family:-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
       background:linear-gradient(135deg,#1a1030 0%,#2a1548 50%,#3d1a4a 100%);color:#eee}
  .card{width:340px;padding:32px 28px;border-radius:16px;
        background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.14);
        box-shadow:0 18px 50px rgba(0,0,0,.45);backdrop-filter:blur(10px)}
  h1{margin:0 0 6px;font-size:20px;font-weight:600;letter-spacing:.5px}
  .sub{margin:0 0 22px;font-size:12px;opacity:.55}
  label{display:block;font-size:12px;opacity:.75;margin:0 0 6px}
  input{width:100%;padding:10px 12px;margin-bottom:16px;border-radius:9px;
        border:1px solid rgba(255,255,255,.18);background:rgba(0,0,0,.28);
        color:#fff;font-size:14px;outline:none}
  input:focus{border-color:#e05a8a;box-shadow:0 0 0 3px rgba(224,90,138,.18)}
  button{width:100%;padding:11px;border:0;border-radius:9px;cursor:pointer;
         font-size:14px;font-weight:600;color:#fff;
         background:linear-gradient(135deg,#e05a8a,#b8427a)}
  button:hover{filter:brightness(1.08)}
  .err{margin:0 0 16px;padding:9px 12px;border-radius:9px;font-size:12.5px;
       background:rgba(255,80,80,.16);border:1px solid rgba(255,80,80,.35);color:#ffb3b3}
  .foot{margin-top:18px;font-size:11px;opacity:.4;text-align:center}
</style>
</head>
<body>
<form class="card" method="post" action="/gm/login">
  <h1>WDS GM 控制台</h1>
  <p class="sub">私有服务器管理后台</p>
  <div class="err" id="err" style="display:none">用户名或密码错误</div>
  <label>用户名</label>
  <input name="user" autocomplete="username" autofocus required>
  <label>密码</label>
  <input name="pass" type="password" autocomplete="current-password" required>
  <button type="submit">登录</button>
  <div class="foot">WDS Local Server</div>
</form>
<script>
  if (new URLSearchParams(location.search).has('e'))
    document.getElementById('err').style.display = 'block';
</script>
</body>
</html>
""";
}

