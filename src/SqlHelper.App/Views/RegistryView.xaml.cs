using System.ComponentModel;
using System.Windows.Controls;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class RegistryView : UserControl
{
    private RegistryViewModel? _viewModel;
    private bool _suppressPasswordSync;

    public RegistryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as RegistryViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ClearPasswordBox();
    }

    /// <summary>A PasswordBox can't be data-bound, so clear it whenever a different database is selected.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegistryViewModel.SelectedRow))
        {
            ClearPasswordBox();
        }
    }

    private void ClearPasswordBox()
    {
        if (PasswordEntry is null)
        {
            return;
        }

        _suppressPasswordSync = true;
        PasswordEntry.Clear();
        _suppressPasswordSync = false;
    }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressPasswordSync || _viewModel?.SelectedRow is not { } row || sender is not PasswordBox box)
        {
            return;
        }

        // Empty means "leave whatever is already stored alone".
        row.PendingPassword = box.Password.Length == 0 ? null : box.Password;
    }
}
