using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlackStarSwissAudio.Core;
using BlackStarSwissAudio.Services;
using BlackStarSwissAudio.ViewModels;

namespace BlackStarSwissAudio;

public partial class MainWindow : Window, IDisposable
{
    const string ToolFormat = "BSSA.Tool";
    readonly MainViewModel _vm = new();
    Point _dragStart;
    bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closed += OnClosed;
    }

    void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Closed -= OnClosed;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void OnDragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ToolFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object s, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await _vm.ImportFilesAsync(files);
            else if (e.Data.GetData(ToolFormat) is string name)
            {
                var tool = MainViewModel.Tools.FirstOrDefault(t => t.Name == name);
                var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                if (tool is not null && item?.DataContext is TrackVM tv) _vm.ApplyToolTo(tv, tool);
                else _vm.Status = "Drop the tool onto a track.";
            }
        }
        catch (Exception ex) { Log.Error("Drop", ex); _vm.Status = $"Error: {ex.Message}"; }
    }

    void ToolList_Down(object s, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    void ToolList_Move(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ToolDef t)
        {
            _vm.SelectedTool = t;
            DragDrop.DoDragDrop((DependencyObject)s, new DataObject(ToolFormat, t.Name), DragDropEffects.Copy);
        }
    }

    void FxList_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) _vm.AddFxCommand.Execute(null);
    }

    void OnMin(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMax(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object s, RoutedEventArgs e) => Close();
    void OnStateChanged(object? s, EventArgs e) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
}
