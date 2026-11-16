using System.Diagnostics;
using System.IO;

namespace BlackStarSwissAudio.Services;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackStarSwissAudio");
    static readonly string ProcessTemp = Path.Combine(
        Root, "temp", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Cache => Path.Combine(Root, "cache");
    public static string UserFx => Path.Combine(Root, "fx-library");
    public static string Temp => ProcessTemp;

    public static void EnsureAll()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(UserFx);
        Directory.CreateDirectory(Path.Combine(Root, "temp"));
        PruneStaleTemp();
        Directory.CreateDirectory(Temp);
        CleanTemp();
        PruneCache(TimeSpan.FromDays(30), 2L * 1024 * 1024 * 1024);
    }

    static void PruneStaleTemp()
    {
        string tempRoot = Path.Combine(Root, "temp");
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(tempRoot))
            {
                string name = Path.GetFileName(directory);
                int separator = name.IndexOf('-');
                if (separator <= 0 || !int.TryParse(name.AsSpan(0, separator), out int processId)) continue;

                try
                {
                    using var process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (IOException ex) { Log.Warn("Temp", $"Could not remove stale temporary files: {ex.Message}"); }
                    catch (UnauthorizedAccessException ex) { Log.Warn("Temp", $"Could not remove stale temporary files: {ex.Message}"); }
                }
            }
        }
        catch (IOException ex) { Log.Warn("Temp", $"Could not inspect temporary files: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Log.Warn("Temp", $"Could not inspect temporary files: {ex.Message}"); }
    }

    public static void CleanTemp()
    {
        try { foreach (var f in Directory.EnumerateFiles(Temp)) TryDelete(f); } catch { /* best effort */ }
    }

    /// <summary>Evicts cache files older than maxAge, then oldest-first until under maxBytes.</summary>
    public static void PruneCache(TimeSpan maxAge, long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(Cache).EnumerateFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (DateTime.UtcNow - f.LastAccessTimeUtc > maxAge || total > maxBytes)
                { total -= f.Length; TryDelete(f.FullName); }
            }
        }
        catch { /* best effort */ }
    }

    public static long CacheBytes() => Directory.EnumerateFiles(Cache).Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).Sum(path => new FileInfo(path).Length);

    public static int ClearCache()
    {
        int removed = 0;
        foreach (var path in Directory.EnumerateFiles(Cache))
        {
            if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); removed++; }
            catch (IOException ex) { Log.Warn("Cache", $"Could not remove {Path.GetFileName(path)}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Log.Warn("Cache", $"Could not remove {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return removed;
    }

    public static void TryDelete(string path) { try { File.Delete(path); } catch { /* in use */ } }
}
