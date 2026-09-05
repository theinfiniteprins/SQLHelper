using System.Windows;
using System.Windows.Input;

namespace SqlHelper.App.Views;

public partial class PasswordPromptDialog : Window
{
    public string? EnteredPassword { get; private set; }

    public PasswordPromptDialog(string clientName)
    {
        InitializeComponent();
        PromptText.Text = $"New password for '{clientName}'. Stored encrypted, never leaves this machine.";
        Loaded += (_, _) => Box.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        EnteredPassword = Box.Password;
        DialogResult = true;
    }
}
