using SqlHelper.Core.Backup;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Backup;

public sealed class ModuleScriptTests
{
    private static ProgrammableObject Module(string definition, bool ansiNulls = true, bool quotedId = true) => new(
        new ObjectName("dbo", "usp_X"),
        ProgrammableObjectKind.StoredProcedure,
        definition,
        ansiNulls,
        quotedId,
        ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow,
        ModifiedServerTime: DateTime.UtcNow,
        IsEncrypted: false);

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1")]
    public void AsCreateOrAlter_normalises_the_verb(string definition)
    {
        Assert.StartsWith("CREATE OR ALTER PROCEDURE", ModuleScript.AsCreateOrAlter(definition));
    }

    [Fact]
    public void ToRunnableScript_carries_the_captured_set_options()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1", ansiNulls: false, quotedId: true));

        Assert.Contains("SET ANSI_NULLS OFF", script);
        Assert.Contains("SET QUOTED_IDENTIFIER ON", script);
    }

    [Fact]
    public void ToRunnableScript_batches_each_part_with_GO()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1"));

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [Fact]
    public void ToRunnableScript_forces_create_or_alter_by_default()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1"));

        Assert.Contains("CREATE OR ALTER PROCEDURE", script);
    }

    // ------------------------------------------------- real procedures have headers

    private const string Header =
        "-- =============================================\r\n" +
        "-- Author:      A. Developer\r\n" +
        "-- Create date: 2019-04-02\r\n" +
        "-- Description: Returns orders for one customer.\r\n" +
        "-- =============================================\r\n";

    [Fact]
    public void A_procedure_with_a_comment_header_still_gets_CREATE_OR_ALTER()
    {
        string definition = Header + "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1";

        string result = ModuleScript.AsCreateOrAlter(definition);

        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.usp_GetOrders", result, StringComparison.Ordinal);
        Assert.StartsWith(Header, result, StringComparison.Ordinal); // the header survived intact
    }

    [Fact]
    public void An_ALTER_under_a_comment_header_is_rewritten_too()
    {
        string result = ModuleScript.AsCreateOrAlter(Header + "ALTER PROCEDURE dbo.usp_GetOrders AS SELECT 1");

        Assert.Contains("CREATE OR ALTER PROCEDURE", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER PROCEDURE dbo", result.Replace("CREATE OR ALTER PROCEDURE", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_comment_header_is_skipped_over_and_kept()
    {
        const string definition = "/* first\r\n   second */\r\nCREATE PROC dbo.usp_X AS SELECT 1";

        string result = ModuleScript.AsCreateOrAlter(definition);

        Assert.Contains("CREATE OR ALTER PROC dbo.usp_X", result, StringComparison.Ordinal);
        Assert.StartsWith("/* first", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_block_comments_do_not_confuse_the_scan()
    {
        const string definition = "/* outer /* inner */ still outer */\r\nCREATE PROCEDURE dbo.usp_X AS SELECT 1";

        Assert.Contains("CREATE OR ALTER PROCEDURE", ModuleScript.AsCreateOrAlter(definition), StringComparison.Ordinal);
    }

    [Fact]
    public void The_word_CREATE_inside_a_comment_is_not_mistaken_for_the_verb()
    {
        const string definition = "-- CREATE PROCEDURE dbo.usp_Old  (superseded)\r\nALTER PROCEDURE dbo.usp_New AS SELECT 1";

        string result = ModuleScript.AsCreateOrAlter(definition);

        Assert.StartsWith("-- CREATE PROCEDURE dbo.usp_Old  (superseded)", result, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.usp_New", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_already_CREATE_OR_ALTER_is_left_exactly_as_it_is()
    {
        string definition = Header + "CREATE   OR   ALTER PROCEDURE dbo.usp_X AS SELECT 1";

        Assert.Equal(definition, ModuleScript.AsCreateOrAlter(definition));
    }

    [Fact]
    public void A_definition_with_no_verb_at_all_is_reported_rather_than_silently_run()
    {
        Assert.False(ModuleScript.CanRewriteVerb("-- just a comment\r\nSELECT 1"));
        Assert.False(ModuleScript.CanRewriteVerb("-- nothing but a comment"));
        Assert.True(ModuleScript.CanRewriteVerb(Header + "CREATE PROCEDURE dbo.usp_X AS SELECT 1"));
    }

    [Fact]
    public void The_runnable_script_keeps_the_header_and_carries_the_SET_options()
    {
        var module = new ProgrammableObject(
            new ObjectName("dbo", "usp_GetOrders"), ProgrammableObjectKind.StoredProcedure,
            Header + "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1",
            UsesAnsiNulls: true, UsesQuotedIdentifier: true, ObjectId: 1,
            CreatedServerTime: DateTime.UtcNow, ModifiedServerTime: DateTime.UtcNow, IsEncrypted: false);

        string script = ModuleScript.ToRunnableScript(module);

        Assert.Contains("SET ANSI_NULLS ON", script, StringComparison.Ordinal);
        Assert.Contains("-- Author:      A. Developer", script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.usp_GetOrders", script, StringComparison.Ordinal);
    }
}
