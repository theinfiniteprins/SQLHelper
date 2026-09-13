using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlHelper.App.Controls;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class PatchView : UserControl
{
    public PatchView()
    {
        // Before InitializeComponent: the XAML binds to these, and they raise no change notification,
        // so they must already exist when the bindings first read them.
        OpenEditorCommand = new DiffReviewPanel.RelayCommandShim(OpenEditor);
        OpenPlanReviewCommand = new DiffReviewPanel.RelayCommandShim(OpenPlanReview);
        InitializeComponent();
    }

    /// <summary>Step 2 in full screen: Before and After editable side by side, the comparison beneath.</summary>
    public ICommand OpenEditorCommand { get; }

    /// <summary>Step 3 in full screen: every client's outcome alongside what would change on it.</summary>
    public ICommand OpenPlanReviewCommand { get; }

    private void OpenEditor()
    {
        if (DataContext is PatchViewModel viewModel)
        {
            new PatchEditorWindow(viewModel) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }

    private void OpenPlanReview()
    {
        if (DataContext is PatchViewModel viewModel)
        {
            new PlanReviewWindow(viewModel) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }
}
