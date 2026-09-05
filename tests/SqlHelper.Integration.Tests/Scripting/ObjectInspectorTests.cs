using Microsoft.Data.SqlClient;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Scripting;

[Collection(SqlServerCollection.Name)]
public sealed class ObjectInspectorTests
{
    private readonly SqlServerFixture _sql;
    private readonly ObjectInspector _inspector = new();

    public ObjectInspectorTests(SqlServerFixture sql) => _sql = sql;

    private async Task<SqlConnection> OpenAsync()
        => await _sql.Connections.OpenAsync(_sql.Target, CancellationToken.None);

    [Fact]
    public async Task Reads_a_stored_procedure_definition_and_flags()
    {
        await _sql.ExecAsync(
            "CREATE OR ALTER PROCEDURE dbo.usp_Sample @id int AS BEGIN SET NOCOUNT ON; SELECT @id; END");

        await using SqlConnection connection = await OpenAsync();
        ProgrammableObject? obj = await _inspector.GetAsync(connection, ObjectName.Parse("dbo.usp_Sample"));

        Assert.NotNull(obj);
        Assert.Equal(ProgrammableObjectKind.StoredProcedure, obj.Kind);
        Assert.Contains("SELECT @id", obj.Definition, StringComparison.OrdinalIgnoreCase);
        Assert.True(obj.UsesQuotedIdentifier);
        Assert.False(obj.IsEncrypted);
        Assert.True(obj.HasDefinition);
    }

    [Fact]
    public async Task Distinguishes_scalar_and_table_valued_functions()
    {
        await _sql.ExecAsync("CREATE FUNCTION dbo.fn_Double(@n int) RETURNS int AS BEGIN RETURN @n * 2 END");
        await _sql.ExecAsync("CREATE FUNCTION dbo.tvf_Nums(@n int) RETURNS TABLE AS RETURN (SELECT @n AS Value)");

        await using SqlConnection connection = await OpenAsync();

        Assert.Equal(ProgrammableObjectKind.ScalarFunction,
            (await _inspector.GetAsync(connection, ObjectName.Parse("dbo.fn_Double")))!.Kind);
        Assert.Equal(ProgrammableObjectKind.TableValuedFunction,
            (await _inspector.GetAsync(connection, ObjectName.Parse("dbo.tvf_Nums")))!.Kind);
    }

    [Fact]
    public async Task Returns_null_for_a_missing_object()
    {
        await using SqlConnection connection = await OpenAsync();

        Assert.Null(await _inspector.GetAsync(connection, ObjectName.Parse("dbo.does_not_exist")));
        Assert.False(await _inspector.ExistsAsync(connection, ObjectName.Parse("dbo.does_not_exist")));
    }

    [Fact]
    public async Task Flags_an_encrypted_module_as_having_no_definition()
    {
        await _sql.ExecAsync(
            "CREATE PROCEDURE dbo.usp_Secret WITH ENCRYPTION AS SELECT 'hidden'");

        await using SqlConnection connection = await OpenAsync();
        ProgrammableObject? obj = await _inspector.GetAsync(connection, ObjectName.Parse("dbo.usp_Secret"));

        Assert.NotNull(obj);
        Assert.True(obj.IsEncrypted);
        Assert.False(obj.HasDefinition);
    }

    [Fact]
    public async Task Reads_a_full_parameter_signature()
    {
        await _sql.ExecAsync(
            """
            CREATE OR ALTER PROCEDURE dbo.usp_Signature
                @id int,
                @name varchar(50),
                @includeArchived bit = 0,
                @rowcount int OUTPUT
            AS BEGIN SET @rowcount = 0; END
            """);

        await using SqlConnection connection = await OpenAsync();
        RoutineSignature? signature = await _inspector.GetSignatureAsync(connection, ObjectName.Parse("dbo.usp_Signature"));

        Assert.NotNull(signature);
        Assert.Equal(4, signature.Parameters.Count);
        Assert.Contains(signature.Parameters, p => p is { Name: "@includeArchived", HasDefault: true });
        Assert.Contains(signature.Parameters, p => p is { Name: "@rowcount", IsOutput: true });
    }

    [Fact]
    public async Task Signature_comparison_detects_client_drift()
    {
        await _sql.ExecAsync("CREATE OR ALTER PROCEDURE dbo.usp_Ref @id int AS SELECT @id");
        await _sql.ExecAsync("CREATE OR ALTER PROCEDURE dbo.usp_Variant @id int, @extra bit = 0 AS SELECT @id");

        await using SqlConnection connection = await OpenAsync();
        RoutineSignature reference = (await _inspector.GetSignatureAsync(connection, ObjectName.Parse("dbo.usp_Ref")))!;
        RoutineSignature variant = (await _inspector.GetSignatureAsync(connection, ObjectName.Parse("dbo.usp_Variant")))!;

        Assert.False(reference.CompareWith(variant).Identical);
    }

    [Fact]
    public async Task Lists_programmable_objects_filtered_by_kind()
    {
        await _sql.ExecAsync("CREATE VIEW dbo.v_ListMe AS SELECT 1 AS x");
        await _sql.ExecAsync("CREATE PROCEDURE dbo.usp_ListMe AS SELECT 1");

        await using SqlConnection connection = await OpenAsync();
        IReadOnlyList<ObjectSummary> views = await _inspector.ListAsync(connection, [ProgrammableObjectKind.View]);

        Assert.Contains(views, o => o.Name.Name == "v_ListMe");
        Assert.DoesNotContain(views, o => o.Kind != ProgrammableObjectKind.View);
    }
}
