using System.Windows;
using System.Windows.Controls;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Controls;

public partial class DiffViewerControl : UserControl
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IReadOnlyList<DiffLine>), typeof(DiffViewerControl), new PropertyMetadata(null));

    /// <summary>Set to a line index to bring that line into view; used to step between changes.</summary>
    public static readonly DependencyProperty ScrollToIndexProperty = DependencyProperty.Register(
        nameof(ScrollToIndex), typeof(int), typeof(DiffViewerControl), new PropertyMetadata(-1, OnScrollToIndexChanged));

    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(
        nameof(EmptyMessage), typeof(string), typeof(DiffViewerControl),
        new PropertyMetadata("Nothing to compare yet."));

    public IReadOnlyList<DiffLine>? Lines
    {
        get => (IReadOnlyList<DiffLine>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public int ScrollToIndex
    {
        get => (int)GetValue(ScrollToIndexProperty);
        set => SetValue(ScrollToIndexProperty, value);
    }

    public string EmptyMessage
    {
        get => (string)GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    public DiffViewerControl() => InitializeComponent();

    private static void OnScrollToIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not DiffViewerControl viewer || e.NewValue is not int index || index < 0)
        {
            return;
        }

        // The list virtualises, so wait for layout before asking it to scroll.
        viewer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (viewer.Lines is { Count: > 0 } lines && index < lines.Count)
            {
                viewer.LineList.ScrollIntoView(lines[index]);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
