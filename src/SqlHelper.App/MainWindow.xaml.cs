using System.Windows;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
