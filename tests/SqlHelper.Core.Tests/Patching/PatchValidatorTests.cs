using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

public sealed class PatchValidatorTests
{
    private const string Original = "CREATE PROCEDURE dbo.usp_X @id int AS\nBEGIN\n  SELECT @id\nEND";

    [Fact]
    public void A_small_matching_change_passes_clean()
    {
        string patched = "CREATE PROCEDURE dbo.usp_X @id int AS\nBEGIN\n  SELECT @id + 1\nEND";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.True(result.Ok);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_patch_that_renames_the_object_is_rejected()
    {
        string patched = "CREATE PROCEDURE dbo.usp_Renamed @id int AS\nBEGIN\n  SELECT @id\nEND";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("named", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_patch_that_breaks_parsing_is_rejected()
    {
        string patched = "CREATE PROCEDURE dbo.usp_X @id int AS\nBEGIN\n  SELECT FROM WHERE\nEND";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_patch_that_changes_the_kind_is_rejected()
    {
        string patched = "CREATE VIEW dbo.usp_X AS SELECT 1 AS id";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.False(result.Ok);
    }

    [Fact]
    public void A_change_far_larger_than_the_hunk_produces_a_warning_not_an_error()
    {
        string patched =
            "CREATE PROCEDURE dbo.usp_X @id int AS\nBEGIN\n" +
            string.Join('\n', Enumerable.Range(0, 30).Select(i => $"  PRINT 'line {i}'")) +
            "\nEND";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.True(result.Ok);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void A_signature_change_is_surfaced_as_a_warning_with_the_diff_attached()
    {
        string patched = "CREATE PROCEDURE dbo.usp_X @id int, @extra bit = 0 AS\nBEGIN\n  SELECT @id\nEND";

        PatchValidation result = PatchValidator.Validate(
            Original, patched, new ObjectName("dbo", "usp_X"), ProgrammableObjectKind.StoredProcedure, hunkLineCount: 1);

        Assert.True(result.Ok);
        Assert.NotNull(result.SignatureChange);
        Assert.False(result.SignatureChange!.Identical);
        Assert.Contains(result.Warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }
}
