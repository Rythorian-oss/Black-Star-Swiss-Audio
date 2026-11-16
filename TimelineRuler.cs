// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BlackStarSwissAudio.Controls;

public sealed class TimelineRuler : FrameworkElement
{
    public static readonly DependencyProperty ViewSecondsProperty = DependencyProperty.Register(nameof(ViewSeconds), typeof(double), typeof(TimelineRuler), new FrameworkPropertyMetadata(60d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlaybackSecondsProperty = DependencyProperty.Register(nameof(PlaybackSeconds), typeof(double), typeof(TimelineRuler), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewStartSecondsProperty = DependencyProperty.Register(nameof(ViewStartSeconds), typeof(double), typeof(TimelineRuler), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ViewSeconds { get => (double)GetValue(ViewSecondsProperty); set => SetValue(ViewSecondsProperty, value); }
    public double ViewStartSeconds { get => (double)GetValue(ViewStartSecondsProperty); set => SetValue(ViewStartSecondsProperty, value); }
    public double PlaybackSeconds { get => (double)GetValue(PlaybackSecondsProperty); set => SetValue(PlaybackSecondsProperty, value); }

    static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x4B)));
    static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xC9, 0xA2, 0x4B)), 1));
    static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x66)), 2));
    static readonly Typeface LabelTypeface = new("Segoe UI");
    readonly record struct TickMark(double X, FormattedText Label);
    TickMark[] _ticks = [];
    double _cachedWidth = double.NaN, _cachedView = double.NaN;
    double _cachedViewStart = double.NaN, _cachedPixelsPerDip = double.NaN;

    void EnsureTicks(double width, double pixelsPerDip)
    {
        if (width == _cachedWidth && ViewSeconds == _cachedView &&
            ViewStartSeconds == _cachedViewStart && pixelsPerDip == _cachedPixelsPerDip) return;

        _cachedWidth = width;
        _cachedView = ViewSeconds;
        _cachedViewStart = ViewStartSeconds;
        _cachedPixelsPerDip = pixelsPerDip;
        if (!double.IsFinite(ViewSeconds) || !double.IsFinite(ViewStartSeconds) || ViewSeconds <= 0)
        {
            _ticks = [];
            return;
        }

        double targetSpacingSeconds = ViewSeconds * 72 / width;
        double tickSeconds = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(targetSpacingSeconds, 0.001))));
        if (tickSeconds * 2 <= targetSpacingSeconds) tickSeconds *= 5;
        else if (tickSeconds * 5 <= targetSpacingSeconds) tickSeconds *= 2;

        List<TickMark> ticks = [];
        for (double seconds = 0; seconds <= ViewSeconds; seconds += tickSeconds)
        {
            double x = seconds / ViewSeconds * width;
            double time = ViewStartSeconds + seconds;
            var label = new FormattedText(TimeSpan.FromSeconds(time).ToString(time >= 60 ? @"m\:ss" : @"s\.f", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 10, TextBrush, pixelsPerDip);
            ticks.Add(new TickMark(x, label));
        }
        _ticks = [.. ticks];
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        if (width <= 0 || ViewSeconds <= 0) return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureTicks(width, pixelsPerDip);
        drawingContext.DrawLine(TickPen, new Point(0, ActualHeight - 1), new Point(width, ActualHeight));
        foreach (var tick in _ticks)
        {
            drawingContext.DrawLine(TickPen, new Point(tick.X, ActualHeight - 8), new Point(tick.X, ActualHeight));
            drawingContext.DrawText(tick.Label, new Point(tick.X + 3, 0));
        }

        if (PlaybackSeconds >= ViewStartSeconds && PlaybackSeconds <= ViewStartSeconds + ViewSeconds)
        {
            double playheadX = (PlaybackSeconds - ViewStartSeconds) / ViewSeconds * width;
            drawingContext.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        }
    }

    static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
