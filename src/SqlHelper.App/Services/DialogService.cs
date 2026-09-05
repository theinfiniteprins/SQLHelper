using System.Windows;
using Microsoft.Win32;

namespace SqlHelper.App.Services;

/// <summary>Thin wrappers around WPF/Win32 dialogs so view models don't reference UI types directly.</summary>
public static class DialogService
{
    public static void Info(string message, string title = "SqlHelper") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title = "SqlHelper") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(string message, string title = "SqlHelper") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string message, string title = "Confirm") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>A plain yes/no before anything is written to a production database.</summary>
    public static bool ConfirmProductionAction(string message) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            message + Environment.NewLine + Environment.NewLine + "Go ahead?",
            "Production change",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public static string? SaveFile(string suggestedName, string filter = "Excel Workbook (*.xlsx)|*.xlsx")
    {
        var dialog = new SaveFileDialog { FileName = suggestedName, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Asks for a passphrase; null when the operator backs out.</summary>
    public static string? AskPassphrase(Views.PassphraseDialog dialog)
    {
        dialog.Owner = Application.Current.MainWindow;
        return dialog.ShowDialog() == true ? dialog.Passphrase : null;
    }

    public static string? PickFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
