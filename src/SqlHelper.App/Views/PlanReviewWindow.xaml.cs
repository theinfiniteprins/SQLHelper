using System.Windows;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class PlanReviewWindow : Window
{
    public PlanReviewWindow(PatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
