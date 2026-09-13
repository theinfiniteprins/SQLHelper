using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Controls;

public partial class DiffViewerControl : UserControl
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IReadOnlyList<DiffLine>), typeof(DiffViewerControl), new PropertyMetadata(null, OnLinesChanged));

    /// <summary>A request to bring a line to the top; a new object each time, so repeating a jump still scrolls.</summary>
    public static readonly DependencyProperty ScrollRequestProperty = DependencyProperty.Register(
        nameof(ScrollRequest), typeof(ScrollRequest), typeof(DiffViewerControl), new PropertyMetadata(null, OnScrollRequestChanged));

    public static readonly DependencyProperty CurrentChangeProperty = DependencyProperty.Register(
        nameof(CurrentChange), typeof(int), typeof(DiffViewerControl), new PropertyMetadata(-1));

    public static readonly DependencyProperty TopVisibleLineProperty = DependencyProperty.Register(
        nameof(TopVisibleLine), typeof(int), typeof(DiffViewerControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BottomVisibleLineProperty = DependencyProperty.Register(
        nameof(BottomVisibleLine), typeof(int), typeof(DiffViewerControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BeforeTextProperty = DependencyProperty.Register(
        nameof(BeforeText), typeof(string), typeof(DiffViewerControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AfterTextProperty = DependencyProperty.Register(
        nameof(AfterText), typeof(string), typeof(DiffViewerControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(
        nameof(EmptyMessage), typeof(string), typeof(DiffViewerControl),
        new PropertyMetadata("Nothing to compare yet."));

    private static readonly DependencyPropertyKey RowWidthPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(RowWidth), typeof(double), typeof(DiffViewerControl), new PropertyMetadata(0.0));

    public static readonly DependencyProperty RowWidthProperty = RowWidthPropertyKey.DependencyProperty;

    private ScrollViewer? _scrollViewer;
    private ScrollRequest? _pendingScroll;

    public DiffViewerControl()
    {
        InitializeComponent();

        Loaded += (_, _) => AttachScrollViewer();
        IsVisibleChanged += (_, _) => ApplyPendingScroll();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, (_, _) => LineList.SelectAll()));
    }

    public IReadOnlyList<DiffLine>? Lines
    {
        get => (IReadOnlyList<DiffLine>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public ScrollRequest? ScrollRequest
    {
        get => (ScrollRequest?)GetValue(ScrollRequestProperty);
        set => SetValue(ScrollRequestProperty, value);
    }

    public int CurrentChange
    {
        get => (int)GetValue(CurrentChangeProperty);
        set => SetValue(CurrentChangeProperty, value);
    }

    public int TopVisibleLine
    {
        get => (int)GetValue(TopVisibleLineProperty);
        set => SetValue(TopVisibleLineProperty, value);
    }

    public int BottomVisibleLine
    {
        get => (int)GetValue(BottomVisibleLineProperty);
        set => SetValue(BottomVisibleLineProperty, value);
    }

    public string BeforeText
    {
        get => (string)GetValue(BeforeTextProperty);
        set => SetValue(BeforeTextProperty, value);
    }

    public string AfterText
    {
        get => (string)GetValue(AfterTextProperty);
        set => SetValue(AfterTextProperty, value);
    }

    public string EmptyMessage
    {
        get => (string)GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    /// <summary>The width of the longest line, so every row shares one horizontal extent.</summary>
    public double RowWidth => (double)GetValue(RowWidthProperty);

    // ------------------------------------------------------------------ scrolling

    private static void OnScrollRequestChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DiffViewerControl viewer && e.NewValue is ScrollRequest request)
        {
            viewer._pendingScroll = request;
            viewer.ApplyPendingScroll();
        }
    }

    private static void OnLinesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not DiffViewerControl viewer)
        {
            return;
        }

        viewer.MeasureRowWidth();
        viewer.ApplyPendingScroll();
    }

    /// <summary>
    /// Brings the requested line to the top of the view. A jump can be asked for before the list has
    /// been laid out - the Step 2 preview only appears once the change is worked out, for instance -
    /// so the request is held until the list is on screen and ready, instead of being lost.
    /// </summary>
    private void ApplyPendingScroll()
    {
        if (_pendingScroll is null || !IsLoaded || !IsVisible)
        {
            return;
        }

        ScrollRequest request = _pendingScroll;

        // Wait for layout so the virtualised list knows how many lines it holds.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_pendingScroll, request))
            {
                return; // a newer request superseded this one
            }

            AttachScrollViewer();
            if (_scrollViewer is null || Lines is not { Count: > 0 } lines)
            {
                return;
            }

            int target = Math.Clamp(request.LineIndex, 0, lines.Count - 1);

            // With item scrolling, the vertical offset is the index of the first visible line.
            _scrollViewer.ScrollToVerticalOffset(target);
            _pendingScroll = null;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AttachScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            return;
        }

        _scrollViewer = FindChild<ScrollViewer>(LineList);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += (_, _) => ReportViewport();
            ReportViewport();
            ApplyPendingScroll();
        }
    }

    /// <summary>Tells the view model which lines are on screen, so "next change" can mean "next one below here".</summary>
    private void ReportViewport()
    {
        if (_scrollViewer is null || Lines is not { Count: > 0 } lines)
        {
            TopVisibleLine = -1;
            BottomVisibleLine = -1;
            return;
        }

        int top = (int)Math.Floor(_scrollViewer.VerticalOffset);
        int visible = Math.Max(1, (int)Math.Floor(_scrollViewer.ViewportHeight));
        TopVisibleLine = Math.Clamp(top, 0, lines.Count - 1);
        BottomVisibleLine = Math.Clamp(top + visible - 1, 0, lines.Count - 1);
    }

    private void MeasureRowWidth()
    {
        int longest = 0;
        if (Lines is { } lines)
        {
            foreach (DiffLine line in lines)
            {
                longest = Math.Max(longest, line.DisplayText.Length);
            }
        }

        var typeface = new Typeface((FontFamily)FindResource("MonoFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double charWidth = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12.5, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

        // Gutter (marker, line number, prefix) + text + right margin.
        SetValue(RowWidthPropertyKey, 4 + 46 + 16 + (longest * charWidth) + 24);
    }

    // ------------------------------------------------------------------ copying

    private void CopySelected()
    {
        if (LineList.SelectedItems.Count == 0)
        {
            return;
        }

        // In the order the lines appear, not the order they were clicked.
        var selected = LineList.SelectedItems.Cast<DiffLine>()
            .Select(line => (Line: line, Index: Lines?.IndexOf(line) ?? -1))
            .OrderBy(pair => pair.Index)
            .Select(pair => pair.Line.Text);

        SetClipboard(string.Join(Environment.NewLine, selected));
    }

    private void OnCopySelected(object sender, RoutedEventArgs e) => CopySelected();

    private void OnSelectAll(object sender, RoutedEventArgs e) => LineList.SelectAll();

    private void OnCopyAfter(object sender, RoutedEventArgs e) => SetClipboard(AfterText);

    private void OnCopyBefore(object sender, RoutedEventArgs e) => SetClipboard(BeforeText);

    private static void SetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is briefly locked by another application; trying again works.
        }
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

internal static class ListIndexExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
