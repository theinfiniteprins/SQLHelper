using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Scripting;

/// <summary>
/// A real procedure's header comment — author, description, change log — is part of the procedure.
/// Deploying used to slice from the CREATE keyword, which quietly erased it on every client.
/// Only the USE / SET / GO preamble should ever be dropped.
/// </summary>
public sealed class ModuleCommentPreservationTests
{
    private const string Preamble =
        "USE [AcmeDb]\nGO\nSET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n";

    private const string Proc =
        "CREATE OR ALTER PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id;\nEND";

    private const string Header =
        "-- =============================================\n" +
        "-- Author:      A. Developer\n" +
        "-- Description: Returns orders for one customer.\n" +
        "-- =============================================\n";

    [Fact]
    public void The_comment_header_above_the_procedure_is_deployed_with_it()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(Preamble + Header + Proc + "\nGO\n");

        Assert.True(extraction.Ok, extraction.Error);
        Assert.Contains("-- Author:      A. Developer", extraction.ModuleText, StringComparison.Ordinal);
        Assert.Contains("-- Description: Returns orders for one customer.", extraction.ModuleText, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE", extraction.ModuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_use_and_set_preamble_is_still_the_only_thing_removed()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(Preamble + Header + Proc);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.DoesNotContain("USE [AcmeDb]", extraction.ModuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET ANSI_NULLS", extraction.ModuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET QUOTED_IDENTIFIER", extraction.ModuleText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_block_comment_header_survives_too()
    {
        const string header = "/*\n  Change log:\n    2021-06-01  added Active filter\n*/\n";

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(header + Proc);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.StartsWith("/*", extraction.ModuleText, StringComparison.Ordinal);
        Assert.Contains("added Active filter", extraction.ModuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_comment_blocks_stacked_above_the_procedure_all_survive()
    {
        const string header = "-- first note\n\n/* second note */\n-- third note\n";

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(header + Proc);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.Contains("first note", extraction.ModuleText, StringComparison.Ordinal);
        Assert.Contains("second note", extraction.ModuleText, StringComparison.Ordinal);
        Assert.Contains("third note", extraction.ModuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_that_belongs_to_the_preamble_is_not_dragged_along()
    {
        // Separated from the procedure by GO, so it is a note about the USE, not about the module.
        const string script =
            "-- run this against the client database\nUSE [AcmeDb]\nGO\n" +
            "CREATE OR ALTER PROCEDURE dbo.usp_GetOrders AS SELECT 1";

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.StartsWith("CREATE OR ALTER PROCEDURE", extraction.ModuleText, StringComparison.Ordinal);
        Assert.DoesNotContain("run this against", extraction.ModuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_between_the_preamble_and_the_procedure_belongs_to_the_procedure()
    {
        const string script =
            "USE [AcmeDb]\nGO\n-- fixes ORD-1487\nCREATE OR ALTER PROCEDURE dbo.usp_GetOrders AS SELECT 1";

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.StartsWith("-- fixes ORD-1487", extraction.ModuleText, StringComparison.Ordinal);
        Assert.DoesNotContain("USE [AcmeDb]", extraction.ModuleText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_procedure_with_no_header_at_all_is_unchanged_from_before()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(Preamble + Proc);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.StartsWith("CREATE OR ALTER PROCEDURE", extraction.ModuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_object_name_and_kind_are_still_read_correctly_with_a_header_present()
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(Header + Proc);

        Assert.True(extraction.Ok, extraction.Error);
        Assert.Equal(ProgrammableObjectKind.StoredProcedure, extraction.Kind);
        Assert.Equal("dbo", extraction.Name!.Schema);
        Assert.Equal("usp_GetOrders", extraction.Name.Name);
    }
}
