// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.Windows;
using System.Windows.Media;

namespace BlackStarSwissAudio.Controls;

/// <summary>Draws pre-computed min/max peaks as one frozen geometry; no per-sample work at render time.</summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(IReadOnlyList<float>), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChannelsProperty = DependencyProperty.Register(nameof(Channels), typeof(int), typeof(WaveformView), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OffsetSecondsProperty = DependencyProperty.Register(nameof(OffsetSeconds), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ClipSecondsProperty = DependencyProperty.Register(nameof(ClipSeconds), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewSecondsProperty = DependencyProperty.Register(nameof(ViewSeconds), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(60d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewStartSecondsProperty = DependencyProperty.Register(nameof(ViewStartSeconds), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlaybackSecondsProperty = DependencyProperty.Register(nameof(PlaybackSeconds), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float>? Peaks { get => (IReadOnlyList<float>?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public int Channels { get => (int)GetValue(ChannelsProperty); set => SetValue(ChannelsProperty, value); }
    public double OffsetSeconds { get => (double)GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public double ClipSeconds { get => (double)GetValue(ClipSecondsProperty); set => SetValue(ClipSecondsProperty, value); }
    public double ViewSeconds { get => (double)GetValue(ViewSecondsProperty); set => SetValue(ViewSecondsProperty, value); }
    public double ViewStartSeconds { get => (double)GetValue(ViewStartSecondsProperty); set => SetValue(ViewStartSecondsProperty, value); }
    public double PlaybackSeconds { get => (double)GetValue(PlaybackSecondsProperty); set => SetValue(PlaybackSecondsProperty, value); }

    static readonly Brush Fill = Freeze(new LinearGradientBrush(Color.FromRgb(0xF5, 0xD9, 0x8A), Color.FromRgb(0xC9, 0x42, 0x4B), 90));
    static readonly Brush ProgressFill = Freeze(new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xF0, 0xC0)));
    static readonly Pen Mid = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xC9, 0xA2, 0x4B)), 1));
    static readonly Pen Playhead = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x66)), 2));
    static readonly Brush TrackBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)));
    StreamGeometry[] _waveforms = [];
    RectangleGeometry? _clipGeometry;
    IReadOnlyList<float>? _cachedPeaks;
    int _cachedChannels = -1;
    double _cachedWidth = double.NaN, _cachedHeight = double.NaN;
    double _cachedOffset = double.NaN, _cachedClip = double.NaN;
    double _cachedView = double.NaN, _cachedViewStart = double.NaN;

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    void EnsureGeometry(double width, double height)
    {
        var peaks = Peaks;
        int channels = Channels;
        double offset = OffsetSeconds, clip = ClipSeconds;
        double view = ViewSeconds, viewStart = ViewStartSeconds;
        if (ReferenceEquals(peaks, _cachedPeaks) && channels == _cachedChannels &&
            width == _cachedWidth && height == _cachedHeight && offset == _cachedOffset &&
            clip == _cachedClip && view == _cachedView && viewStart == _cachedViewStart) return;

        _cachedPeaks = peaks;
        _cachedChannels = channels;
        _cachedWidth = width;
        _cachedHeight = height;
        _cachedOffset = offset;
        _cachedClip = clip;
        _cachedView = view;
        _cachedViewStart = viewStart;
        _waveforms = [];
        _clipGeometry = null;

        if (width < 2 || height < 2 || channels <= 0 || !double.IsFinite(offset) ||
            !double.IsFinite(clip) || !double.IsFinite(view) || !double.IsFinite(viewStart) || view <= 0) return;

        double left = Math.Clamp((offset - viewStart) / view * width, 0, width);
        double right = Math.Clamp((offset + clip - viewStart) / view * width, left, width);
        _clipGeometry = Freeze(new RectangleGeometry(new Rect(left, 0, Math.Max(0, right - left), height)));
        if (peaks is null || peaks.Count < channels * 4) return;

        int columns = peaks.Count / (channels * 2);
        double channelHeight = height / channels;
        double scaleX = clip > 0 ? clip / view * width / Math.Max(1, columns - 1) : width / Math.Max(1, columns - 1);
        var waveforms = new StreamGeometry[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            double center = channelHeight * (channel + 0.5);
            double amplitude = channelHeight * 0.43;
            int channelStart = channel * columns * 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(left, center - peaks[channelStart + 1] * amplitude), true, true);
                for (int i = 1; i < columns; i++) context.LineTo(new Point(left + i * scaleX, center - peaks[channelStart + i * 2 + 1] * amplitude), true, false);
                for (int i = columns - 1; i >= 0; i--) context.LineTo(new Point(left + i * scaleX, center - peaks[channelStart + i * 2] * amplitude), true, false);
            }
            geometry.Freeze();
            waveforms[channel] = geometry;
        }
        _waveforms = waveforms;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;
        drawingContext.DrawRectangle(TrackBackground, null, new Rect(0, 0, width, height));
        if (width < 2 || height < 2 || ViewSeconds <= 0) return;

        EnsureGeometry(width, height);
        var clipGeometry = _clipGeometry;
        if (clipGeometry is null) return;
        drawingContext.PushClip(clipGeometry);
        double left = Math.Clamp((OffsetSeconds - ViewStartSeconds) / ViewSeconds * width, 0, width);
        double right = Math.Clamp((OffsetSeconds + ClipSeconds - ViewStartSeconds) / ViewSeconds * width, left, width);
        if (_waveforms.Length == Channels)
        {
            double channelHeight = height / Channels;
            for (int channel = 0; channel < Channels; channel++)
            {
                double center = channelHeight * (channel + 0.5);
                drawingContext.DrawLine(Mid, new Point(left, center), new Point(right, center));
                drawingContext.DrawGeometry(Fill, null, _waveforms[channel]);
                if (channel + 1 < Channels) drawingContext.DrawLine(Mid, new Point(left, channelHeight * (channel + 1)), new Point(right, channelHeight * (channel + 1)));
            }
        }
        double progress = Math.Clamp((PlaybackSeconds - ViewStartSeconds) / ViewSeconds * width, left, right);
        if (double.IsFinite(PlaybackSeconds) && PlaybackSeconds >= OffsetSeconds && PlaybackSeconds <= OffsetSeconds + ClipSeconds && PlaybackSeconds >= ViewStartSeconds && PlaybackSeconds <= ViewStartSeconds + ViewSeconds)
        {
            drawingContext.DrawRectangle(ProgressFill, null, new Rect(left, 0, Math.Max(0, progress - left), height));
            drawingContext.DrawLine(Playhead, new Point(progress, 0), new Point(progress, height));
        }
        drawingContext.Pop();
    }
}
