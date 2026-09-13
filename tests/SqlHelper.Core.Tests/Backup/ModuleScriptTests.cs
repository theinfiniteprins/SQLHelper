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

    private const string Header =
        "-- =============================================\r\n" +
        "-- Author:      A. Developer\r\n" +
        "-- Create date: 2019-04-02\r\n" +
        "-- Description: Returns orders for one customer.\r\n" +
        "-- =============================================\r\n";

    // ------------------------------------------------------------------ the verb itself

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1")]
    public void Any_verb_becomes_ALTER_for_an_existing_object(string definition)
    {
        Assert.Equal("ALTER PROCEDURE dbo.x AS SELECT 1", ModuleScript.WithVerb(definition, ModuleVerb.Alter));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1")]
    public void Any_verb_becomes_CREATE_for_a_new_object(string definition)
    {
        Assert.Equal("CREATE PROCEDURE dbo.x AS SELECT 1", ModuleScript.WithVerb(definition, ModuleVerb.Create));
    }

    [Fact]
    public void CREATE_OR_ALTER_is_never_produced()
    {
        // SQL Server stores CREATE OR ALTER as "CREATE   PROCEDURE" - two extra spaces per deployment.
        foreach (ModuleVerb verb in Enum.GetValues<ModuleVerb>())
        {
            string script = ModuleScript.ToRunnableScript(Module("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1"), verb);
            Assert.DoesNotContain("OR ALTER", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ the spacing bug

    [Theory]
    [InlineData("CREATE   PROCEDURE dbo.x AS SELECT 1")]      // after one CREATE OR ALTER deployment
    [InlineData("CREATE     PROCEDURE dbo.x AS SELECT 1")]    // after two
    [InlineData("CREATE\t\tPROCEDURE dbo.x AS SELECT 1")]
    public void Spaces_left_behind_by_earlier_deployments_are_healed_back_to_one(string storedOnServer)
    {
        Assert.Equal("ALTER PROCEDURE dbo.x AS SELECT 1", ModuleScript.WithVerb(storedOnServer, ModuleVerb.Alter));
    }

    [Fact]
    public void Deploying_the_same_object_again_and_again_never_grows_the_gap()
    {
        // Model the round trip exactly: the server stores the text with its verb rewritten to CREATE.
        string stored = "CREATE PROCEDURE dbo.x AS SELECT 1";

        for (int deployment = 0; deployment < 10; deployment++)
        {
            string sent = ModuleScript.WithVerb(stored, ModuleVerb.Alter);
            stored = ModuleScript.ExpectedStoredDefinition(sent);
        }

        Assert.Equal("CREATE PROCEDURE dbo.x AS SELECT 1", stored);
    }

    [Fact]
    public void A_line_break_between_the_verb_and_the_keyword_is_left_alone()
    {
        const string definition = "ALTER\r\nPROCEDURE dbo.x AS SELECT 1";

        Assert.Equal("ALTER\r\nPROCEDURE dbo.x AS SELECT 1", ModuleScript.WithVerb(definition, ModuleVerb.Alter));
    }

    [Fact]
    public void Nothing_after_the_object_keyword_is_touched()
    {
        const string definition = "ALTER PROCEDURE   dbo.x    @a  int\r\nAS\r\n  SELECT   1  ";

        Assert.Equal(definition, ModuleScript.WithVerb(definition, ModuleVerb.Alter));
    }

    // ------------------------------------------------------------------ what the server keeps

    [Theory]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1", "CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("alter proc dbo.x AS SELECT 1", "CREATE proc dbo.x AS SELECT 1")]
    [InlineData("-- hdr\r\nALTER VIEW dbo.v AS SELECT 1", "-- hdr\r\nCREATE VIEW dbo.v AS SELECT 1")]
    [InlineData("ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("ALTER TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1", "CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1")]
    public void The_expected_stored_text_matches_what_SQL_Server_actually_keeps(string sent, string stored)
    {
        // Observed directly against SQL Server: the verb becomes CREATE, everything else is kept.
        Assert.Equal(stored, ModuleScript.ExpectedStoredDefinition(sent));
    }

    // ------------------------------------------------------------------ comment headers

    [Fact]
    public void A_procedure_with_a_comment_header_still_gets_its_verb_rewritten()
    {
        string result = ModuleScript.WithVerb(Header + "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1", ModuleVerb.Alter);

        Assert.Equal(Header + "ALTER PROCEDURE dbo.usp_GetOrders AS SELECT 1", result);
    }

    [Fact]
    public void A_block_comment_header_is_skipped_over_and_kept()
    {
        const string definition = "/* first\r\n   second */\r\nCREATE PROC dbo.usp_X AS SELECT 1";

        Assert.Equal("/* first\r\n   second */\r\nALTER PROC dbo.usp_X AS SELECT 1", ModuleScript.WithVerb(definition, ModuleVerb.Alter));
    }

    [Fact]
    public void Nested_block_comments_do_not_confuse_the_scan()
    {
        const string definition = "/* outer /* inner */ still outer */\r\nCREATE PROCEDURE dbo.usp_X AS SELECT 1";

        Assert.EndsWith("ALTER PROCEDURE dbo.usp_X AS SELECT 1", ModuleScript.WithVerb(definition, ModuleVerb.Alter), StringComparison.Ordinal);
    }

    [Fact]
    public void The_word_CREATE_inside_a_comment_is_not_mistaken_for_the_verb()
    {
        const string definition = "-- CREATE PROCEDURE dbo.usp_Old  (superseded)\r\nCREATE PROCEDURE dbo.usp_New AS SELECT 1";

        string result = ModuleScript.WithVerb(definition, ModuleVerb.Alter);

        Assert.StartsWith("-- CREATE PROCEDURE dbo.usp_Old  (superseded)", result, StringComparison.Ordinal);
        Assert.EndsWith("ALTER PROCEDURE dbo.usp_New AS SELECT 1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_with_no_verb_at_all_is_reported_rather_than_silently_run()
    {
        Assert.False(ModuleScript.CanRewriteVerb("-- just a comment\r\nSELECT 1"));
        Assert.False(ModuleScript.CanRewriteVerb("-- nothing but a comment"));
        Assert.True(ModuleScript.CanRewriteVerb(Header + "CREATE PROCEDURE dbo.usp_X AS SELECT 1"));
    }

    // ------------------------------------------------------------------ the runnable script

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
    public void ToRunnableScript_uses_ALTER_by_default_the_way_SSMS_scripts_an_existing_object()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1"));

        Assert.Contains("ALTER PROCEDURE dbo.x", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runnable_script_keeps_the_header()
    {
        string script = ModuleScript.ToRunnableScript(Module(Header + "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1"));

        Assert.Contains("-- Author:      A. Developer", script, StringComparison.Ordinal);
        Assert.Contains("ALTER PROCEDURE dbo.usp_GetOrders", script, StringComparison.Ordinal);
    }
}
