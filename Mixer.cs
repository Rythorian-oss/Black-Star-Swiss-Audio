// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
namespace BlackStarSwissAudio.Core;

public readonly record struct MixItem(AudioBuffer Buf, double OffsetSec, float Gain, float Pan);

public static class Mixer
{
    /// <summary>Converts channel layout and sample rate (linear interpolation).</summary>
    public static AudioBuffer Conform(AudioBuffer b, int rate, int ch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ch);
        if (b.Rate == rate && b.Channels == ch) return b;

        int frames = b.Frames;
        double ratio = (double)b.Rate / rate;
        double outputFrames = frames / ratio;
        if (!double.IsFinite(outputFrames) || outputFrames > Array.MaxLength / ch)
            throw new InvalidOperationException("Converted audio is too large for in-memory processing.");

        int outFrames = (int)outputFrames;
        var output = new float[outFrames * ch];
        for (int i = 0; i < outFrames; i++)
        {
            double position = i * ratio;
            int frame0 = (int)position;
            int frame1 = Math.Min(frame0 + 1, frames - 1);
            float fraction = (float)(position - frame0);
            for (int channel = 0; channel < ch; channel++)
            {
                float first = MapSample(b, frame0, channel, ch);
                float second = MapSample(b, frame1, channel, ch);
                output[i * ch + channel] = first * (1 - fraction) + second * fraction;
            }
        }
        return new AudioBuffer(output, rate, ch);
    }

    static float MapSample(AudioBuffer buffer, int frame, int channel, int targetChannels)
    {
        int sourceChannels = buffer.Channels;
        if (sourceChannels == targetChannels) return buffer.Data[frame * sourceChannels + channel];
        if (sourceChannels == 1) return buffer.Data[frame];
        if (targetChannels == 1)
        {
            float sum = 0;
            for (int sourceChannel = 0; sourceChannel < sourceChannels; sourceChannel++)
                sum += buffer.Data[frame * sourceChannels + sourceChannel];
            return sum / sourceChannels;
        }
        return buffer.Data[frame * sourceChannels + channel % sourceChannels];
    }

    public static AudioBuffer MendJoin(AudioBuffer first, AudioBuffer second, AudioBuffer bridge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(bridge);
        if (first.Channels <= 0 || first.Rate <= 0) throw new ArgumentException("The first clip has an invalid format.", nameof(first));

        var right = Conform(second, first.Rate, first.Channels);
        var effect = Conform(bridge, first.Rate, first.Channels);
        int channels = first.Channels;
        int crossfadeFrames = Math.Min((int)(first.Rate * 0.08), Math.Min(first.Frames, right.Frames));
        long outputFrames = (long)first.Frames + right.Frames - crossfadeFrames;
        if (outputFrames > Array.MaxLength / channels) throw new InvalidOperationException("Mended audio is too large for in-memory processing.");

        var output = new float[(int)outputFrames * channels];
        Array.Copy(first.Data, output, first.Data.Length);
        int seamStart = first.Frames - crossfadeFrames;
        for (int frame = 0; frame < crossfadeFrames; frame++)
        {
            if ((frame & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            float t = (frame + 0.5f) / crossfadeFrames;
            float leftGain = MathF.Cos(t * MathF.PI / 2);
            float rightGain = MathF.Sin(t * MathF.PI / 2);
            int destination = (seamStart + frame) * channels;
            int source = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                output[destination + channel] = output[destination + channel] * leftGain + right.Data[source + channel] * rightGain;
        }
        Array.Copy(right.Data, crossfadeFrames * channels, output, first.Data.Length, right.Data.Length - crossfadeFrames * channels);

        int bridgeFrames = (int)Math.Min(Math.Min(effect.Frames, (int)(first.Rate * 0.75)), outputFrames);
        if (bridgeFrames > 0)
        {
            int bridgeStart = (int)Math.Clamp((long)seamStart - bridgeFrames / 2L, 0, outputFrames - bridgeFrames);
            int sourceStart = Math.Max(0, (effect.Frames - bridgeFrames) / 2);
            for (int frame = 0; frame < bridgeFrames; frame++)
            {
                if ((frame & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                float fadeIn = Math.Min(1, (frame + 1) / Math.Max(1f, bridgeFrames * 0.2f));
                float fadeOut = Math.Min(1, (bridgeFrames - frame) / Math.Max(1f, bridgeFrames * 0.2f));
                float gain = 0.28f * Math.Min(fadeIn, fadeOut);
                int destination = (bridgeStart + frame) * channels;
                int source = (sourceStart + frame) * channels;
                for (int channel = 0; channel < channels; channel++)
                    output[destination + channel] += effect.Data[source + channel] * gain;
            }
        }
        return new AudioBuffer(output, first.Rate, channels);
    }

    /// <summary>Sums all items into one stereo buffer; scales down only if the sum would clip.</summary>
    public static AudioBuffer? Mixdown(IReadOnlyList<MixItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return null;
        int rate = Math.Max(44100, items.Max(i => i.Buf.Rate));
        long total = 0;
        foreach (var it in items)
        {
            if (!double.IsFinite(it.OffsetSec) || it.OffsetSec < 0) throw new ArgumentOutOfRangeException(nameof(items), "Track offsets must be finite and non-negative.");
            double endFrames = it.OffsetSec * rate + it.Buf.Seconds * rate + 1;
            if (!double.IsFinite(endFrames) || endFrames > Array.MaxLength / 2)
                throw new InvalidOperationException("Mix is too long for in-memory processing.");
            total = Math.Max(total, (long)endFrames);
        }
        var mix = new float[(int)(total * 2)];
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var c = Conform(it.Buf, rate, 2);
            double a = (it.Pan + 1) * Math.PI / 4;
            float gl = it.Gain * (float)(Math.Cos(a) * Math.Sqrt(2)), gr = it.Gain * (float)(Math.Sin(a) * Math.Sqrt(2));
            long start = (long)(it.OffsetSec * rate) * 2;
            for (int i = 0; i < c.Data.Length; i += 2)
            {
                long d = start + i;
                if (d + 1 >= mix.Length) break;
                mix[d] += c.Data[i] * gl; mix[d + 1] += c.Data[i + 1] * gr;
            }
        }
        float peak = 0;
        foreach (var s in mix) { var a = Math.Abs(s); if (a > peak) peak = a; }
        if (peak > 0.99f) { float g = 0.99f / peak; for (int i = 0; i < mix.Length; i++) mix[i] *= g; }
        return new AudioBuffer(mix, rate, 2);
    }
}
