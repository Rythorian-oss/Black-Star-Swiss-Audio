// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
namespace BlackStarSwissAudio.Core;

public sealed record SpectrogramColumn(int Sequence, byte[] Pixels);

public static class SpectrogramAnalysis
{
    public const int Width = 1024;
    public const int Height = 256;
    public const int FftSize = 2048;
    const double NoiseFloorDecibels = -78;
    const double DisplayCeilingDecibels = 18;
    static readonly double[] HannWindow = CreateWindow();
    static readonly (byte Red, byte Green, byte Blue)[] ColorStops =
    [
        (0, 0, 0),
        (18, 38, 170),
        (0, 190, 255),
        (32, 230, 75),
        (255, 220, 30),
        (255, 48, 24)
    ];

    static double[] CreateWindow()
    {
        var window = new double[FftSize];
        for (int i = 0; i < window.Length; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (window.Length - 1));
        return window;
    }

    public static SpectrogramColumn ComputeLiveColumn(AudioBuffer buffer, double centerSeconds, int sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!double.IsFinite(centerSeconds)) throw new ArgumentOutOfRangeException(nameof(centerSeconds));
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new byte[Height * 4];
        if (buffer.Frames == 0) return new SpectrogramColumn(sequence, pixels);

        var real = new double[FftSize];
        var imaginary = new double[FftSize];
        var magnitudes = new double[FftSize / 2];
        int center = (int)Math.Clamp(centerSeconds * buffer.Rate, 0, buffer.Frames);
        int start = center - FftSize / 2;
        int maxBin = FftSize / 2;
        double minFrequency = Math.Max(30, buffer.Rate * 2.0 / FftSize);
        double maxFrequency = Math.Min(buffer.Rate / 2.0, 16000);
        if (maxFrequency <= minFrequency) return new SpectrogramColumn(sequence, pixels);
        double logRange = Math.Log(maxFrequency / minFrequency);
        ComputeMagnitudes(buffer, start, real, imaginary, magnitudes, cancellationToken);
        double peakDecibels = 20 * Math.Log10(magnitudes.Skip(2).Max() + 1e-12);
        if (peakDecibels <= NoiseFloorDecibels) return new SpectrogramColumn(sequence, pixels);

        for (int y = 0; y < Height; y++)
        {
            double frequency = maxFrequency * Math.Exp(-((double)y / (Height - 1)) * logRange);
            double binPosition = Math.Clamp(frequency * FftSize / buffer.Rate, 2, maxBin - 1);
            int lowerBin = (int)binPosition;
            int upperBin = Math.Min(lowerBin + 1, maxBin - 1);
            double fraction = binPosition - lowerBin;
            double magnitude = magnitudes[lowerBin] * (1 - fraction) + magnitudes[upperBin] * fraction;
            double decibels = 20 * Math.Log10(magnitude + 1e-12);
            if (decibels > NoiseFloorDecibels)
                WriteColor(pixels, y * 4, (decibels - NoiseFloorDecibels) / (DisplayCeilingDecibels - NoiseFloorDecibels));
        }

        return new SpectrogramColumn(sequence, pixels);
    }

    static void ComputeMagnitudes(AudioBuffer buffer, int start, double[] real, double[] imaginary,
        double[] magnitudes, CancellationToken cancellationToken)
    {
        Array.Clear(magnitudes);
        int maxBin = FftSize / 2;
        double fullWindowSum = HannWindow.Sum();
        for (int channel = 0; channel < buffer.Channels; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(real);
            Array.Clear(imaginary);
            double weightedSum = 0, channelWindowSum = 0;
            for (int i = 0; i < FftSize; i++)
            {
                int frame = start + i;
                if ((uint)frame >= (uint)buffer.Frames) continue;
                double window = HannWindow[i];
                weightedSum += buffer.Data[frame * buffer.Channels + channel] * window;
                channelWindowSum += window;
            }

            double mean = channelWindowSum > 0 ? weightedSum / channelWindowSum : 0;
            for (int i = 0; i < FftSize; i++)
            {
                int frame = start + i;
                if ((uint)frame < (uint)buffer.Frames)
                    real[i] = (buffer.Data[frame * buffer.Channels + channel] - mean) * HannWindow[i];
            }

            Transform(real, imaginary);
            for (int bin = 0; bin < maxBin; bin++)
                magnitudes[bin] += real[bin] * real[bin] + imaginary[bin] * imaginary[bin];
        }

        for (int bin = 0; bin < maxBin; bin++)
            magnitudes[bin] = fullWindowSum > 0
                ? 2 * Math.Sqrt(magnitudes[bin] / buffer.Channels) / fullWindowSum
                : 0;
    }

    static void Transform(double[] real, double[] imaginary)
    {
        int j = 0;
        for (int i = 1; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1;
            double angle = -2 * Math.PI / size;
            (double stepReal, double stepImaginary) = (Math.Cos(angle), Math.Sin(angle));
            for (int start = 0; start < FftSize; start += size)
            {
                (double twiddleReal, double twiddleImaginary) = (1, 0);
                for (int offset = 0; offset < half; offset++)
                {
                    int even = start + offset, odd = even + half;
                    double oddReal = real[odd] * twiddleReal - imaginary[odd] * twiddleImaginary;
                    double oddImaginary = real[odd] * twiddleImaginary + imaginary[odd] * twiddleReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                    (twiddleReal, twiddleImaginary) = (twiddleReal * stepReal - twiddleImaginary * stepImaginary,
                        twiddleReal * stepImaginary + twiddleImaginary * stepReal);
                }
            }
        }
    }

    static void WriteColor(byte[] pixels, int offset, double intensity)
    {
        double position = Math.Clamp(intensity, 0, 1) * (ColorStops.Length - 1);
        int start = Math.Min((int)position, ColorStops.Length - 2);
        double blend = position - start;
        var (from, to) = (ColorStops[start], ColorStops[start + 1]);
        pixels[offset] = (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * blend);
        pixels[offset + 1] = (byte)Math.Round(from.Green + (to.Green - from.Green) * blend);
        pixels[offset + 2] = (byte)Math.Round(from.Red + (to.Red - from.Red) * blend);
        pixels[offset + 3] = 255;
    }
}
