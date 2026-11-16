// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
namespace BlackStarSwissAudio.Core;

/// <summary>Immutable interleaved float PCM. Because buffers are never mutated, undo history is just references.</summary>
public sealed class AudioBuffer
{
    public AudioBuffer(float[] data, int rate, int channels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (data.Length % channels != 0) throw new ArgumentException("Audio data must contain complete frames.", nameof(data));

        Data = data; Rate = rate; Channels = channels;
    }
    public float[] Data { get; }
    public int Rate { get; }
    public int Channels { get; }
    public int Frames => Data.Length / Channels;
    public double Seconds => (double)Frames / Rate;
    public AudioBuffer With(float[] data) => new(data, Rate, Channels);
    public float[] CloneData() => (float[])Data.Clone();
    public static AudioBuffer Empty(int rate = 44100, int ch = 2) => new([], rate, ch);

    public static float[] ComputePeaks(AudioBuffer b, int cols)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (cols < 0 || cols > Array.MaxLength / (2 * b.Channels)) throw new ArgumentOutOfRangeException(nameof(cols));
        var peaks = new float[cols * 2 * b.Channels];
        int channels = b.Channels, frames = b.Frames;
        if (frames == 0 || cols == 0) return peaks;
        for (int channel = 0; channel < channels; channel++)
        {
            int channelOffset = channel * cols * 2;
            for (int column = 0; column < cols; column++)
            {
                long start = (long)column * frames / cols;
                long end = Math.Max(start + 1, (long)(column + 1) * frames / cols);
                long step = Math.Max(1, (end - start) / 256);
                float min = 0, max = 0;
                for (long frame = start; frame < end && frame < frames; frame += step)
                {
                    float sample = b.Data[frame * channels + channel];
                    if (sample < min) min = sample;
                    if (sample > max) max = sample;
                }
                peaks[channelOffset + column * 2] = min;
                peaks[channelOffset + column * 2 + 1] = max;
            }
        }
        return peaks;
    }
}
