using System.Windows;
using System.Windows.Input;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.Views;

public partial class PassphraseDialog : Window
{
    private readonly bool _requireConfirmation;

    public string? Passphrase { get; private set; }

    private PassphraseDialog(string heading, string explanation, bool requireConfirmation)
    {
        InitializeComponent();
        _requireConfirmation = requireConfirmation;
        HeadingText.Text = heading;
        ExplanationText.Text = explanation;
        ConfirmPanel.Visibility = requireConfirmation ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Box.Focus();
    }

    /// <summary>Asks for a new passphrase, twice, before writing a file that carries passwords.</summary>
    public static PassphraseDialog ForExport() => new(
        "Choose a passphrase for this file",
        $"The file carries your saved passwords, so it is encrypted. Whoever imports it needs this passphrase — "
        + $"send it to them separately, not with the file. At least {RegistryPackage.MinimumPassphraseLength} characters. "
        + "There is no way to recover it if it is lost.",
        requireConfirmation: true);

    /// <summary>Asks for the passphrase of a file being opened.</summary>
    public static PassphraseDialog ForImport(string fileName) => new(
        "This file is encrypted",
        $"Enter the passphrase for {fileName}, as given to you by whoever exported it.",
        requireConfirmation: false);

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        ProblemText.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = _requireConfirmation
            ? Box.Password.Length >= RegistryPackage.MinimumPassphraseLength && ConfirmBox.Password.Length > 0
            : Box.Password.Length > 0;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OkButton.IsEnabled)
        {
            Accept();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        if (_requireConfirmation && Box.Password != ConfirmBox.Password)
        {
            ProblemText.Text = "The two passphrases don't match.";
            ProblemText.Visibility = Visibility.Visible;
            ConfirmBox.Clear();
            ConfirmBox.Focus();
            return;
        }

        Passphrase = Box.Password;
        DialogResult = true;
    }
}
