using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Scripting;

public sealed class TSqlNormalizerTests
{
    private const string Preamble =
        "USE [AcmeDb]\nGO\nSET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n";

    private const string Proc =
        "CREATE OR ALTER PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id;\nEND";

    [Fact]
    public void StripLeadingDirectives_removes_use_go_and_set_preamble()
    {
        string body = TSqlNormalizer.StripLeadingDirectives(Preamble + Proc, out IReadOnlyList<string> removed);

        Assert.StartsWith("CREATE OR ALTER PROCEDURE", body);
        Assert.Contains(removed, r => r.Contains("USE [AcmeDb]", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(removed, r => r.Contains("ANSI_NULLS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StripLeadingDirectives_leaves_a_clean_definition_untouched()
    {
        string body = TSqlNormalizer.StripLeadingDirectives(Proc, out IReadOnlyList<string> removed);

        Assert.Equal(Proc, body.TrimEnd());
        Assert.Empty(removed);
    }

    [Fact]
    public void SemanticKey_ignores_whitespace_and_keyword_casing()
    {
        string a = "CREATE PROCEDURE dbo.x @a int AS SELECT * FROM t WHERE a = @a";
        string b = "create   procedure dbo.x\n  @a int\nas\n   select *   from t   where a=@a";

        Assert.Equal(TSqlNormalizer.SemanticKey(a), TSqlNormalizer.SemanticKey(b));
    }

    [Fact]
    public void SemanticKey_folds_create_alter_and_create_or_alter()
    {
        string create = "CREATE PROCEDURE dbo.x AS SELECT 1";
        string alter = "ALTER PROCEDURE dbo.x AS SELECT 1";
        string createOrAlter = "CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1";

        string key = TSqlNormalizer.SemanticKey(create);
        Assert.Equal(key, TSqlNormalizer.SemanticKey(alter));
        Assert.Equal(key, TSqlNormalizer.SemanticKey(createOrAlter));
    }

    [Fact]
    public void SemanticKey_changes_when_the_logic_changes()
    {
        Assert.NotEqual(
            TSqlNormalizer.SemanticKey("CREATE PROCEDURE dbo.x AS SELECT 1"),
            TSqlNormalizer.SemanticKey("CREATE PROCEDURE dbo.x AS SELECT 2"));
    }

    [Fact]
    public void SemanticKey_marks_unparseable_definitions_degraded()
    {
        string key = TSqlNormalizer.SemanticKey("CREATE PROCEDURE dbo.x AS SELECT FROM WHERE");

        Assert.StartsWith("degraded:", key);
    }

    [Fact]
    public void ExactKey_ignores_line_endings_and_trailing_spaces_only()
    {
        string a = "CREATE PROC dbo.x AS\r\n  SELECT 1   \r\n";
        string b = "CREATE PROC dbo.x AS\n  SELECT 1\n";

        Assert.Equal(TSqlNormalizer.ExactKey(a), TSqlNormalizer.ExactKey(b));
        Assert.NotEqual(TSqlNormalizer.ExactKey(a), TSqlNormalizer.ExactKey("CREATE PROC dbo.x AS\n  SELECT 2\n"));
    }

    [Fact]
    public void ExtractModule_pulls_one_procedure_out_of_a_full_script()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(Preamble + Proc + "\nGO\n");

        Assert.True(extraction.Ok, extraction.Error);
        Assert.Equal(ProgrammableObjectKind.StoredProcedure, extraction.Kind);
        Assert.Equal("dbo", extraction.Name!.Schema);
        Assert.Equal("usp_GetOrders", extraction.Name.Name);
        Assert.StartsWith("CREATE OR ALTER PROCEDURE", extraction.ModuleText);
        Assert.DoesNotContain("USE [AcmeDb]", extraction.ModuleText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CREATE FUNCTION dbo.fn_Tax(@amt money) RETURNS money AS BEGIN RETURN @amt * 0.2 END", ProgrammableObjectKind.ScalarFunction)]
    [InlineData("CREATE FUNCTION dbo.tvf_Orders(@id int) RETURNS TABLE AS RETURN (SELECT * FROM dbo.Orders WHERE Id = @id)", ProgrammableObjectKind.TableValuedFunction)]
    [InlineData("CREATE VIEW dbo.v_Orders AS SELECT * FROM dbo.Orders", ProgrammableObjectKind.View)]
    public void ExtractModule_identifies_the_kind(string script, ProgrammableObjectKind expected)
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.Equal(expected, extraction.Kind);
    }

    [Fact]
    public void ExtractModule_rejects_a_script_that_changes_two_objects()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(
            "CREATE PROCEDURE dbo.a AS SELECT 1\nGO\nCREATE PROCEDURE dbo.b AS SELECT 2\nGO");

        Assert.False(extraction.Ok);
        Assert.Contains("2 objects", extraction.Error);
    }

    [Fact]
    public void ExtractModule_rejects_a_script_with_no_module()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule("SELECT * FROM dbo.Orders");

        Assert.False(extraction.Ok);
    }
}
