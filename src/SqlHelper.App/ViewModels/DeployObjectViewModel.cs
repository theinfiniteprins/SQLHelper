using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Scripting;

namespace SqlHelper.App.ViewModels;

public partial class DeployObjectViewModel : ObservableObject
{
    private readonly AppSession _session;

    public TargetPickerViewModel Picker { get; }

    public ObservableCollection<DeployResultRow> Results { get; } = [];

    [ObservableProperty]
    private string changeTitle = string.Empty;

    [ObservableProperty]
    private string? ticket;

    [ObservableProperty]
    private string definitionScript = string.Empty;

    [ObservableProperty]
    private string backupRoot;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string? lastBackupFolder;

    public DeployObjectViewModel(AppSession session)
    {
        _session = session;
        Picker = new TargetPickerViewModel(session);
        backupRoot = session.Paths.DefaultBackupRoot;
    }

    [RelayCommand]
    private void BrowseBackupRoot()
    {
        string? picked = DialogService.PickFolder("Where should backups be written?");
        if (picked is not null)
        {
            BackupRoot = picked;
        }
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        IReadOnlyList<DatabaseTarget> targets = Picker.SelectedTargets;
        if (targets.Count == 0)
        {
            StatusMessage = "Tick at least one database on the right.";
            return;
        }

        if (string.IsNullOrWhiteSpace(DefinitionScript))
        {
            StatusMessage = "Paste the definition you want to deploy.";
            return;
        }

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(DefinitionScript);
        if (!extraction.Ok)
        {
            StatusMessage = extraction.Error ?? "The script isn't a single CREATE/ALTER of one object.";
            return;
        }

        bool touchesProduction = targets.Any(t => t.Environment == ServerEnvironment.Production);
        if (touchesProduction)
        {
            bool confirmed = DialogService.ConfirmProductionAction(
                $"Deploy '{extraction.Name}' to {targets.Count(t => t.Environment == ServerEnvironment.Production)} production database(s). " +
                "Each target's current definition is backed up first, with a rollback script.");
            if (!confirmed)
            {
                return;
            }
        }

        Results.Clear();
        IsRunning = true;
        StatusMessage = "Deploying…";

        var progress = new Progress<Core.Execution.TargetRun<DeployTargetResult>>(run =>
        {
            if (run.Result is not null)
            {
                Results.Add(DeployResultRow.From(run.Result));
            }
        });

        try
        {
            // The description is optional; backups are named for the object and the time, so a
            // blank title never makes a backup hard to find.
            string description = string.IsNullOrWhiteSpace(ChangeTitle) ? "(no description)" : ChangeTitle.Trim();
            DeployResult result = await _session.Engine.DeployObjectAsync(
                targets, extraction.Name!, DefinitionScript, BackupRoot, description, Ticket, progress);

            LastBackupFolder = result.BackupFolder;
            OpenBackupFolderCommand.NotifyCanExecuteChanged();
            StatusMessage = result.FailedCount == 0
                ? $"Deployed to all {result.SucceededCount} database(s). Backups and rollback scripts are in {result.BackupFolder}"
                : $"{result.SucceededCount} succeeded, {result.FailedCount} failed. Backups: {result.BackupFolder}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            StatusMessage = $"Deployment could not start: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasBackupFolder))]
    private void OpenBackupFolder()
    {
        if (LastBackupFolder is null)
        {
            return;
        }

        // Open the folder, and only a folder. Handing an arbitrary path to the shell would run it
        // if it happened to be an executable, so check what it is first and pass it to Explorer as
        // an argument rather than as the thing to launch.
        if (!Directory.Exists(LastBackupFolder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LastBackupFolder}\"") { UseShellExecute = false });
    }

    private bool HasBackupFolder() => LastBackupFolder is not null;
}
