using System.Windows;
using System.Windows.Input;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class UnlockWindow : Window
{
    public UnlockViewModel ViewModel { get; }

    public UnlockWindow(UnlockViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;

        if (viewModel.IsFirstRun)
        {
            SubtitleText.Text = "First run on this machine — let's set up your local connection registry.";
            PromptText.Text = "Optional master passphrase (adds a second lock on top of Windows, blank is fine):";
            HintText.Text = "Nothing here goes anywhere but this PC. If you set a passphrase, you'll need it every time — there is no recovery.";
        }
        else
        {
            SubtitleText.Text = "Unlock your local connection registry.";
            PromptText.Text = "Master passphrase (leave blank if you didn't set one):";
            HintText.Text = string.Empty;
        }

        Loaded += (_, _) => PassphraseBox.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryContinue();
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e) => TryContinue();

    private void TryContinue()
    {
        if (ViewModel.TryUnlock(PassphraseBox.Password))
        {
            DialogResult = true;
            return;
        }

        ErrorText.Text = ViewModel.ErrorMessage;
        ErrorText.Visibility = Visibility.Visible;
        PassphraseBox.SelectAll();
        PassphraseBox.Focus();
    }
}
