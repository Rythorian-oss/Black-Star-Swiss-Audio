// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using BlackStarSwissAudio.Core;

namespace BlackStarSwissAudio.ViewModels;

public sealed class ParamVM(ToolParam p) : Obs
{
    double _v = p.Default;
    public string Key => p.Key; public string Label => p.Label; public double Min => p.Min; public double Max => p.Max;
    public bool Snap => p.Step > 0; public double Tick => p.Step > 0 ? p.Step : 1;
    public double Value { get => _v; set => Set(ref _v, Math.Clamp(value, p.Min, p.Max)); }
}

public sealed class TrackVM(string name, AudioBuffer buf, string? videoSource, string? sourcePath = null) : Obs, IDisposable
{
    public const int MaxUndo = 8;
    readonly List<AudioBuffer> _undo = [];
    readonly List<AudioBuffer> _redo = [];
    AudioBuffer _buf = buf; string _name = name; double _off, _gain; double _pan; bool _mute, _solo; float[] _peaks = AudioBuffer.ComputePeaks(buf, 1600);
    double _meterPeak, _meterRms; bool _isClipping;
    public event Action? Edited;

    public string? VideoSource { get; } = videoSource; public string? SourcePath { get; } = sourcePath; public string Name { get => _name; set => Set(ref _name, value); }
    public AudioBuffer Buffer => _buf;
    public IReadOnlyList<float> Peaks => _peaks;
    public double MeterPeak { get => _meterPeak; private set => Set(ref _meterPeak, value); }
    public double MeterRms { get => _meterRms; private set => Set(ref _meterRms, value); }
    public bool IsClipping { get => _isClipping; private set => Set(ref _isClipping, value); }
    public string Info => $"{_buf.Seconds:F2}s · {_buf.Rate / 1000.0:0.#}kHz · {(_buf.Channels == 1 ? "mono" : _buf.Channels == 2 ? "stereo" : _buf.Channels + "ch")}";
    public double Offset { get => _off; set { if (Set(ref _off, Math.Max(0, value))) Edited?.Invoke(); } }
    public double GainDb { get => _gain; set { if (Set(ref _gain, value)) Edited?.Invoke(); } }
    public double Pan { get => _pan; set { if (Set(ref _pan, value)) Edited?.Invoke(); } }
    public bool Mute { get => _mute; set { if (Set(ref _mute, value)) Edited?.Invoke(); } }
    public bool Solo { get => _solo; set { if (Set(ref _solo, value)) Edited?.Invoke(); } }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoDescription => $"Undo history: {_undo.Count}/{MaxUndo}";

    public void Replace(AudioBuffer nb)
    {
        _undo.Add(_buf);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Apply(nb);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(_buf);
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(previous);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(_buf);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(next);
        return true;
    }

    public void UpdateMeter(double projectTime, double windowSeconds, bool audible)
    {
        double localStart = projectTime - Offset;
        if (!audible || localStart < 0 || localStart >= _buf.Seconds)
        {
            MeterPeak = 0;
            MeterRms = 0;
            IsClipping = false;
            return;
        }

        int first = Math.Clamp((int)(localStart * _buf.Rate), 0, _buf.Frames);
        int last = Math.Clamp((int)Math.Ceiling((localStart + windowSeconds) * _buf.Rate), first, _buf.Frames);
        double panAngle = (Pan + 1) * Math.PI / 4;
        float leftGain = (float)(Math.Cos(panAngle) * Math.Sqrt(2));
        float rightGain = (float)(Math.Sin(panAngle) * Math.Sqrt(2));
        float trackGain = (float)Math.Pow(10, GainDb / 20);
        double peak = 0, sumSquares = 0;
        long sampleCount = 0;
        for (int frame = first; frame < last; frame++)
        {
            int index = frame * _buf.Channels;
            float left = _buf.Data[index] * trackGain * leftGain;
            float right = (_buf.Channels == 1 ? _buf.Data[index] : _buf.Data[index + 1]) * trackGain * rightGain;
            double absLeft = Math.Abs(left), absRight = Math.Abs(right);
            peak = Math.Max(peak, Math.Max(absLeft, absRight));
            sumSquares += left * left + right * right;
            sampleCount += 2;
        }
        MeterPeak = peak;
        MeterRms = sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
        IsClipping = peak > 1;
    }

    public void RestoreState(double offset, double gainDb, double pan, bool mute, bool solo)
    {
        _off = Math.Max(0, offset);
        _gain = gainDb;
        _pan = Math.Clamp(pan, -1, 1);
        _mute = mute;
        _solo = solo;
        Raise(nameof(Offset)); Raise(nameof(GainDb)); Raise(nameof(Pan)); Raise(nameof(Mute)); Raise(nameof(Solo));
    }

    void Apply(AudioBuffer b)
    {
        _buf = b; _peaks = AudioBuffer.ComputePeaks(b, 1600);
        Raise(nameof(Buffer)); Raise(nameof(Peaks)); Raise(nameof(Info)); Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); Raise(nameof(UndoDescription)); Edited?.Invoke();
    }

    public void Dispose() { _undo.Clear(); _redo.Clear(); _buf = AudioBuffer.Empty(); _peaks = []; Edited = null; }
}
