using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Export;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Model;

namespace SqlHelper.App.ViewModels;

public partial class SelectQueryViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;
    private CancellationTokenSource? _cts;

    public TargetPickerViewModel Picker { get; }

    public ObservableCollection<ClientResultTab> Results { get; } = [];

    [ObservableProperty]
    private string sqlText = "SELECT TOP (100) *\nFROM ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool isRunning;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string? guardWarning;

    [ObservableProperty]
    private ClientResultTab? selectedTab;

    public bool CanCancel => IsRunning;

    public SelectQueryViewModel(AppSession session)
    {
        _session = session;
        // Read-only screen: default to every database, since that's almost always the intent.
        Picker = new TargetPickerViewModel(session, selectAllByDefault: true);
        EvaluateGuard(SqlText);
    }

    partial void OnSqlTextChanged(string value) => EvaluateGuard(value);

    private void EvaluateGuard(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.TrimEnd().EndsWith("FROM", StringComparison.OrdinalIgnoreCase))
        {
            // Don't nag while the query is obviously still being typed.
            GuardWarning = null;
            return;
        }

        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(sql);
        GuardWarning = verdict.IsReadOnly ? null : verdict.Summary;
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

        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(SqlText);
        if (!verdict.IsReadOnly)
        {
            GuardWarning = verdict.Summary;
            StatusMessage = "This screen only runs read-only SELECT statements. Use Update / Delete for anything that writes.";
            return;
        }

        Results.Clear();
        NotifyExportState();
        IsRunning = true;
        StatusMessage = $"Running on {targets.Count} database(s)…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var progress = new Progress<TargetRun<QueryResult>>(run =>
        {
            Results.Add(ToTab(run));
            SelectedTab ??= Results[0];
        });

        try
        {
            IReadOnlyList<TargetRun<QueryResult>> runs = await _session.Engine.RunSelectAsync(
                targets, SqlText, QueryExecutionOptions.Default, progress, _cts.Token);

            int ok = runs.Count(r => r.IsSuccess);
            int rows = runs.Where(r => r.IsSuccess).Sum(r => r.Result?.TotalRows ?? 0);
            StatusMessage = ok == runs.Count
                ? $"Done — {rows:N0} row(s) from {ok} database(s)."
                : $"{ok} of {runs.Count} succeeded — {runs.Count - ok} failed (see the red tabs).";
            SelectedTab ??= Results.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
            NotifyExportState();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportCombined() => Export(combined: true);

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportPerClient() => Export(combined: false);

    private bool HasResults() => Results.Any(r => r.Success);

    private void NotifyExportState()
    {
        ExportCombinedCommand.NotifyCanExecuteChanged();
        ExportPerClientCommand.NotifyCanExecuteChanged();
    }

    private void Export(bool combined)
    {
        string? path = DialogService.SaveFile("query-results.xlsx");
        if (path is null)
        {
            return;
        }

        try
        {
            var rows = Results
                .Where(r => r is { Success: true, PrimaryTable: not null })
                .Select(r => (r.ClientName, QueryResultPresentation.ToQueryTable(r.PrimaryTable!)))
                .ToList();

            if (rows.Count == 0)
            {
                StatusMessage = "Nothing to export — no successful results.";
                return;
            }

            if (combined)
            {
                ExcelExporter.ExportCombined(path, rows);
            }
            else
            {
                ExcelExporter.ExportPerClientSheets(path, rows);
            }

            StatusMessage = $"Exported to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Couldn't write the file: {ex.Message}";
        }
    }

    private static ClientResultTab ToTab(TargetRun<QueryResult> run)
    {
        if (!run.IsSuccess || run.Result is null)
        {
            return new ClientResultTab { ClientName = run.Target.ClientName, Success = false, Error = run.Error };
        }

        QueryTable? first = run.Result.Tables.FirstOrDefault();
        return new ClientResultTab
        {
            ClientName = run.Target.ClientName,
            Success = true,
            PrimaryTable = first is null ? null : QueryResultPresentation.ToDataTable(first).DefaultView,
            RowCount = first?.Rows.Count ?? 0,
            Truncated = first?.Truncated ?? false,
            Messages = run.Result.Messages,
        };
    }

    public void Dispose()
    {
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
