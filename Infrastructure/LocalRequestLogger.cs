using System.Text;
using System.Threading.Channels;

/// <summary>
/// 请求日志。原来是「SemaphoreSlim + 同步 File.AppendAllText」：
/// 一旦某个请求卡在写盘上，锁就把所有后续请求（连 /health）一起堵死。
/// 现在改成无阻塞入队 + 单后台线程写盘，队列满就丢最旧的，永不反压到请求线程。
/// </summary>
sealed class LocalRequestLogger
{
    const long MaxBytes = 64L * 1024 * 1024;

    readonly string logPath;
    readonly bool writeToConsole;
    readonly Channel<string> queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(20000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public LocalRequestLogger(string logPath, bool writeToConsole = true)
    {
        this.logPath = logPath;
        this.writeToConsole = writeToConsole;
        var t = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "request-log-writer",
        };
        t.Start();
    }

    public void Log(string message) => Enqueue(message);

    public Task LogAsync(string message)
    {
        Enqueue(message);
        return Task.CompletedTask;
    }

    void Enqueue(string message)
    {
        string line;
        try
        {
            line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        }
        catch
        {
            return;
        }
        if (writeToConsole)
        {
            try { Console.Write(line); } catch { }
        }
        // 队列满时丢最旧的，绝不阻塞调用方
        queue.Writer.TryWrite(line);
    }

    void WriterLoop()
    {
        var reader = queue.Reader;
        var sb = new StringBuilder(64 * 1024);
        while (true)
        {
            try
            {
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return;
            }
            catch
            {
                return;
            }

            sb.Clear();
            while (sb.Length < 64 * 1024 && reader.TryRead(out var line))
            {
                sb.Append(line);
            }
            if (sb.Length == 0) continue;

            try
            {
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(logPath, sb.ToString());
            }
            catch
            {
                // 写日志永远不影响主流程
            }
        }
    }

    void Rotate()
    {
        try
        {
            var bak = logPath + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(logPath, bak);
        }
        catch
        {
        }
    }
}
