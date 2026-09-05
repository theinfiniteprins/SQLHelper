using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SqlHelper.App.Services;
using SqlHelper.App.ViewModels;
using SqlHelper.App.Views;
using SqlHelper.Core;

namespace SqlHelper.App;

public partial class App : Application
{
    private AppPaths _paths = AppPaths.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _paths = AppPaths.Default;
        _paths.EnsureCreated();

        // Never let the window just vanish: write the fault down and say so.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var unlockViewModel = new UnlockViewModel(_paths);
        var unlockWindow = new UnlockWindow(unlockViewModel);

        if (unlockWindow.ShowDialog() != true || unlockViewModel.Result is null)
        {
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow(new MainWindowViewModel(unlockViewModel.Result));
        MainWindow = mainWindow;
        mainWindow.Closed += (_, _) => Shutdown();
        mainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string path = WriteCrashLog(e.Exception, "UI thread");

        MessageBox.Show(
            "Something went wrong on this screen, but SqlHelper is still running — nothing was left half-done.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"Full details were written to:\n{path}",
            "SqlHelper hit a problem",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Keep the app alive: a broken screen shouldn't cost the operator their session.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog(ex, "background thread");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "unobserved task");
        e.SetObserved();
    }

    private string WriteCrashLog(Exception exception, string source)
    {
        try
        {
            string directory = Path.Combine(_paths.Root, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

            var text = new StringBuilder();
            text.AppendLine($"SqlHelper {AppInfo.Version} — unhandled exception on the {source}");
            text.AppendLine($"When     : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            text.AppendLine($"Operator : {AppInfo.Operator} on {AppInfo.Machine}");
            text.AppendLine();
            text.AppendLine(exception.ToString());

            File.WriteAllText(path, text.ToString());
            return path;
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            return "(the crash log itself could not be written)";
        }
    }
}
