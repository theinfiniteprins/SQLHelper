using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.ViewModels;

public partial class RegistryViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly Action _onChanged;

    public ObservableCollection<RegistryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RegistryRowViewModel? selectedRow;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool statusIsError;

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private bool isTesting;

    [ObservableProperty]
    private string? testResult;

    [ObservableProperty]
    private bool testFailed;

    public bool HasSelection => SelectedRow is not null;

    public RegistryViewModel(AppSession session, Action onChanged)
    {
        _session = session;
        _onChanged = onChanged;
        Rows.CollectionChanged += OnRowsChanged;
        LoadRows();
    }

    private void LoadRows()
    {
        foreach (RegistryRowViewModel existing in Rows)
        {
            existing.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.CollectionChanged -= OnRowsChanged;
        Rows.Clear();
        foreach (DatabaseTarget target in _session.Registry.Targets.OrderBy(t => t.ClientName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new RegistryRowViewModel(target, _session.Registry.HasPassword(target.Id));
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        Rows.CollectionChanged += OnRowsChanged;
        SelectedRow = Rows.FirstOrDefault();
        IsDirty = false;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (RegistryRowViewModel row in e.NewItems?.OfType<RegistryRowViewModel>() ?? [])
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        foreach (RegistryRowViewModel row in e.OldItems?.OfType<RegistryRowViewModel>() ?? [])
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        IsDirty = true;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore purely derived/display properties so typing in one field doesn't spam the flag.
        if (e.PropertyName is nameof(RegistryRowViewModel.DisplayTitle)
            or nameof(RegistryRowViewModel.DisplaySubtitle)
            or nameof(RegistryRowViewModel.PasswordStatus)
            or nameof(RegistryRowViewModel.ValidationMessage)
            or nameof(RegistryRowViewModel.IsValid)
            or nameof(RegistryRowViewModel.IsSqlLogin)
            or nameof(RegistryRowViewModel.IsProduction)
            or nameof(RegistryRowViewModel.EnvironmentLabel))
        {
            return;
        }

        IsDirty = true;
        TestResult = null;
    }

    partial void OnSelectedRowChanged(RegistryRowViewModel? value) => TestResult = null;

    [RelayCommand]
    private void AddRow()
    {
        var row = new RegistryRowViewModel();
        Rows.Add(row);
        SelectedRow = row;
        SetStatus("New database added. Fill in the details on the right, then Save.", isError: false);
    }

    [RelayCommand]
    private void DuplicateRow()
    {
        if (SelectedRow is null)
        {
            return;
        }

        RegistryRowViewModel copy = SelectedRow.CloneForNew();
        Rows.Add(copy);
        SelectedRow = copy;
        SetStatus("Copied. Change the client name and database, then Save.", isError: false);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (!row.IsNew && !DialogService.Confirm(
                $"Remove '{row.DisplayTitle}' from the registry?\n\nThis only removes it from this tool — the database itself is untouched.",
                "Remove database"))
        {
            return;
        }

        int index = Rows.IndexOf(row);
        Rows.Remove(row);

        if (!row.IsNew)
        {
            _session.Registry.Remove(row.Id);
            _session.Save();
            _onChanged();
        }

        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        SetStatus($"Removed '{row.DisplayTitle}'.", isError: false);
    }

    [RelayCommand]
    private void ImportFromClipboard()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("Couldn't read the clipboard — try copying the range again.", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Clipboard is empty. In Excel, copy rows with these columns: Client, Server, Database, Auth, User, Environment.", isError: true);
            return;
        }

        IReadOnlyList<ImportedTargetRow> imported = TargetImporter.ParseTabSeparated(text);
        if (imported.Count == 0)
        {
            SetStatus("Nothing on the clipboard looked like a row of database details.", isError: true);
            return;
        }

        RegistryRowViewModel? first = null;
        foreach (ImportedTargetRow imported1 in imported)
        {
            var row = new RegistryRowViewModel(imported1.ToTarget(), hasStoredPassword: false);
            Rows.Add(row);
            first ??= row;
        }

        SelectedRow = first;
        int needPassword = imported.Count(r => r.AuthMode == AuthMode.SqlLogin);
        SetStatus(
            needPassword == 0
                ? $"Imported {imported.Count} database(s). Review them, then Save."
                : $"Imported {imported.Count} database(s). {needPassword} use a SQL login and still need a password. Review, then Save.",
            isError: false);
    }

    [RelayCommand]
    private void Save()
    {
        var invalid = Rows.Where(r => !r.IsValid).ToList();
        if (invalid.Count > 0)
        {
            SelectedRow = invalid[0];
            SetStatus($"Can't save yet — '{invalid[0].DisplayTitle}': {invalid[0].ValidationMessage}", isError: true);
            return;
        }

        var duplicates = Rows
            .GroupBy(r => r.ClientName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            SetStatus($"Client names must be unique — '{duplicates[0]}' is used more than once.", isError: true);
            return;
        }

        foreach (RegistryRowViewModel row in Rows)
        {
            _session.Registry.Upsert(row.ToTarget(), row.PendingPassword ?? ReadOnlySpan<char>.Empty);
            if (row.PendingPassword is { Length: > 0 })
            {
                row.HasStoredPassword = true;
            }

            row.MarkSaved();
        }

        _session.Save();
        _onChanged();
        IsDirty = false;

        SetStatus($"Saved. {Rows.Count} database(s) registered and available on the other screens.", isError: false);
    }

    [RelayCommand]
    private void Revert()
    {
        if (IsDirty && !DialogService.Confirm("Discard unsaved changes and reload from disk?", "Discard changes"))
        {
            return;
        }

        LoadRows();
        SetStatus("Reloaded from the saved registry.", isError: false);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (row.ValidationMessage is { } problem)
        {
            TestFailed = true;
            TestResult = problem;
            return;
        }

        IsTesting = true;
        TestResult = null;
        try
        {
            string? password = row.PendingPassword;
            if (string.IsNullOrEmpty(password) && row.AuthMode == AuthMode.SqlLogin && row.HasStoredPassword && !row.IsNew)
            {
                password = _session.Registry.ResolvePassword(row.Id);
            }

            var executor = new SqlExecutor(new AdHocConnectionFactory(password));
            ConnectionProbe probe = await executor.TestConnectionAsync(row.ToTarget());

            if (probe.Ok)
            {
                TestFailed = false;
                string writable = probe.CanWrite ? "read/write" : "read-only";
                TestResult = $"Connected in {probe.Duration.TotalMilliseconds:F0} ms — SQL Server {probe.ServerVersion}, " +
                             $"database {probe.DatabaseName}, signed in as {probe.LoginName} ({writable}).";
            }
            else
            {
                TestFailed = true;
                TestResult = ConnectionErrorHelp.Explain(probe.Error);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            TestFailed = true;
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    // ------------------------------------------------------------------
    // Sharing the list with another PC
    // ------------------------------------------------------------------

    private const string PackageFilter = "SqlHelper database list (*.sqlhelper)|*.sqlhelper|All files (*.*)|*.*";

    [RelayCommand]
    private void ExportDatabases()
    {
        if (Rows.Count == 0)
        {
            SetStatus("There are no databases to export yet.", isError: true);
            return;
        }

        if (IsDirty)
        {
            SetStatus("Save your changes first — the export is written from the saved registry.", isError: true);
            return;
        }

        string? path = DialogService.SaveFile("databases.sqlhelper", PackageFilter);
        if (path is null)
        {
            return;
        }

        string? passphrase = DialogService.AskPassphrase(Views.PassphraseDialog.ForExport());
        if (passphrase is null)
        {
            return;
        }

        try
        {
            RegistryPackage.ExportEncrypted(_session.Registry, path, passphrase, AppInfo.Operator);
            int withPasswords = _session.Registry.Targets.Count(
                t => t.AuthMode == AuthMode.SqlLogin && _session.Registry.HasPassword(t.Id));

            SetStatus(
                $"Exported {Rows.Count} database(s) to {path}. The file is encrypted" +
                (withPasswords > 0 ? $" and includes {withPasswords} saved password(s)" : string.Empty) +
                " — send the passphrase separately, not with the file.",
                isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Could not write that file: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void ImportDatabases()
    {
        string? path = DialogService.OpenFile("Open a SqlHelper database list", PackageFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            ImportOutcome outcome;

            if (RegistryPackage.IsEncryptedPackage(path))
            {
                string? passphrase = DialogService.AskPassphrase(
                    Views.PassphraseDialog.ForImport(Path.GetFileName(path)));
                if (passphrase is null)
                {
                    return;
                }

                PortableBundle bundle = RegistryPackage.ImportEncrypted(path, passphrase);
                outcome = RegistryPackage.Import(_session.Registry, bundle, AskWhetherToOverwrite());
            }
            else
            {
                PortableRegistry plain = RegistryPortability.ReadFile(path);
                outcome = RegistryPortability.Import(_session.Registry, plain, AskWhetherToOverwrite());
            }

            _session.Save();
            LoadRows();
            _onChanged();

            var summary = new List<string>();
            if (outcome.Added > 0)
            {
                summary.Add($"{outcome.Added} added");
            }

            if (outcome.Updated > 0)
            {
                summary.Add($"{outcome.Updated} updated");
            }

            if (outcome.Skipped > 0)
            {
                summary.Add($"{outcome.Skipped} left alone");
            }

            string headline = summary.Count == 0 ? "Nothing to import from that file." : string.Join(", ", summary) + ".";
            string notes = outcome.Notes.Count == 0 ? string.Empty : " " + string.Join(" ", outcome.Notes.Take(3));
            SetStatus(headline + notes, isError: false);
        }
        catch (InvalidDataException ex)
        {
            LoadRows(); // the import may have got part-way; show what the registry actually holds
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadRows();
            SetStatus($"Could not read that file: {ex.Message}", isError: true);
        }
    }

    private bool AskWhetherToOverwrite() =>
        Rows.Count > 0
        && DialogService.Confirm(
            "Some of these may already be registered here. A database counts as the same "
            + "one when the client, server and database name all match.\n\n"
            + "Yes — replace those with the details from the file.\n"
            + "No — keep what is already on this PC and add only the new ones.",
            "Import databases");
}
