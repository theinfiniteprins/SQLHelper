using SqlHelper.Core.Backup;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Backup;

public sealed class BackupWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlhelper-backup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DatabaseTarget Client(string name = "Acme") => new()
    {
        ClientName = name, ServerName = "sql-01", DatabaseName = "AcmeDb", Environment = ServerEnvironment.Production,
    };

    private static ProgrammableObject Module(string schema, string name, string definition, DateTime? modified = null) => new(
        new ObjectName(schema, name),
        ProgrammableObjectKind.StoredProcedure,
        definition,
        UsesAnsiNulls: true,
        UsesQuotedIdentifier: true,
        ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow,
        ModifiedServerTime: modified ?? DateTime.UtcNow,
        IsEncrypted: false);

    [Fact]
    public void BeginSession_creates_a_timestamped_folder_with_a_rollback_subfolder()
    {
        var writer = new BackupWriter(_root);
        BackupSession session = writer.BeginSession("Fix tax rounding", new DateTimeOffset(2026, 9, 4, 14, 30, 5, TimeSpan.Zero));

        Assert.True(Directory.Exists(session.Folder));
        Assert.True(Directory.Exists(session.RollbackFolder));
        Assert.EndsWith("2026-09-04_14-30-05__Fix-tax-rounding", session.Folder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_writes_a_client_scoped_file_with_a_header_and_the_definition()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);
        DatabaseTarget target = Client();
        ProgrammableObject module = Module("dbo", "usp_GetOrders", "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1");

        string path = await session.CaptureAsync(target, module);
        string content = await File.ReadAllTextAsync(path);

        // Backups are named <object>_<timestamp>.sql, under a per-client folder.
        Assert.Equal(Path.Combine(session.Folder, "Acme"), Path.GetDirectoryName(path));
        Assert.Equal($"dbo.usp_GetOrders_{session.Timestamp}.sql", Path.GetFileName(path));
        Assert.Contains("Client     : Acme", content);
        Assert.Contains("Server      : sql-01", content);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.usp_GetOrders", content);
    }

    [Fact]
    public async Task CaptureAsync_throws_for_an_object_with_no_readable_definition()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);
        var encrypted = new ProgrammableObject(
            new ObjectName("dbo", "usp_Secret"), ProgrammableObjectKind.StoredProcedure, "",
            true, true, 1, DateTime.UtcNow, DateTime.UtcNow, IsEncrypted: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureAsync(Client(), encrypted));
    }

    [Fact]
    public async Task CaptureMissingAsync_writes_a_marker_file()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);

        string path = await session.CaptureMissingAsync(Client(), new ObjectName("dbo", "usp_New"));
        string content = await File.ReadAllTextAsync(path);

        Assert.Equal($"dbo.usp_New_{session.Timestamp}.NEW.txt", Path.GetFileName(path));
        Assert.Contains("did not exist", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteRollbackAsync_reapplies_captured_definitions_and_drops_new_objects()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);
        DatabaseTarget target = Client();
        ProgrammableObject module = Module("dbo", "usp_GetOrders", "ALTER PROCEDURE dbo.usp_GetOrders AS SELECT 1");

        string path = await session.WriteRollbackAsync(
            target,
            [module],
            [new DroppedObject(new ObjectName("dbo", "usp_BrandNew"), ProgrammableObjectKind.StoredProcedure)]);
        string content = await File.ReadAllTextAsync(path);

        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.usp_GetOrders", content);
        Assert.Contains("DROP PROCEDURE IF EXISTS [dbo].[usp_BrandNew]", content);
    }

    [Fact]
    public async Task WriteManifestAsync_produces_readable_json()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);
        var manifest = new SqlHelper.Core.Backup.DeploymentManifest
        {
            ToolVersion = "0.1.0", Operator = "DOMAIN\\user", Machine = "PC1",
            StartedUtc = DateTimeOffset.UtcNow, ChangeTitle = "Fix tax rounding",
        };
        manifest.Entries.Add(new ManifestEntry
        {
            Client = "Acme", Server = "sql-01", Database = "AcmeDb", Environment = "Production",
            ObjectName = "dbo.usp_GetOrders", ObjectKind = "StoredProcedure", Method = "replace", Result = "succeeded",
        });

        string path = await session.WriteManifestAsync(manifest);

        Assert.True(File.Exists(path));
        string json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Fix tax rounding\"", json);
        Assert.Contains("\"succeeded\"", json);
    }

    [Fact]
    public async Task Two_clients_get_separate_folders_under_the_same_session()
    {
        BackupSession session = new BackupWriter(_root).BeginSession("change", DateTimeOffset.Now);
        ProgrammableObject module = Module("dbo", "usp_X", "CREATE PROCEDURE dbo.usp_X AS SELECT 1");

        await session.CaptureAsync(Client("Acme"), module);
        await session.CaptureAsync(Client("Globex"), module);

        Assert.Single(Directory.GetFiles(Path.Combine(session.Folder, "Acme"), "dbo.usp_X_*.sql"));
        Assert.Single(Directory.GetFiles(Path.Combine(session.Folder, "Globex"), "dbo.usp_X_*.sql"));
    }
}
