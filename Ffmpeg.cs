using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BlackStarSwissAudio.Services;

/// <summary>
/// Runs FFmpeg without a shell, capturing diagnostics
/// and terminating cancelled jobs.
/// </summary>
public static class Ffmpeg
{
    public static string Exe
    {
        get
        {
            var local = Path.Combine(
                AppContext.BaseDirectory,
                "ffmpeg",
                "ffmpeg.exe");

            return File.Exists(local) ? local : "ffmpeg";
        }
    }

    public static async Task RunAsync(
        IEnumerable<string> args,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process
        {
            StartInfo = psi
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    "FFmpeg did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException(
                "FFmpeg could not be started. Install it on PATH " +
                "or place ffmpeg.exe beside the application " +
                "in an ffmpeg folder.",
                ex);
        }

        // Drain both pipes concurrently to avoid deadlocks.
        Task<string> stderr =
            ReadTailAsync(process.StandardError, 8000);

        Task stdout =
            DrainAsync(process.StandardOutput);

        using var registration =
            ct.Register(() => KillProcess(process));

        try
        {
            await process.WaitForExitAsync(ct)
                .ConfigureAwait(false);

            await Task.WhenAll(stderr, stdout)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var details = await stderr
                    .ConfigureAwait(false);

                Log.Warn("ffmpeg", details);

                throw new InvalidOperationException(
                    $"FFmpeg failed (exit {process.ExitCode}). " +
                    details);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);

            try
            {
                await process.WaitForExitAsync(
                    CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process already exited or is unavailable.
            }

            await Task.WhenAll(stderr, stdout)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, int capacity)
    {
        var buffer = new char[4096];
        var tail = new char[capacity];
        int next = 0, count = 0, read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)
            .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                tail[next] = buffer[i];
                next = (next + 1) % capacity;
                if (count < capacity) count++;
            }
        }

        var result = new char[count];
        int start = (next - count + capacity) % capacity;
        for (int i = 0; i < count; i++)
            result[i] = tail[(start + i) % capacity];
        return new string(result);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, 0, buffer.Length)
            .ConfigureAwait(false) > 0)
        {
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    public static Task ExtractAudioAsync(
        string input,
        string wavOut,
        CancellationToken ct) =>
        RunAsync(
            [
                "-y", "-i", input,
                "-vn", "-acodec", "pcm_f32le",
                wavOut
            ],
            ct);

    public static Task ConvertAsync(
        string wavIn,
        string output,
        CancellationToken ct) =>
        RunAsync(
            ["-y", "-i", wavIn, output],
            ct);

    public static Task MuxAsync(
        string video,
        string audio,
        string output,
        CancellationToken ct) =>
        RunAsync(
            [
                "-y",
                "-i", video,
                "-i", audio,
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c:v", "copy",
                "-c:a", "aac",
                "-b:a", "256k",
                "-shortest",
                output
            ],
            ct);
}
