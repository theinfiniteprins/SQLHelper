using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Model;

namespace SqlHelper.App.ViewModels;

public partial class NonQueryViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;
    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    public TargetPickerViewModel Picker { get; }

    public ObservableCollection<NonQueryResultRow> Results { get; } = [];

    [ObservableProperty]
    private string sqlText = string.Empty;

    [ObservableProperty]
    private string changeTitle = string.Empty;

    [ObservableProperty]
    private string? ticket;

    [ObservableProperty]
    private bool isDryRun = true;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public int TotalRowsAffected => Results.Sum(r => r.RowsAffected);

    public NonQueryViewModel(AppSession session)
    {
        _session = session;
        Picker = new TargetPickerViewModel(session);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        IReadOnlyList<DatabaseTarget> targets = Picker.SelectedTargets;
        if (targets.Count == 0)
        {
            StatusMessage = _session.Registry.Targets.Count == 0
                ? "No databases are registered yet — add one on the Databases screen first."
                : "Tick at least one database on the right.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SqlText))
        {
            StatusMessage = "Enter the script you want to run.";
            return;
        }

        // A title is helpful in the audit log but never blocks a run — if it's blank we record
        // the run under a clear placeholder rather than refusing to do the work.
        string changeTitle = string.IsNullOrWhiteSpace(ChangeTitle) ? "(untitled change)" : ChangeTitle.Trim();

        bool touchesProduction = targets.Any(t => t.Environment == ServerEnvironment.Production);
        if (!IsDryRun && touchesProduction)
        {
            bool confirmed = DialogService.ConfirmProductionAction(
                $"This will run against {targets.Count(t => t.Environment == ServerEnvironment.Production)} production database(s), for real (not a dry run).");
            if (!confirmed)
            {
                return;
            }
        }

        Results.Clear();
        IsRunning = true;
        StatusMessage = IsDryRun ? "Dry run in progress…" : "Running…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var options = NonQueryExecutionOptions.Default;
        if (IsDryRun)
        {
            options = options.AsDryRun();
        }

        var progress = new Progress<TargetRun<NonQueryResult>>(run => Results.Add(ToRow(run)));

        try
        {
            IReadOnlyList<TargetRun<NonQueryResult>> runs = await _session.Engine.RunNonQueryAsync(
                targets, SqlText, options, changeTitle, Ticket, progress, _cts.Token);

            int ok = runs.Count(r => r.IsSuccess);
            StatusMessage = $"{ok}/{runs.Count} succeeded. {TotalRowsAffected} row(s) affected in total.";
            OnPropertyChanged(nameof(TotalRowsAffected));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task RunFailedOnlyAsync()
    {
        IReadOnlyList<string> failedClients = [.. Results.Where(r => !r.Success).Select(r => r.ClientName)];
        if (failedClients.Count == 0)
        {
            return;
        }

        // Re-select just the failures, then re-run — RunAsync reads the picker's current selection.
        foreach (TargetCheckItem item in Picker.Items)
        {
            item.IsSelected = failedClients.Contains(item.Target.ClientName);
        }

        await RunAsync();
    }

    private static NonQueryResultRow ToRow(TargetRun<NonQueryResult> run) => new()
    {
        ClientName = run.Target.ClientName,
        Success = run.IsSuccess,
        RowsAffected = run.Result?.TotalRowsAffected ?? 0,
        WasRolledBack = run.Result?.WasRolledBack ?? false,
        Error = run.Error,
    };
}
