using System.Collections.Concurrent;
using System.IO;
using BlackStarSwissAudio.Services;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BlackStarSwissAudio.Core;

/// <summary>Sample provider over an immutable buffer; position is thread-safe for audio-thread reads.</summary>
public sealed class BufferProvider(AudioBuffer b) : ISampleProvider
{
    sealed record LoopRange(long Start, long End);

    const int RecentFrameCapacity = SpectrogramAnalysis.FftSize;
    readonly AudioBuffer _b = b;
    readonly float[] _recentSamples = new float[RecentFrameCapacity * b.Channels];
    readonly Lock _recentLock = new();
    long _pos;
    int _recentWriteFrame, _recentFrameCount;
    LoopRange? _loop;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(b.Rate, b.Channels);
    public double DataDurationSeconds => (double)_b.Frames / _b.Rate;
    public long Position
    {
        get => Interlocked.Read(ref _pos);
        set => Interlocked.Exchange(ref _pos, Math.Clamp(value - value % _b.Channels, 0, _b.Data.Length));
    }
    public void SetLoop(double startSeconds, double endSeconds)
    {
        if (!double.IsFinite(startSeconds)) throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (!double.IsFinite(endSeconds)) throw new ArgumentOutOfRangeException(nameof(endSeconds));
        long start = (long)(Math.Clamp(startSeconds, 0, DataDurationSeconds) * _b.Rate * _b.Channels);
        long end = (long)(Math.Clamp(endSeconds, 0, DataDurationSeconds) * _b.Rate * _b.Channels);
        start -= start % _b.Channels; end -= end % _b.Channels;
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(endSeconds), "Loop end must be after its start.");
        Volatile.Write(ref _loop, new LoopRange(start, end));
    }
    public void ClearLoop() => Volatile.Write(ref _loop, null);

    public void ResetRecentAudio()
    {
        lock (_recentLock)
        {
            Array.Clear(_recentSamples);
            _recentWriteFrame = 0;
            _recentFrameCount = 0;
        }
    }

    public AudioBuffer? GetRecentAudio()
    {
        lock (_recentLock)
        {
            if (_recentFrameCount == 0) return null;
            int channels = _b.Channels;
            int startFrame = (_recentWriteFrame - _recentFrameCount + RecentFrameCapacity) % RecentFrameCapacity;
            var samples = new float[_recentFrameCount * channels];
            int firstFrames = Math.Min(_recentFrameCount, RecentFrameCapacity - startFrame);
            Array.Copy(_recentSamples, startFrame * channels, samples, 0, firstFrames * channels);
            int remainingFrames = _recentFrameCount - firstFrames;
            if (remainingFrames > 0)
                Array.Copy(_recentSamples, 0, samples, firstFrames * channels, remainingFrames * channels);
            return new AudioBuffer(samples, _b.Rate, channels);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var loop = Volatile.Read(ref _loop);
        long position = Interlocked.Read(ref _pos);
        if (loop is not null && position >= loop.End) position = loop.Start;
        long end = loop?.End ?? _b.Data.Length;
        int samples = (int)Math.Min(count, end - position);
        if (samples <= 0) return 0;
        samples -= samples % _b.Channels;
        _b.Data.AsSpan(checked((int)position), samples).CopyTo(buffer.AsSpan(offset, samples));
        CaptureRecentAudio(buffer, offset, samples);
        Interlocked.Exchange(ref _pos, position + samples);
        return samples;
    }

    void CaptureRecentAudio(float[] buffer, int offset, int samples)
    {
        int channels = _b.Channels;
        int frames = samples / channels;
        if (frames == 0) return;

        lock (_recentLock)
        {
            if (frames >= RecentFrameCapacity)
            {
                int sourceOffset = offset + (frames - RecentFrameCapacity) * channels;
                Array.Copy(buffer, sourceOffset, _recentSamples, 0, _recentSamples.Length);
                _recentWriteFrame = 0;
                _recentFrameCount = RecentFrameCapacity;
                return;
            }

            int firstFrames = Math.Min(frames, RecentFrameCapacity - _recentWriteFrame);
            Array.Copy(buffer, offset, _recentSamples, _recentWriteFrame * channels, firstFrames * channels);
            int remainingFrames = frames - firstFrames;
            if (remainingFrames > 0)
                Array.Copy(buffer, offset + firstFrames * channels, _recentSamples, 0, remainingFrames * channels);
            _recentWriteFrame = (_recentWriteFrame + frames) % RecentFrameCapacity;
            _recentFrameCount = Math.Min(RecentFrameCapacity, _recentFrameCount + frames);
        }
    }
}

public static class AudioIO
{
    static readonly HashSet<string> Native = new([".wav", ".mp3", ".aif", ".aiff", ".m4a", ".aac", ".wma", ".mp4", ".m4v"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Video = new([".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp"], StringComparer.OrdinalIgnoreCase);
    static readonly Lazy<bool> MediaFoundation = new(() =>
    {
        MediaFoundationApi.Startup();
        return true;
    });
    sealed record CachedAudio(long Length, long LastWriteTicks, WeakReference<AudioBuffer> Buffer);
    static readonly ConcurrentDictionary<string, CachedAudio> AudioCache = new(StringComparer.OrdinalIgnoreCase);
    static int _cacheMaintenanceCount;

    public const string OpenFilter =
        "All media|*.wav;*.mp3;*.aif;*.aiff;*.m4a;*.aac;*.wma;*.flac;*.ogg;*.opus;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp|All files|*.*";
    public const string SaveFilter =
        "WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|FLAC (*.flac)|*.flac|OGG Vorbis (*.ogg)|*.ogg|Opus (*.opus)|*.opus|AAC (*.m4a)|*.m4a";

    public static bool IsVideo(string path) => Video.Contains(Path.GetExtension(path));

    public static async Task<AudioBuffer> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Audio file was not found.", fullPath);
        long length = info.Length, lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        if (AudioCache.TryGetValue(fullPath, out var cached) && cached.Length == length && cached.LastWriteTicks == lastWriteTicks && cached.Buffer.TryGetTarget(out var hit))
            return hit;

        string extension = Path.GetExtension(fullPath);
        bool native = Native.Contains(extension);
        Log.Info("Load", $"Decoding {Path.GetFileName(fullPath)} ({extension}) via {(native ? "native" : "FFmpeg")}");
        AudioBuffer result;
        if (native)
            result = await Task.Run(() => LoadNative(fullPath, ct), ct);
        else
        {
            var tmp = Path.Combine(AppPaths.Temp, $"x-{Guid.NewGuid():N}.wav");
            try
            {
                await Ffmpeg.ExtractAudioAsync(fullPath, tmp, ct);
                result = await Task.Run(() => LoadNative(tmp, ct), ct);
            }
            finally { AppPaths.TryDelete(tmp); }
        }

        AudioCache[fullPath] = new CachedAudio(length, lastWriteTicks, new WeakReference<AudioBuffer>(result));
        if (Interlocked.Increment(ref _cacheMaintenanceCount) % 64 == 0) PruneAudioCache();
        return result;
    }

    static void PruneAudioCache()
    {
        foreach (var entry in AudioCache)
        {
            if (AudioCache.Count <= 256 && entry.Value.Buffer.TryGetTarget(out _)) continue;
            AudioCache.TryRemove(entry.Key, out _);
        }
    }

    static AudioBuffer LoadNative(string path, CancellationToken ct)
    {
        using var r = new AudioFileReader(path);
        int ch = r.WaveFormat.Channels, rate = r.WaveFormat.SampleRate;
        var data = new float[Math.Max(1 << 16, r.Length / sizeof(float))];
        int total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (total == data.Length) Array.Resize(ref data, data.Length + Math.Max(1 << 20, data.Length / 4));
            int n = r.Read(data, total, Math.Min(data.Length - total, 1 << 16));
            if (n <= 0) break;
            total += n;
        }
        Array.Resize(ref data, total - total % ch);
        Log.Info("Load", $"{Path.GetFileName(path)} {rate}Hz {ch}ch {(double)(data.Length / ch) / rate:F1}s");
        return new AudioBuffer(data, rate, ch);
    }

    public static Task SaveFloatWaveAsync(AudioBuffer buffer, string path, CancellationToken ct = default) => Task.Run(() =>
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(buffer.Rate, buffer.Channels));
        const int chunkSize = 1 << 16;
        for (int offset = 0; offset < buffer.Data.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            writer.WriteSamples(buffer.Data, offset, Math.Min(chunkSize, buffer.Data.Length - offset));
        }
    }, ct);

    public static Task SaveAsync(AudioBuffer b, string path, int mp3Bitrate = 192000, CancellationToken ct = default) => Task.Run(async () =>
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav") { WaveFileWriter.CreateWaveFile16(path, new BufferProvider(b)); return; }
        if (ext == ".mp3")
        {
            var c = Mixer.Conform(b, 44100, Math.Min(2, b.Channels));
            _ = MediaFoundation.Value;
            MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(new BufferProvider(c)), path, mp3Bitrate);
            return;
        }
        var tmp = Path.Combine(AppPaths.Temp, $"e-{Guid.NewGuid():N}.wav");
        try
        {
            WaveFileWriter.CreateWaveFile16(tmp, new BufferProvider(b));
            await Ffmpeg.ConvertAsync(tmp, path, ct);
        }
        finally { AppPaths.TryDelete(tmp); }
    }, ct);
}
