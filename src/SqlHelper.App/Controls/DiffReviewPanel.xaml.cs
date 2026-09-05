using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlHelper.App.ViewModels;
using SqlHelper.App.Views;

namespace SqlHelper.App.Controls;

public partial class DiffReviewPanel : UserControl
{
    public static readonly DependencyProperty ShowExpandButtonProperty = DependencyProperty.Register(
        nameof(ShowExpandButton), typeof(bool), typeof(DiffReviewPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(
        nameof(EmptyMessage), typeof(string), typeof(DiffReviewPanel),
        new PropertyMetadata("Nothing to compare yet."));

    /// <summary>Hidden inside the full-screen window itself, where there is nothing left to expand into.</summary>
    public bool ShowExpandButton
    {
        get => (bool)GetValue(ShowExpandButtonProperty);
        set => SetValue(ShowExpandButtonProperty, value);
    }

    public string EmptyMessage
    {
        get => (string)GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    public DiffReviewPanel()
    {
        InitializeComponent();

        // Alt+Up / Alt+Down step between changes without reaching for the mouse.
        InputBindings.Add(new KeyBinding(NavigateNext, Key.Down, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(NavigatePrevious, Key.Up, ModifierKeys.Alt));
    }

    private ICommand NavigateNext => new RelayCommandShim(() => (DataContext as DiffReviewViewModel)?.NextChangeCommand.Execute(null));

    private ICommand NavigatePrevious => new RelayCommandShim(() => (DataContext as DiffReviewViewModel)?.PreviousChangeCommand.Execute(null));

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffReviewViewModel viewModel)
        {
            return;
        }

        // The window shares this very view model, so whatever the reader does in there — stepping
        // through changes, toggling whitespace — is still there when they come back.
        var window = new DiffWindow(viewModel) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    /// <summary>Minimal ICommand for the two key bindings; the real commands live on the view model.</summary>
    private sealed class RelayCommandShim(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
