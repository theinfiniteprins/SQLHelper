using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlHelper.App.ViewModels;

/// <summary>
/// Everything the diff panel needs: the rendered lines, a running count of what changed, a
/// whitespace toggle, and a way to step from one change to the next without hunting through a
/// long procedure by eye. Shared by the inline panel and the full-screen window so both behave
/// identically and stay in step.
/// </summary>
public partial class DiffReviewViewModel : ObservableObject
{
    private string _before = string.Empty;
    private string _after = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> lines = [];

    [ObservableProperty]
    private DiffStats stats = DiffStats.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int currentChange = -1;

    [ObservableProperty]
    private int scrollToIndex = -1;

    [ObservableProperty]
    private string title = "Change";

    /// <summary>When on, lines that differ only in spacing or indentation are not counted as changes.</summary>
    [ObservableProperty]
    private bool ignoreWhitespace;

    public string PositionText => Stats.ChangeBlocks == 0
        ? "No changes"
        : CurrentChange < 0
            ? $"{Stats.ChangeBlocks} change(s)"
            : $"Change {CurrentChange + 1} of {Stats.ChangeBlocks}";

    public bool HasChanges => Stats.ChangeBlocks > 0;

    public void SetContent(string? before, string? after, string title = "Change")
    {
        _before = before ?? string.Empty;
        _after = after ?? string.Empty;
        Title = title;
        Rebuild(resetPosition: true);
    }

    partial void OnIgnoreWhitespaceChanged(bool value) => Rebuild(resetPosition: true);

    private void Rebuild(bool resetPosition)
    {
        Lines = DiffRenderer.Build(_before, _after, IgnoreWhitespace);
        Stats = DiffRenderer.Summarise(Lines);
        OnPropertyChanged(nameof(HasChanges));

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

        NextChangeCommand.NotifyCanExecuteChanged();
        PreviousChangeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PositionText));
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void NextChange()
    {
        if (Stats.ChangeBlocks == 0)
        {
            return;
        }

        GoToChange(CurrentChange + 1 >= Stats.ChangeBlocks ? 0 : CurrentChange + 1);
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void PreviousChange()
    {
        if (Stats.ChangeBlocks == 0)
        {
            return;
        }

        GoToChange(CurrentChange - 1 < 0 ? Stats.ChangeBlocks - 1 : CurrentChange - 1);
    }

    private void GoToChange(int index)
    {
        CurrentChange = index;

        int lineIndex = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].ChangeIndex == index)
            {
                lineIndex = i;
                break;
            }
        }

        if (lineIndex < 0)
        {
            return;
        }

        // Show a little of the code above the change so it has context on screen.
        ScrollToIndex = Math.Max(0, lineIndex - 3);
    }
}
