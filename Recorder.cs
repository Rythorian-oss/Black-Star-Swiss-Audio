using System.IO;
using System.Runtime.InteropServices;
using BlackStarSwissAudio.Core;
using NAudio.Wave;

namespace BlackStarSwissAudio.Services;

/// <summary>Records 44.1 kHz stereo 16-bit from the default input straight to a temp WAV (no RAM growth).</summary>
public sealed class Recorder : IDisposable
{
    WaveInEvent? _in; WaveFileWriter? _w; string? _path; TaskCompletionSource? _stop; long _bytes; volatile float _level; bool _disposed;
    readonly float[] _recentSamples = new float[SpectrogramAnalysis.FftSize * 2];
    readonly Lock _recentLock = new();
    int _recentWriteFrame, _recentFrameCount;
    public bool IsRecording { get; private set; }
    public float Level => _level;
    public double Seconds => _bytes / (44100.0 * 4);

    public AudioBuffer GetRecentAudio()
    {
        lock (_recentLock)
        {
            var data = new float[_recentFrameCount * 2];
            int capacity = _recentSamples.Length / 2;
            int startFrame = (_recentWriteFrame - _recentFrameCount + capacity) % capacity;
            int firstFrames = Math.Min(_recentFrameCount, capacity - startFrame);
            Array.Copy(_recentSamples, startFrame * 2, data, 0, firstFrames * 2);
            int remainingFrames = _recentFrameCount - firstFrames;
            if (remainingFrames > 0)
                Array.Copy(_recentSamples, 0, data, firstFrames * 2, remainingFrames * 2);
            return new AudioBuffer(data, 44100, 2);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording) return;
        _path = Path.Combine(AppPaths.Temp, $"rec-{Guid.NewGuid():N}.wav");
        var fmt = new WaveFormat(44100, 16, 2);
        try
        {
            _in = new WaveInEvent { WaveFormat = fmt, BufferMilliseconds = 40 };
            _w = new WaveFileWriter(_path, fmt); _bytes = 0;
            lock (_recentLock) { Array.Clear(_recentSamples); _recentWriteFrame = 0; _recentFrameCount = 0; }
            _in.DataAvailable += OnData; _in.RecordingStopped += OnStopped;
            _stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _in.StartRecording(); IsRecording = true;
        }
        catch
        {
            Cleanup();
            if (_path is not null) AppPaths.TryDelete(_path);
            _path = null;
            throw;
        }
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        _w?.Write(e.Buffer, 0, e.BytesRecorded); _bytes += e.BytesRecorded;
        int peak = 0;
        var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
        lock (_recentLock)
        {
            int capacity = _recentSamples.Length / 2;
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                short left = samples[i], right = samples[i + 1];
                peak = Math.Max(peak, Math.Max(Math.Abs((int)left), Math.Abs((int)right)));
                _recentSamples[_recentWriteFrame * 2] = left / 32768f;
                _recentSamples[_recentWriteFrame * 2 + 1] = right / 32768f;
                _recentWriteFrame = (_recentWriteFrame + 1) % capacity;
                _recentFrameCount = Math.Min(_recentFrameCount + 1, capacity);
            }
        }
        _level = peak / 32768f;
    }

    void OnStopped(object? s, StoppedEventArgs e) { if (e.Exception is not null) Log.Error("Record", e.Exception); _stop?.TrySetResult(); }

    public async Task<AudioBuffer?> StopAsync(CancellationToken ct = default)
    {
        if (!IsRecording || _in is null) return null;
        var path = _path;
        try
        {
            _in.StopRecording(); await _stop!.Task.WaitAsync(ct); IsRecording = false; _level = 0;
            Cleanup();
            return path is null ? null : await AudioIO.LoadAsync(path, ct);
        }
        finally
        {
            Cleanup();
            if (path is not null) AppPaths.TryDelete(path);
            _path = null;
        }
    }

    void Cleanup()
    {
        if (_in is not null) { _in.DataAvailable -= OnData; _in.RecordingStopped -= OnStopped; _in.Dispose(); _in = null; }
        _w?.Dispose(); _w = null; _stop = null; IsRecording = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        if (_path is not null) AppPaths.TryDelete(_path);
        _path = null;
    }
}
