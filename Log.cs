// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace BlackStarSwissAudio.Services;

/// <summary>Non-blocking rolling file logger: callers enqueue, one background task writes.</summary>
public static class Log
{
    static Channel<string>? _ch;
    static Task? _pump;

    public static void Init()
    {
        Prune();
        _ch = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _pump = Task.Run(PumpAsync);
    }

    static async Task PumpAsync()
    {
        try
        {
            var path = Path.Combine(AppPaths.Logs, $"bssa-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
            await using var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            var r = _ch!.Reader;
            await foreach (var line in r.ReadAllAsync())
            {
                await w.WriteLineAsync(line);
                if (!r.TryPeek(out _)) await w.FlushAsync();
            }
        }
        catch { /* logging must never crash the app */ }
    }

    static void Prune()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Logs, "bssa-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) AppPaths.TryDelete(f);
        }
        catch { }
    }

    static void Write(string level, string ctx, string msg, Exception? ex) =>
        _ch?.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff} [{level}] {ctx}: {msg}{(ex is null ? "" : Environment.NewLine + ex)}");

    public static void Info(string ctx, string msg) => Write("INF", ctx, msg, null);
    public static void Warn(string ctx, string msg) => Write("WRN", ctx, msg, null);
    public static void Error(string ctx, Exception? ex, string msg = "") => Write("ERR", ctx, msg.Length > 0 ? msg : ex?.Message ?? "", ex);

    public static void Shutdown()
    {
        _ch?.Writer.TryComplete();
        try { _pump?.Wait(2000); } catch { }
    }
}
