using System.IO;
using SqlHelper.App.Services;
using SqlHelper.App.ViewModels;
using SqlHelper.Core;
using SqlHelper.Core.Auditing;
using SqlHelper.Core.Credentials;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.Tests;

/// <summary>
/// Covers the path that used to silently drop everything the operator typed: fill the form in,
/// press Save, and the database must actually land in the registry, on disk, and in the picker
/// every other screen uses.
/// </summary>
public sealed class RegistryViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-app-tests", Guid.NewGuid().ToString("N"));
    private readonly AppSession _session;
    private int _changeNotifications;

    public RegistryViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();

        var protector = new DpapiSecretProtector();
        var store = new RegistryStore(paths.RegistryFile, protector);
        ConnectionRegistry registry = store.Load();
        var audit = new AuditLog(paths.AuditDirectory);
        var connections = new RegistryConnectionFactory(registry);

        _session = new AppSession
        {
            Paths = paths,
            Protector = protector,
            Store = store,
            Registry = registry,
            Audit = audit,
            Connections = connections,
            Engine = new DeploymentEngine(connections, audit),
        };
    }

    public void Dispose()
    {
        _session.Audit.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RegistryViewModel NewViewModel() => new(_session, () => _changeNotifications++);

    private static void FillIn(RegistryRowViewModel row, string client = "Acme", AuthMode auth = AuthMode.WindowsIntegrated)
    {
        row.ClientName = client;
        row.ServerName = "localhost";
        row.DatabaseName = "AcmeDb";
        row.AuthMode = auth;
        if (auth == AuthMode.SqlLogin)
        {
            row.UserId = "app_user";
            row.PendingPassword = "p@ssw0rd";
        }
    }

    [Fact]
    public void Adding_and_saving_a_database_persists_it_and_notifies_the_other_screens()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Single(_session.Registry.Targets);
        Assert.Equal("Acme", _session.Registry.Targets[0].ClientName);
        Assert.True(_changeNotifications > 0, "The other screens were never told the registry changed.");
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void A_saved_database_survives_a_reload_from_disk()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Globex");
        vm.SaveCommand.Execute(null);

        // Reload exactly as a fresh app launch would.
        ConnectionRegistry reloaded = new RegistryStore(_session.Paths.RegistryFile, _session.Protector).Load();

        DatabaseTarget target = Assert.Single(reloaded.Targets);
        Assert.Equal("Globex", target.ClientName);
        Assert.Equal("localhost", target.ServerName);
        Assert.Equal("AcmeDb", target.DatabaseName);
    }

    [Fact]
    public void A_saved_database_shows_up_in_the_target_picker_used_by_the_other_screens()
    {
        var picker = new TargetPickerViewModel(_session);
        Assert.Empty(picker.Items);

        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Initech");
        vm.SaveCommand.Execute(null);

        picker.Refresh();

        Assert.Single(picker.Items);
        Assert.Equal("Initech", picker.Items[0].Target.ClientName);
    }

    [Fact]
    public void A_sql_login_password_typed_in_the_form_is_stored_encrypted_and_readable_back()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Acme", AuthMode.SqlLogin);
        Guid id = vm.SelectedRow!.Id;

        vm.SaveCommand.Execute(null);

        Assert.True(_session.Registry.HasPassword(id));
        Assert.Equal("p@ssw0rd", _session.Registry.ResolvePassword(id));
    }

    [Fact]
    public void Choosing_sql_login_immediately_enables_the_password_fields()
    {
        // The old grid kept AuthMode stale until the row committed, so "Set password" wrongly
        // insisted the target was still Windows-authenticated.
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        RegistryRowViewModel row = vm.SelectedRow!;

        Assert.False(row.IsSqlLogin);
        row.AuthMode = AuthMode.SqlLogin;

        Assert.True(row.IsSqlLogin);
    }

    [Fact]
    public void Save_refuses_incomplete_rows_and_says_exactly_what_is_missing()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        vm.SelectedRow!.ClientName = "Acme";
        // server and database deliberately left blank

        vm.SaveCommand.Execute(null);

        Assert.True(vm.StatusIsError);
        Assert.Contains("Server is required", vm.StatusMessage);
        Assert.Empty(_session.Registry.Targets);
    }

    [Fact]
    public void Save_refuses_a_sql_login_with_no_password()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        RegistryRowViewModel row = vm.SelectedRow!;
        row.ClientName = "Acme";
        row.ServerName = "localhost";
        row.DatabaseName = "AcmeDb";
        row.AuthMode = AuthMode.SqlLogin;
        row.UserId = "app_user";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.StatusIsError);
        Assert.Contains("password", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_refuses_duplicate_client_names()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Acme");
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "acme");

        vm.SaveCommand.Execute(null);

        Assert.True(vm.StatusIsError);
        Assert.Contains("unique", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editing_a_saved_database_updates_it_rather_than_adding_a_second_one()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Acme");
        vm.SaveCommand.Execute(null);

        vm.Rows[0].DatabaseName = "AcmeDb_v2";
        vm.SaveCommand.Execute(null);

        DatabaseTarget target = Assert.Single(_session.Registry.Targets);
        Assert.Equal("AcmeDb_v2", target.DatabaseName);
    }

    [Fact]
    public void An_existing_password_is_kept_when_the_password_box_is_left_blank()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Acme", AuthMode.SqlLogin);
        Guid id = vm.SelectedRow!.Id;
        vm.SaveCommand.Execute(null);

        // Edit something else and save again without re-entering the password.
        vm.Rows[0].Notes = "moved to new host";
        Assert.Null(vm.Rows[0].PendingPassword);
        vm.SaveCommand.Execute(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Equal("p@ssw0rd", _session.Registry.ResolvePassword(id));
    }

    [Fact]
    public void Editing_any_field_marks_the_registry_dirty()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!);
        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty);

        vm.Rows[0].ServerName = "sql-02";

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Disabled_databases_are_hidden_from_the_picker()
    {
        RegistryViewModel vm = NewViewModel();
        vm.AddRowCommand.Execute(null);
        FillIn(vm.SelectedRow!, "Acme");
        vm.SelectedRow!.Enabled = false;
        vm.SaveCommand.Execute(null);

        var picker = new TargetPickerViewModel(_session);

        Assert.Empty(picker.Items);
    }
}
