using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Tests;

public sealed class DiffReviewViewModelTests
{
    private const string Before = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj";
    private const string After = "a\nB\nc\nd\ne\nf\ng\nh\nI\nj";

    private static DiffReviewViewModel Loaded(string before = Before, string after = After)
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent(before, after, "Test");
        return vm;
    }

    [Fact]
    public void It_lands_on_the_first_change_so_the_reader_does_not_have_to_hunt_for_it()
    {
        DiffReviewViewModel vm = Loaded();

        Assert.Equal(2, vm.Stats.ChangeBlocks);
        Assert.Equal(0, vm.CurrentChange);
        Assert.Equal("Change 1 of 2", vm.PositionText);
    }

    [Fact]
    public void Next_and_previous_step_through_every_change()
    {
        DiffReviewViewModel vm = Loaded();

        vm.NextChangeCommand.Execute(null);
        Assert.Equal("Change 2 of 2", vm.PositionText);

        vm.PreviousChangeCommand.Execute(null);
        Assert.Equal("Change 1 of 2", vm.PositionText);
    }

    [Fact]
    public void Stepping_past_the_last_change_wraps_to_the_first()
    {
        DiffReviewViewModel vm = Loaded();

        vm.NextChangeCommand.Execute(null); // 2 of 2
        vm.NextChangeCommand.Execute(null); // wraps
        Assert.Equal(0, vm.CurrentChange);

        vm.PreviousChangeCommand.Execute(null); // wraps backwards
        Assert.Equal(1, vm.CurrentChange);
    }

    [Fact]
    public void Moving_to_a_change_scrolls_to_it_with_some_context_above()
    {
        DiffReviewViewModel vm = Loaded();

        vm.NextChangeCommand.Execute(null);
        int firstLineOfChange = vm.Lines
            .Select((line, index) => (line, index))
            .First(t => t.line.ChangeIndex == vm.CurrentChange).index;

        Assert.True(vm.ScrollToIndex <= firstLineOfChange, "scrolls to at or above the change");
        Assert.True(vm.ScrollToIndex >= 0, "never scrolls off the top");
    }

    [Fact]
    public void With_nothing_to_compare_the_navigation_is_disabled_rather_than_misleading()
    {
        DiffReviewViewModel vm = Loaded("same\ntext", "same\ntext");

        Assert.False(vm.HasChanges);
        Assert.Equal("No changes", vm.PositionText);
        Assert.False(vm.NextChangeCommand.CanExecute(null));
        Assert.False(vm.PreviousChangeCommand.CanExecute(null));
    }

    [Fact]
    public void Ignoring_whitespace_hides_an_indentation_only_change()
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent("SELECT 1\n    SELECT 2", "SELECT 1\n        SELECT 2");

        Assert.True(vm.HasChanges);

        vm.IgnoreWhitespace = true;

        Assert.False(vm.HasChanges);
        Assert.Equal("No changes", vm.PositionText);
    }

    [Fact]
    public void Toggling_whitespace_back_off_restores_the_change()
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent("SELECT 1\n    SELECT 2", "SELECT 1\n        SELECT 2");

        vm.IgnoreWhitespace = true;
        vm.IgnoreWhitespace = false;

        Assert.True(vm.HasChanges);
        Assert.Equal(0, vm.CurrentChange);
    }

    [Fact]
    public void Loading_new_content_starts_the_reader_at_the_top_of_the_new_change()
    {
        DiffReviewViewModel vm = Loaded();
        vm.NextChangeCommand.Execute(null);
        Assert.Equal(1, vm.CurrentChange);

        vm.SetContent("x\ny", "x\nY", "Another client");

        Assert.Equal("Another client", vm.Title);
        Assert.Equal(1, vm.Stats.ChangeBlocks);
        Assert.Equal(0, vm.CurrentChange);
    }
}
