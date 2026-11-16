using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlackStarSwissAudio.Core;

namespace BlackStarSwissAudio.Controls;

public sealed class SpectrogramView : FrameworkElement
{
    public static readonly DependencyProperty LiveColumnProperty = DependencyProperty.Register(
        nameof(LiveColumn), typeof(SpectrogramColumn), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLiveColumnChanged));

    static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Colors.Black));
    static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(49, 94, 101)), 1));
    WriteableBitmap? _liveBitmap;
    byte[]? _livePixels;

    public SpectrogramColumn? LiveColumn
    {
        get => (SpectrogramColumn?)GetValue(LiveColumnProperty);
        set => SetValue(LiveColumnProperty, value);
    }

    static void OnLiveColumnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (SpectrogramView)sender;
        if (args.NewValue is not SpectrogramColumn column)
        {
            view._liveBitmap = null;
            view._livePixels = null;
            view.InvalidateVisual();
            return;
        }

        int width = SpectrogramAnalysis.Width;
        int height = SpectrogramAnalysis.Height;
        view._liveBitmap ??= new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        view._livePixels ??= new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            Buffer.BlockCopy(view._livePixels, rowOffset + 4, view._livePixels, rowOffset, (width - 1) * 4);
            Buffer.BlockCopy(column.Pixels, y * 4, view._livePixels, rowOffset + (width - 1) * 4, 4);
        }
        view._liveBitmap.WritePixels(new Int32Rect(0, 0, width, height), view._livePixels, width * 4, 0);
        view.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(BackgroundBrush, BorderPen, bounds);
        if (ActualWidth <= 0 || ActualHeight <= 0 || _liveBitmap is null) return;

        drawingContext.DrawImage(_liveBitmap, bounds);
    }

    static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
