// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using NAudio.Dsp;

namespace BlackStarSwissAudio.Core;

public sealed record ToolParam(string Key, string Label, double Min, double Max, double Default, double Step = 0);
public delegate AudioBuffer ToolFn(AudioBuffer src, IReadOnlyDictionary<string, double> p, AudioBuffer? other, CancellationToken ct);
public sealed record ToolDef(string Name, string Glyph, string Description, ToolParam[] Params, ToolFn Run, bool NeedsSecondary = false);

/// <summary>The 35 tools. Every tool is a pure function: buffer in, new buffer out.</summary>
public static class ToolRegistry
{
    static ToolParam P(string k, string l, double min, double max, double d, double step = 0) => new(k, l, min, max, d, step);
    static float Db(double d) => (float)Math.Pow(10, d / 20);
    static int Fr(AudioBuffer b, double s) => (int)Math.Clamp(Math.Round(s * b.Rate), 0, b.Frames);
    const int Chk = 0xFFFF;
    static readonly int[] CombDelays = [1116, 1188, 1277, 1356];
    static readonly int[] AllPassDelays = [556, 441];

    public static IReadOnlyList<ToolDef> All { get; } =
    [
        new("Cut", "✂", "Keep or delete a region (percent of clip).", [P("start", "Start (% of clip)", 0, 100, 0, 0.1), P("end", "End (% of clip)", 0, 100, 50, 0.1), P("mode", "Mode (0 keep, 1 delete)", 0, 1, 0, 1)], Cut),
        new("Merge", "⛓", "Join this track with the next track (or previous if last), with crossfade.", [P("xfade", "Crossfade (s)", 0, 10, 0, 0.05)], Merge, true),
        new("Fade In", "◢", "Linear fade from silence.", [P("dur", "Duration (s)", 0.01, 30, 2)], (b, p, _, ct) => Fade(b, p["dur"], true)),
        new("Fade Out", "◣", "Linear fade to silence.", [P("dur", "Duration (s)", 0.01, 30, 2)], (b, p, _, ct) => Fade(b, p["dur"], false)),
        new("Gain", "▲", "Boost or cut level.", [P("db", "Gain (dB)", -48, 24, 0)], (b, p, _, ct) => Scale(b, Db(p["db"]))),
        new("Normalize", "⌗", "Scale peak to a target level.", [P("db", "Peak target (dB)", -24, 0, -1)], Normalize),
        new("Reverse", "⟲", "Play backwards.", [], Reverse),
        new("Speed", "≫", "Resample speed (pitch follows).", [P("factor", "Factor", 0.25, 4, 1)], Speed),
        new("Silence", "◌", "Pad silence at start and end.", [P("lead", "Lead (s)", 0, 30, 0), P("tail", "Tail (s)", 0, 30, 1)], PadSilence),
        new("Echo", "≋", "Feedback delay.", [P("ms", "Delay (ms)", 10, 2000, 350), P("fb", "Feedback", 0, 0.95, 0.4), P("mix", "Mix", 0, 1, 0.5)], Echo),
        new("Reverb", "◈", "Schroeder-style room reverb.", [P("room", "Room size", 0.3, 0.95, 0.75), P("damp", "Damping", 0, 0.9, 0.4), P("mix", "Mix", 0, 1, 0.3)], Reverb),
        new("Low-Pass", "⌄", "Remove highs.", [P("hz", "Cutoff (Hz)", 40, 20000, 8000), P("q", "Q", 0.3, 4, 0.707)], (b, p, _, ct) => Biq(b, r => BiQuadFilter.LowPassFilter(r, (float)Math.Min(p["hz"], r * 0.45), (float)p["q"]), ct)),
        new("High-Pass", "⌃", "Remove lows.", [P("hz", "Cutoff (Hz)", 20, 10000, 120), P("q", "Q", 0.3, 4, 0.707)], (b, p, _, ct) => Biq(b, r => BiQuadFilter.HighPassFilter(r, (float)Math.Min(p["hz"], r * 0.45), (float)p["q"]), ct)),
        new("Parametric EQ", "▣", "Peaking band.", [P("hz", "Frequency (Hz)", 30, 16000, 1000), P("q", "Q", 0.2, 10, 1), P("db", "Gain (dB)", -18, 18, 3)], (b, p, _, ct) => Biq(b, r => BiQuadFilter.PeakingEQ(r, (float)Math.Min(p["hz"], r * 0.45), (float)p["q"], (float)p["db"]), ct)),
        new("Compressor", "▭", "Dynamic range compression.", [P("thr", "Threshold (dB)", -60, 0, -18), P("ratio", "Ratio", 1, 20, 4), P("att", "Attack (ms)", 0.1, 200, 10), P("rel", "Release (ms)", 5, 1500, 120), P("mk", "Makeup (dB)", 0, 24, 3)], Compressor),
        new("Limiter", "⊓", "Brick-wall ceiling.", [P("ceil", "Ceiling (dB)", -24, 0, -1), P("rel", "Release (ms)", 5, 1000, 80)], Limiter),
        new("Noise Gate", "⊔", "Mute below threshold.", [P("thr", "Threshold (dB)", -80, -5, -45), P("att", "Attack (ms)", 0.1, 100, 5), P("rel", "Release (ms)", 5, 1000, 150)], Gate),
        new("Distortion", "⌁", "Tanh saturation.", [P("drive", "Drive", 1, 50, 8), P("mix", "Mix", 0, 1, 0.6)], Distortion),
        new("Tremolo", "∿", "Amplitude modulation.", [P("hz", "Rate (Hz)", 0.1, 20, 5), P("depth", "Depth", 0, 1, 0.6)], Tremolo),
        new("Pan", "⇌", "Stereo position (mono is promoted to stereo).", [P("pos", "Position (-1 L … +1 R)", -1, 1, 0)], Pan),
        new("DC Offset", "⊖", "Remove per-channel DC bias.", [], RemoveDc),
        new("Trim Silence", "⌁", "Trim leading and trailing audio below a threshold.", [P("threshold", "Threshold (dB)", -90, -20, -60), P("pad", "Edge padding (ms)", 0, 250, 10)], TrimSilence),
        new("RMS Normalize", "◒", "Normalize average level with a peak safety ceiling.", [P("target", "Target RMS (dB)", -36, -6, -18)], NormalizeRms),
        new("De-click", "⌇", "Repair isolated single-sample clicks.", [P("threshold", "Sensitivity", 0.02, 0.9, 0.18)], Declick),
        new("Bit Crusher", "▤", "Reduce bit depth and sample rate for controlled grit.", [P("bits", "Bit depth", 4, 16, 8, 1), P("hold", "Sample hold", 1, 16, 4, 1)], BitCrush),
        new("Chorus", "≋", "Modulated short delay for width and movement.", [P("rate", "Rate (Hz)", 0.1, 5, 0.8), P("depth", "Depth", 0, 1, 0.5), P("mix", "Mix", 0, 1, 0.35)], (b, p, _, ct) => ModulatedDelay(b, p, false, ct)),
        new("Flanger", "⟿", "Short sweeping comb-filter modulation.", [P("rate", "Rate (Hz)", 0.05, 3, 0.25), P("depth", "Depth", 0, 1, 0.65), P("mix", "Mix", 0, 1, 0.4)], (b, p, _, ct) => ModulatedDelay(b, p, true, ct)),
        new("Stereo Width", "◫", "Adjust stereo side energy without changing the center.", [P("width", "Width", 0, 2, 1.3)], StereoWidth),
        new("Mono Downmix", "⊙", "Create a phase-aware mono sum.", [], MonoDownmix),
        new("Swap Channels", "⇄", "Exchange left and right channels.", [], SwapChannels),
        new("Bass Focus", "▂", "Apply a low-frequency shelf.", [P("gain", "Bass gain (dB)", -12, 12, 3)], BassFocus),
        new("Presence Lift", "▴", "Add or reduce upper-mid presence.", [P("gain", "Presence gain (dB)", -12, 12, 3)], PresenceLift),
        new("Transient Shaper", "⌃", "Emphasize or soften attacks relative to sustain.", [P("amount", "Amount", 0, 2, 0.7), P("attack", "Attack (ms)", 1, 100, 10), P("release", "Release (ms)", 10, 500, 120)], TransientShape),
        new("Auto Pan", "⤾", "Oscillate stereo position for rhythmic movement.", [P("rate", "Rate (Hz)", 0.05, 8, 0.4), P("depth", "Depth", 0, 1, 0.8)], AutoPan),
        new("Soft Clip", "⌒", "Add warm saturation with a normalized soft knee.", [P("drive", "Drive", 1, 12, 2.5), P("mix", "Mix", 0, 1, 1)], SoftClip),
    ];

    static AudioBuffer Cut(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, s = Fr(b, p["start"] / 100 * b.Seconds), e = Math.Max(s, Fr(b, p["end"] / 100 * b.Seconds));
        if (p["mode"] < 0.5) { var k = new float[(e - s) * ch]; Array.Copy(b.Data, s * ch, k, 0, k.Length); return b.With(k); }
        var d = new float[(b.Frames - (e - s)) * ch];
        Array.Copy(b.Data, 0, d, 0, s * ch); Array.Copy(b.Data, e * ch, d, s * ch, (b.Frames - e) * ch);
        return b.With(d);
    }

    static AudioBuffer Merge(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? other, CancellationToken ct)
    {
        if (other is null) throw new InvalidOperationException("Merge needs a second track.");
        var o = Mixer.Conform(other, b.Rate, b.Channels); int ch = b.Channels;
        int xf = Math.Min((int)(p["xfade"] * b.Rate), Math.Min(b.Frames, o.Frames));
        var d = new float[(b.Frames + o.Frames - xf) * ch];
        Array.Copy(b.Data, d, b.Data.Length);
        int start = (b.Frames - xf) * ch;
        for (int i = 0; i < xf; i++)
        {
            float t = (i + 0.5f) / xf, a = MathF.Cos(t * MathF.PI / 2), c = MathF.Sin(t * MathF.PI / 2);
            for (int k = 0; k < ch; k++) d[start + i * ch + k] = d[start + i * ch + k] * a + o.Data[i * ch + k] * c;
        }
        Array.Copy(o.Data, xf * ch, d, start + xf * ch, o.Data.Length - xf * ch);
        return b.With(d);
    }

    static AudioBuffer Fade(AudioBuffer b, double dur, bool fadeIn)
    {
        var d = b.CloneData(); int ch = b.Channels, n = Math.Min(b.Frames, (int)(dur * b.Rate));
        for (int i = 0; i < n; i++)
        {
            float g = (i + 0.5f) / n; int f = fadeIn ? i : b.Frames - 1 - i;
            for (int k = 0; k < ch; k++) d[f * ch + k] *= g;
        }
        return b.With(d);
    }

    static AudioBuffer Scale(AudioBuffer b, float g) { var d = new float[b.Data.Length]; for (int i = 0; i < d.Length; i++) d[i] = b.Data[i] * g; return b.With(d); }

    static AudioBuffer Normalize(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        float peak = 0; foreach (var s in b.Data) { var a = Math.Abs(s); if (a > peak) peak = a; }
        return peak < 1e-9f ? b : Scale(b, Db(p["db"]) / peak);
    }

    static AudioBuffer Reverse(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length];
        for (int i = 0; i < n; i++) Array.Copy(b.Data, (n - 1 - i) * ch, d, i * ch, ch);
        return b.With(d);
    }

    static AudioBuffer Speed(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        double f = p["factor"]; int ch = b.Channels, outF = (int)(b.Frames / f); var d = new float[outF * ch];
        for (int i = 0; i < outF; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            double pos = i * f; int i0 = (int)pos, i1 = Math.Min(i0 + 1, b.Frames - 1); float fr = (float)(pos - i0);
            for (int k = 0; k < ch; k++) d[i * ch + k] = b.Data[i0 * ch + k] * (1 - fr) + b.Data[i1 * ch + k] * fr;
        }
        return b.With(d);
    }

    static AudioBuffer PadSilence(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, lead = (int)(p["lead"] * b.Rate) * ch, tail = (int)(p["tail"] * b.Rate) * ch;
        var d = new float[lead + b.Data.Length + tail]; Array.Copy(b.Data, 0, d, lead, b.Data.Length); return b.With(d);
    }

    static AudioBuffer Echo(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, D = Math.Max(1, (int)(p["ms"] / 1000 * b.Rate)); float fb = (float)p["fb"], mix = (float)p["mix"];
        int total = b.Frames + (int)Math.Min(10.0 * b.Rate, (double)D * 6);
        var o = new float[total * ch]; var line = new float[D * ch]; int w = 0;
        for (int i = 0; i < total; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            for (int k = 0; k < ch; k++)
            {
                float x = i < b.Frames ? b.Data[i * ch + k] : 0, del = line[w * ch + k];
                line[w * ch + k] = x + del * fb; o[i * ch + k] = x + del * mix;
            }
            if (++w == D) w = 0;
        }
        return b.With(o);
    }

    sealed class Comb(int n, float fb, float damp)
    {
        readonly float[] _b = new float[n]; int _i; float _s;
        public float Run(float x) { float o = _b[_i]; _s = o * (1 - damp) + _s * damp; _b[_i] = x + _s * fb; if (++_i == _b.Length) _i = 0; return o; }
    }
    sealed class AllPass(int n)
    {
        readonly float[] _b = new float[n]; int _i;
        public float Run(float x) { float bo = _b[_i]; float o = bo - x; _b[_i] = x + bo * 0.5f; if (++_i == _b.Length) _i = 0; return o; }
    }

    static AudioBuffer Reverb(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels; float room = (float)p["room"], damp = (float)p["damp"], mix = (float)p["mix"];
        int tail = 2 * b.Rate, total = b.Frames + tail; var o = new float[total * ch];
        for (int k = 0; k < ch; k++)
        {
            double sc = b.Rate / 44100.0; int spread = 23 * k;
            var combs = new Comb[CombDelays.Length];
            for (int i = 0; i < combs.Length; i++)
                combs[i] = new Comb(Math.Max(1, (int)((CombDelays[i] + spread) * sc)), room, damp);
            var aps = new AllPass[AllPassDelays.Length];
            for (int i = 0; i < aps.Length; i++)
                aps[i] = new AllPass(Math.Max(1, (int)((AllPassDelays[i] + spread) * sc)));
            for (int i = 0; i < total; i++)
            {
                if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
                float x = i < b.Frames ? b.Data[i * ch + k] : 0, inp = x * 0.03f, w = 0;
                foreach (var c in combs) w += c.Run(inp);
                foreach (var a in aps) w = a.Run(w);
                o[i * ch + k] = x * (1 - mix * 0.5f) + w * 3f * mix;
            }
        }
        return b.With(o);
    }

    private static AudioBuffer Biq(AudioBuffer b, Func<float, BiQuadFilter> make, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length];
        for (int k = 0; k < ch; k++)
        {
            var f = make(b.Rate);
            for (int i = 0; i < n; i++) { if ((i & Chk) == 0) ct.ThrowIfCancellationRequested(); d[i * ch + k] = f.Transform(b.Data[i * ch + k]); }
        }
        return b.With(d);
    }

    static float FramePeak(float[] d, int i, int ch) { float m = 0; for (int k = 0; k < ch; k++) { var a = Math.Abs(d[i * ch + k]); if (a > m) m = a; } return m; }
    static float Coef(double ms, int rate) => (float)Math.Exp(-1.0 / (Math.Max(0.01, ms) * 0.001 * rate));

    static AudioBuffer Compressor(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length];
        float ac = Coef(p["att"], b.Rate), rc = Coef(p["rel"], b.Rate), env = 0; double thr = p["thr"], inv = 1 - 1 / p["ratio"];
        for (int i = 0; i < n; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            float pk = FramePeak(b.Data, i, ch); float c = pk > env ? ac : rc; env = c * env + (1 - c) * pk;
            double over = 20 * Math.Log10(env + 1e-9) - thr; float g = Db((over > 0 ? -over * inv : 0) + p["mk"]);
            for (int k = 0; k < ch; k++) d[i * ch + k] = b.Data[i * ch + k] * g;
        }
        return b.With(d);
    }

    static AudioBuffer Limiter(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length]; float ceil = Db(p["ceil"]), rc = Coef(p["rel"], b.Rate), g = 1;
        for (int i = 0; i < n; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            float pk = FramePeak(b.Data, i, ch), target = pk > ceil ? ceil / pk : 1;
            g = target < g ? target : rc * g + (1 - rc) * target;
            for (int k = 0; k < ch; k++) d[i * ch + k] = Math.Clamp(b.Data[i * ch + k] * g, -ceil, ceil);
        }
        return b.With(d);
    }

    static AudioBuffer Gate(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length];
        float thr = Db(p["thr"]), ac = Coef(p["att"], b.Rate), rc = Coef(p["rel"], b.Rate), env = 0, g = 0;
        for (int i = 0; i < n; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            float pk = FramePeak(b.Data, i, ch); env = pk > env ? pk : rc * env + (1 - rc) * pk;
            float target = env > thr ? 1 : 0; float c = target > g ? ac : rc; g = c * g + (1 - c) * target;
            for (int k = 0; k < ch; k++) d[i * ch + k] = b.Data[i * ch + k] * g;
        }
        return b.With(d);
    }

    static AudioBuffer Distortion(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var d = new float[b.Data.Length]; double dr = p["drive"], norm = Math.Tanh(dr); float mix = (float)p["mix"];
        for (int i = 0; i < d.Length; i++) { if ((i & Chk) == 0) ct.ThrowIfCancellationRequested(); d[i] = b.Data[i] * (1 - mix) + (float)(Math.Tanh(dr * b.Data[i]) / norm) * mix; }
        return b.With(d);
    }

    static AudioBuffer Tremolo(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int ch = b.Channels, n = b.Frames; var d = new float[b.Data.Length]; double w = 2 * Math.PI * p["hz"] / b.Rate, depth = p["depth"];
        for (int i = 0; i < n; i++)
        {
            float g = (float)(1 - depth * 0.5 * (1 - Math.Cos(w * i)));
            for (int k = 0; k < ch; k++) d[i * ch + k] = b.Data[i * ch + k] * g;
        }
        return b.With(d);
    }

    static AudioBuffer Pan(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var s = Mixer.Conform(b, b.Rate, 2); var d = new float[s.Data.Length];
        double a = (p["pos"] + 1) * Math.PI / 4; float gl = (float)(Math.Cos(a) * Math.Sqrt(2)), gr = (float)(Math.Sin(a) * Math.Sqrt(2));
        for (int i = 0; i < d.Length; i += 2) { d[i] = s.Data[i] * gl; d[i + 1] = s.Data[i + 1] * gr; }
        return s.With(d);
    }

    static AudioBuffer RemoveDc(AudioBuffer b, IReadOnlyDictionary<string, double> _, AudioBuffer? __, CancellationToken ct)
    {
        int channels = b.Channels, frames = b.Frames;
        if (frames == 0) return b;
        var means = new double[channels];
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++) means[channel] += b.Data[offset + channel];
        }
        for (int channel = 0; channel < channels; channel++) means[channel] /= frames;
        var output = new float[b.Data.Length];
        for (int i = 0; i < output.Length; i++) output[i] = (float)(b.Data[i] - means[i % channels]);
        return b.With(output);
    }

    static AudioBuffer TrimSilence(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int channels = b.Channels, first = 0, last = b.Frames - 1;
        float threshold = Db(p["threshold"]);
        while (first <= last && FramePeak(b.Data, first, channels) < threshold)
        {
            if ((first & Chk) == 0) ct.ThrowIfCancellationRequested();
            first++;
        }
        if (first > last) return b.With([]);
        while (last >= first && FramePeak(b.Data, last, channels) < threshold)
        {
            if ((last & Chk) == 0) ct.ThrowIfCancellationRequested();
            last--;
        }
        int padding = (int)(p["pad"] * 0.001 * b.Rate);
        first = Math.Max(0, first - padding);
        last = Math.Min(b.Frames - 1, last + padding);
        int length = (last - first + 1) * channels;
        var output = new float[length];
        Array.Copy(b.Data, first * channels, output, 0, length);
        return b.With(output);
    }

    static AudioBuffer NormalizeRms(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        if (b.Data.Length == 0) return b;
        double sum = 0;
        float peak = 0;
        for (int i = 0; i < b.Data.Length; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            float sample = b.Data[i];
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        double rms = Math.Sqrt(sum / b.Data.Length);
        if (rms < 1e-9) return b;
        float gain = (float)(Db(p["target"]) / rms);
        gain = Math.Min(gain, peak > 0 ? 0.99f / peak : gain);
        return Scale(b, gain);
    }

    static AudioBuffer Declick(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        int channels = b.Channels, frames = b.Frames;
        if (frames < 3) return b;
        var output = b.CloneData();
        float threshold = (float)p["threshold"];
        for (int frame = 1; frame < frames - 1; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float previous = b.Data[offset - channels + channel];
                float current = b.Data[offset + channel];
                float next = b.Data[offset + channels + channel];
                float interpolation = (previous + next) * 0.5f;
                if (Math.Abs(current - interpolation) > threshold && Math.Abs(previous - next) < threshold * 0.5f)
                    output[offset + channel] = interpolation;
            }
        }
        return b.With(output);
    }

    static AudioBuffer BitCrush(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var output = new float[b.Data.Length];
        int channels = b.Channels, hold = (int)p["hold"];
        int levels = (1 << (int)p["bits"]) - 1;
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * channels;
            int heldOffset = frame / hold * hold * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = b.Data[heldOffset + channel];
                output[offset + channel] = MathF.Round(Math.Clamp(sample, -1, 1) * levels) / levels;
            }
        }
        return b.With(output);
    }

    static AudioBuffer ModulatedDelay(AudioBuffer b, IReadOnlyDictionary<string, double> p, bool flanger, CancellationToken ct)
    {
        int channels = b.Channels, frames = b.Frames;
        int maxDelay = Math.Max(2, (int)(b.Rate * (flanger ? 0.009 : 0.035)) + 2);
        var delayLine = new float[maxDelay * channels];
        var output = new float[b.Data.Length];
        double rate = p["rate"], depth = p["depth"];
        float mix = (float)p["mix"];
        double baseDelay = b.Rate * (flanger ? 0.002 : 0.018);
        double range = b.Rate * (flanger ? 0.0015 : 0.012) * depth;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            double delay = Math.Clamp(baseDelay + range * (0.5 + 0.5 * Math.Sin(2 * Math.PI * rate * frame / b.Rate)), 1, maxDelay - 1);
            int wholeDelay = (int)delay;
            float fraction = (float)(delay - wholeDelay);
            int write = frame % maxDelay;
            int read1 = (write - wholeDelay + maxDelay) % maxDelay;
            int read2 = (read1 - 1 + maxDelay) % maxDelay;
            for (int channel = 0; channel < channels; channel++)
            {
                int outputOffset = frame * channels + channel;
                delayLine[write * channels + channel] = b.Data[outputOffset];
                float delayed = delayLine[read1 * channels + channel] * (1 - fraction) + delayLine[read2 * channels + channel] * fraction;
                output[outputOffset] = b.Data[outputOffset] * (1 - mix) + delayed * mix;
            }
        }
        return b.With(output);
    }

    static AudioBuffer StereoWidth(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var source = Mixer.Conform(b, b.Rate, 2);
        var output = new float[source.Data.Length];
        float width = (float)p["width"];
        for (int i = 0; i < output.Length; i += 2)
        {
            if ((i & (Chk * 2)) == 0) ct.ThrowIfCancellationRequested();
            float mid = (source.Data[i] + source.Data[i + 1]) * 0.5f;
            float side = (source.Data[i] - source.Data[i + 1]) * 0.5f * width;
            output[i] = mid + side;
            output[i + 1] = mid - side;
        }
        return source.With(output);
    }

    static AudioBuffer MonoDownmix(AudioBuffer b, IReadOnlyDictionary<string, double> _, AudioBuffer? __, CancellationToken ct)
    {
        if (b.Channels == 1) return b;
        var output = new float[b.Frames];
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            double sum = 0;
            int offset = frame * b.Channels;
            for (int channel = 0; channel < b.Channels; channel++) sum += b.Data[offset + channel];
            output[frame] = (float)(sum / b.Channels);
        }
        return new AudioBuffer(output, b.Rate, 1);
    }

    static AudioBuffer SwapChannels(AudioBuffer b, IReadOnlyDictionary<string, double> _, AudioBuffer? __, CancellationToken ct)
    {
        if (b.Channels < 2) return b;
        var output = b.CloneData();
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * b.Channels;
            (output[offset], output[offset + 1]) = (output[offset + 1], output[offset]);
        }
        return b.With(output);
    }

    static AudioBuffer BassFocus(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var output = new float[b.Data.Length];
        int channels = b.Channels;
        float alpha = (float)(1 - Math.Exp(-2 * Math.PI * Math.Min(180, b.Rate * 0.2) / b.Rate));
        float gain = Db(p["gain"]) - 1;
        var low = new float[channels];
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = b.Data[offset + channel];
                low[channel] += alpha * (sample - low[channel]);
                output[offset + channel] = sample + low[channel] * gain;
            }
        }
        return b.With(output);
    }

    static AudioBuffer PresenceLift(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var output = new float[b.Data.Length];
        int channels = b.Channels;
        float alpha = (float)(1 - Math.Exp(-2 * Math.PI * Math.Min(2500, b.Rate * 0.4) / b.Rate));
        float gain = Db(p["gain"]) - 1;
        var low = new float[channels];
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = b.Data[offset + channel];
                low[channel] += alpha * (sample - low[channel]);
                output[offset + channel] = sample + (sample - low[channel]) * gain;
            }
        }
        return b.With(output);
    }

    static AudioBuffer TransientShape(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var output = new float[b.Data.Length];
        int channels = b.Channels;
        float attack = Coef(p["attack"], b.Rate), release = Coef(p["release"], b.Rate);
        float fast = 0, slow = 0, amount = (float)p["amount"];
        for (int frame = 0; frame < b.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            float peak = FramePeak(b.Data, frame, channels);
            fast = (peak > fast ? attack : release) * fast + (1 - (peak > fast ? attack : release)) * peak;
            slow = release * slow + (1 - release) * peak;
            float gain = Math.Clamp(1 + amount * (fast - slow) / (slow + 0.02f), 0.25f, 3f);
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++) output[offset + channel] = b.Data[offset + channel] * gain;
        }
        return b.With(output);
    }

    static AudioBuffer AutoPan(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        var source = Mixer.Conform(b, b.Rate, 2);
        var output = new float[source.Data.Length];
        double phaseStep = 2 * Math.PI * p["rate"] / b.Rate;
        double depth = p["depth"];
        for (int frame = 0; frame < source.Frames; frame++)
        {
            if ((frame & Chk) == 0) ct.ThrowIfCancellationRequested();
            double position = Math.Sin(phaseStep * frame) * depth;
            double angle = (position + 1) * Math.PI / 4;
            float left = (float)(Math.Cos(angle) * Math.Sqrt(2));
            float right = (float)(Math.Sin(angle) * Math.Sqrt(2));
            int offset = frame * 2;
            float mono = (source.Data[offset] + source.Data[offset + 1]) * 0.5f;
            output[offset] = mono * left;
            output[offset + 1] = mono * right;
        }
        return source.With(output);
    }

    static AudioBuffer SoftClip(AudioBuffer b, IReadOnlyDictionary<string, double> p, AudioBuffer? _, CancellationToken ct)
    {
        float drive = (float)p["drive"], mix = (float)p["mix"];
        float normalization = (float)Math.Tanh(drive);
        var output = new float[b.Data.Length];
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & Chk) == 0) ct.ThrowIfCancellationRequested();
            float sample = b.Data[i];
            float clipped = (float)Math.Tanh(drive * sample) / normalization;
            output[i] = sample * (1 - mix) + clipped * mix;
        }
        return b.With(output);
    }
}
