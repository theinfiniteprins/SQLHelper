using System.Windows;
using System.Windows.Threading;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class PatchEditorWindow : Window
{
    private readonly PatchViewModel _viewModel;

    // Re-diffing a long procedure on every keystroke would make typing lag; wait for a pause.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public PatchEditorWindow(PatchViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            _viewModel.RefreshEditorDiff();
        };

        Loaded += (_, _) => _viewModel.LoadEditorDiff();
        Closed += (_, _) => _refresh.Stop();
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _refresh.Stop();
        _refresh.Start();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
