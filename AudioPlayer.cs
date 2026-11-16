using BlackStarSwissAudio.Core;
using NAudio.Wave;

namespace BlackStarSwissAudio.Services;

public enum PlayState { Stopped, Playing, Paused }

public sealed class AudioPlayer : IDisposable
{
    WaveOutEvent? _out; BufferProvider? _src;
    public PlayState State { get; private set; }
    public event Action? Stopped;

    public double Seconds => _src is null ? 0 : (double)_src.Position / _src.WaveFormat.Channels / _src.WaveFormat.SampleRate;
    public AudioBuffer? GetRecentAudio() => _src?.GetRecentAudio();

    public void Seek(double seconds)
    {
        if (_src is null || !double.IsFinite(seconds)) return;
        double samplePosition = Math.Clamp(seconds, 0, _src.DataDurationSeconds) * _src.WaveFormat.SampleRate * _src.WaveFormat.Channels;
        _src.Position = (long)samplePosition;
    }

    public void SetLoop(double startSeconds, double endSeconds) => _src?.SetLoop(startSeconds, endSeconds);
    public void ClearLoop() => _src?.ClearLoop();

    public void Load(AudioBuffer b)
    {
        Teardown();
        _src = new BufferProvider(b);
        _out = new WaveOutEvent { DesiredLatency = 100 };
        _out.Init(_src.ToWaveProvider());
        _out.PlaybackStopped += OnStopped;
        State = PlayState.Stopped;
    }

    public void Play()
    {
        if (_out is null) return;
        if (State == PlayState.Stopped) _src?.ResetRecentAudio();
        _out.Play();
        State = PlayState.Playing;
    }
    public void Pause() { if (State != PlayState.Playing || _out is null) return; _out.Pause(); State = PlayState.Paused; }
    public void Stop() { if (_out is null || State == PlayState.Stopped) return; _out.Stop(); }

    void OnStopped(object? s, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Error("Playback", e.Exception);
        State = PlayState.Stopped;
        _src?.Position = 0;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Stopped?.Invoke();
        else dispatcher.BeginInvoke(Stopped);
    }

    void Teardown()
    {
        if (_out is not null) { _out.PlaybackStopped -= OnStopped; try { _out.Stop(); } catch { } _out.Dispose(); _out = null; }
        _src = null; State = PlayState.Stopped;
    }

    public void Dispose() => Teardown();
}
