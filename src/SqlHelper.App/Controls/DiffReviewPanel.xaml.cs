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

    /// <summary>
    /// What "full screen" opens. Left unset, a read-only comparison window. Step 2 sets it to its
    /// editor, and Step 3 to a window with the client list alongside the comparison.
    /// </summary>
    public static readonly DependencyProperty ExpandCommandProperty = DependencyProperty.Register(
        nameof(ExpandCommand), typeof(ICommand), typeof(DiffReviewPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty ExpandLabelProperty = DependencyProperty.Register(
        nameof(ExpandLabel), typeof(string), typeof(DiffReviewPanel), new PropertyMetadata("⛶  Full screen"));

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

    public ICommand? ExpandCommand
    {
        get => (ICommand?)GetValue(ExpandCommandProperty);
        set => SetValue(ExpandCommandProperty, value);
    }

    public string ExpandLabel
    {
        get => (string)GetValue(ExpandLabelProperty);
        set => SetValue(ExpandLabelProperty, value);
    }

    public DiffReviewPanel()
    {
        InitializeComponent();

        // Alt+Up / Alt+Down step between changes without reaching for the mouse.
        InputBindings.Add(new KeyBinding(new RelayCommandShim(() => (DataContext as DiffReviewViewModel)?.NextChangeCommand.Execute(null)), Key.Down, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(new RelayCommandShim(() => (DataContext as DiffReviewViewModel)?.PreviousChangeCommand.Execute(null)), Key.Up, ModifierKeys.Alt));
    }

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        if (ExpandCommand is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        if (DataContext is not DiffReviewViewModel viewModel)
        {
            return;
        }

        // The window shares this very view model, so whatever the reader does in there — stepping
        // through changes, toggling whitespace — is still there when they come back.
        var window = new DiffWindow(viewModel) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    /// <summary>Minimal ICommand for the key bindings and window-opening hooks.</summary>
    internal sealed class RelayCommandShim(Action execute) : ICommand
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
