using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlHelper.App.ViewModels;

/// <summary>
/// A request to bring a line to the top of the view. A fresh object every time, so asking for the
/// same line twice still scrolls — the viewer reacts to the request changing, not to the number.
/// Deliberately a class and not a record: two records for the same line would compare equal, the
/// property would see "no change", and the second press of a button would do nothing.
/// </summary>
public sealed class ScrollRequest(int lineIndex)
{
    public int LineIndex { get; } = lineIndex;
}

/// <summary>
/// Everything the diff panel needs: the rendered lines, a running count of what changed, a
/// whitespace toggle, and a way to step from one change to the next without hunting through a
/// long procedure by eye. Shared by the inline panel and the full-screen window so both behave
/// identically and stay in step.
/// </summary>
public partial class DiffReviewViewModel : ObservableObject
{
    /// <summary>Lines of code kept visible above a change when jumping to it.</summary>
    public const int ContextAbove = 3;

    private string _before = string.Empty;
    private string _after = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> lines = [];

    [ObservableProperty]
    private DiffStats stats = DiffStats.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int currentChange = -1;

    /// <summary>The line the last jump aimed for; kept for callers that only need the number.</summary>
    [ObservableProperty]
    private int scrollToIndex = -1;

    [ObservableProperty]
    private ScrollRequest? scrollRequest;

    /// <summary>First and last line currently on screen, reported by the viewer; -1 when unknown.</summary>
    [ObservableProperty]
    private int topVisibleLine = -1;

    [ObservableProperty]
    private int bottomVisibleLine = -1;

    [ObservableProperty]
    private string title = "Change";

    /// <summary>When on, lines that differ only in formatting are not counted as changes.</summary>
    [ObservableProperty]
    private bool ignoreWhitespace;

    public string PositionText => Stats.ChangeBlocks == 0
        ? "No changes"
        : CurrentChange < 0
            ? $"{Stats.ChangeBlocks} change(s)"
            : $"Change {CurrentChange + 1} of {Stats.ChangeBlocks}";

    public bool HasChanges => Stats.ChangeBlocks > 0;

    public string BeforeText => _before;

    public string AfterText => _after;

    /// <summary>Loads new content and lands on its first change.</summary>
    public void SetContent(string? before, string? after, string title = "Change")
    {
        _before = before ?? string.Empty;
        _after = after ?? string.Empty;
        Title = title;
        Rebuild(resetPosition: true);
    }

    /// <summary>
    /// Re-renders after the text was edited, without jumping: while someone is typing, the view
    /// must stay where they are looking.
    /// </summary>
    public void Refresh(string? before, string? after)
    {
        _before = before ?? string.Empty;
        _after = after ?? string.Empty;
        Rebuild(resetPosition: false);
    }

    partial void OnIgnoreWhitespaceChanged(bool value) => Rebuild(resetPosition: true);

    private void Rebuild(bool resetPosition)
    {
        Lines = DiffRenderer.Build(_before, _after, IgnoreWhitespace);
        Stats = DiffRenderer.Summarise(Lines);
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(BeforeText));
        OnPropertyChanged(nameof(AfterText));

        if (resetPosition)
        {
            CurrentChange = -1;

            // Land on the first change straight away — that is what the reader came to see.
            if (Stats.ChangeBlocks > 0)
            {
                GoToChange(0);
            }
            else
            {
                ScrollToIndex = -1;
            }
        }
        else if (CurrentChange >= Stats.ChangeBlocks)
        {
            CurrentChange = Stats.ChangeBlocks - 1;
        }

        NextChangeCommand.NotifyCanExecuteChanged();
        PreviousChangeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PositionText));
    }

    /// <summary>
    /// The next change below what is on screen. If the current change is still in view, simply the
    /// one after it — so repeated presses walk through every change in turn. After scrolling
    /// somewhere by hand, it is the first change from there downwards. From the last change it
    /// wraps to the first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void NextChange()
    {
        if (Stats.ChangeBlocks == 0)
        {
            return;
        }

        int target;
        if (!KnowsViewport || IsInView(CurrentChange))
        {
            target = CurrentChange + 1 >= Stats.ChangeBlocks ? 0 : CurrentChange + 1;
        }
        else
        {
            target = FirstChangeAtOrBelow(TopVisibleLine);
        }

        GoToChange(target);
    }

    /// <summary>The mirror of <see cref="NextChange"/>: the change above what is on screen, wrapping to the last.</summary>
    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void PreviousChange()
    {
        if (Stats.ChangeBlocks == 0)
        {
            return;
        }

        int target;
        if (!KnowsViewport || IsInView(CurrentChange))
        {
            target = CurrentChange - 1 < 0 ? Stats.ChangeBlocks - 1 : CurrentChange - 1;
        }
        else
        {
            target = LastChangeAbove(TopVisibleLine);
        }

        GoToChange(target);
    }

    private bool KnowsViewport => TopVisibleLine >= 0 && BottomVisibleLine >= TopVisibleLine;

    private bool IsInView(int change)
    {
        int start = StartLineOf(change);
        return start >= 0 && start >= TopVisibleLine && start <= BottomVisibleLine;
    }

    private int FirstChangeAtOrBelow(int line)
    {
        for (int change = 0; change < Stats.ChangeBlocks; change++)
        {
            if (StartLineOf(change) >= line)
            {
                return change;
            }
        }

        return 0;
    }

    private int LastChangeAbove(int line)
    {
        for (int change = Stats.ChangeBlocks - 1; change >= 0; change--)
        {
            if (StartLineOf(change) < line)
            {
                return change;
            }
        }

        return Stats.ChangeBlocks - 1;
    }

    private int StartLineOf(int change)
    {
        if (change < 0)
        {
            return -1;
        }

        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].ChangeIndex == change)
            {
                return i;
            }
        }

        return -1;
    }

    private void GoToChange(int index)
    {
        CurrentChange = index;

        int lineIndex = StartLineOf(index);
        if (lineIndex < 0)
        {
            return;
        }

        // Show a little of the code above the change so it has context on screen.
        ScrollToIndex = Math.Max(0, lineIndex - ContextAbove);
        ScrollRequest = new ScrollRequest(ScrollToIndex);
    }
}
