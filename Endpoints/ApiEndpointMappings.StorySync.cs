using Microsoft.AspNetCore.Http.HttpResults;

static partial class ApiEndpointMappings
{
    // 平板热更新用的数据根目录（与 D:\wds_story\www 一一对应）
    const string StoryRoot = @"D:\wds_story\www";

    static void MapStorySyncEndpoints(WebApplication app)
    {
        // 清单：平板按 size 比对，只下载变了的文件
        app.MapGet("/story/sync/list", () =>
        {
            var items = new List<object>();
            foreach (var (p, s) in EnumerateStoryFiles())
            {
                items.Add(new { p, s });
            }
            return Results.Json(new { count = items.Count, files = items });
        });

        // 原始文件：/story/raw/tl/1040113.csv 之类
        app.MapGet("/story/raw/{**path}", (string path) =>
        {
            var root = Path.GetFullPath(StoryRoot);
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return Results.NotFound();
            }
            return Results.File(full, GuessMime(full), enableRangeProcessing: true);
        });
    }

    static List<(string, long)> EnumerateStoryFiles()
    {
        var list = new List<(string, long)>();

        void AddDir(string urlPrefix, string fsDir, string? suffix = null)
        {
            if (!Directory.Exists(fsDir)) return;
            foreach (var f in Directory.EnumerateFiles(fsDir, "*", SearchOption.AllDirectories))
            {
                if (suffix != null && !f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = f.Substring(fsDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                list.Add((urlPrefix + rel, new FileInfo(f).Length));
            }
        }

        AddDir("/tl/", Path.Combine(StoryRoot, "tl"), ".csv");
        AddDir("/adv/episode/", Path.Combine(StoryRoot, "adv", "episode"), ".json");
        AddDir("/play/", Path.Combine(StoryRoot, "play"));
        AddDir("/assets/", Path.Combine(StoryRoot, "assets"));
        AddDir("/icons/", Path.Combine(StoryRoot, "icons"));
        AddDir("/static/", Path.Combine(StoryRoot, "static"));

        foreach (var n in new[] { "index.html", "settings.js", "manifest.json", "comic.html", "viewer.html" })
        {
            var f = Path.Combine(StoryRoot, n);
            if (File.Exists(f)) list.Add(("/" + n, new FileInfo(f).Length));
        }

        return list;
    }

    static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".otf" => "font/otf",
        ".ttf" => "font/ttf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
