using System.Windows;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class DiffWindow : Window
{
    public DiffWindow(DiffReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
