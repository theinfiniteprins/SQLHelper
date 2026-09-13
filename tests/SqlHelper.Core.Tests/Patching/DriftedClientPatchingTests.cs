using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

/// <summary>
/// Real clients' copies of "the same" procedure differ in formatting: one team indents with tabs,
/// another re-aligned the column lists. Changes of three or four lines at a time then failed to
/// match, because lines were compared exactly (bar their ends). These tests take the large
/// production-shaped procedure, reformat the client's copy throughout, and require every change to
/// land — with the client's formatting kept on every line the change does not touch.
/// </summary>
public sealed class DriftedClientPatchingTests
{
    private static string? _script;

    private static string Script => _script ??= File.ReadAllText(FixturePath());

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Fixtures", "LargeProcedure.sql");
    }

    private static readonly DatabaseTarget Client = new()
    {
        ClientName = "Drifted", ServerName = "sql-01", DatabaseName = "ClientDb", Environment = ServerEnvironment.Production,
    };

    private static string LiveDefinition(string script)
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);
        Assert.True(extraction.Ok, extraction.Error);
        return extraction.ModuleText;
    }

    private static ProgrammableObject Module(string definition) => new(
        ObjectName.Parse("dbo.usp_Request_SelectForApproval"), ProgrammableObjectKind.StoredProcedure, definition,
        true, true, 1, DateTime.UtcNow, DateTime.UtcNow, false);

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>
    /// Tabs become spaces and alignment runs collapse, as a different team would format it - on
    /// every line that holds no string literal. Whitespace inside a literal is part of its value,
    /// so reformatting those lines would be a real change, not a formatting one.
    /// </summary>
    private static string Reformat(string text) =>
        string.Join("\r\n", Lines(text).Select(l => l.Contains('\'') || l.Contains('"')
            ? l
            : System.Text.RegularExpressions.Regex.Replace(l.Replace("\t", "    "), " {2,}", "  ")));

    private static string InsertAfter(string text, string marker, params string[] newLines)
    {
        var lines = new List<string>(Lines(text));
        int at = lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(at >= 0, $"fixture no longer contains: {marker}");
        lines.InsertRange(at + 1, newLines);
        return string.Join('\n', lines);
    }

    private static string ReplaceLine(string text, string marker, string replacement)
    {
        var lines = new List<string>(Lines(text));
        int at = lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(at >= 0, $"fixture no longer contains: {marker}");
        lines[at] = replacement;
        return string.Join('\n', lines);
    }

    private static string[] Keys(string text) => SqlLineKeys.Compute(Lines(text));

    [Fact]
    public void The_reformatted_client_really_differs_but_only_in_formatting()
    {
        string live = LiveDefinition(Script);
        string drifted = Reformat(live);

        Assert.NotEqual(live, drifted);
        Assert.Equal(Keys(live), Keys(drifted));
    }

    [Fact]
    public void Several_multi_line_changes_land_on_a_client_formatted_differently_throughout()
    {
        string script = Script;

        // Four separate changes, each several lines, a mix of additions and rewrites.
        string after = script;
        after = InsertAfter(after, "[CancellationDateTime] IS NULL",
            "\t\tAND\t\t[dbo].[REQ_Request].[IsDeleted] = 0",
            "\t\tAND\t\t[dbo].[REQ_Request].[IsArchived] = 0",
            "\t\tAND\t\t[dbo].[REQ_Request].[IsDraft] = 0");
        after = ReplaceLine(after, "[dbo].[REQ_Request].[TotalDays],", "\t\t\t\t\t[dbo].[REQ_Request].[TotalDays] AS [Days],");
        after = InsertAfter(after, "[dbo].[REQ_Request].[TotalDays] AS [Days],",
            "\t\t\t\t\t[dbo].[REQ_Request].[NewColumnOne],",
            "\t\t\t\t\t[dbo].[REQ_Request].[NewColumnTwo],");
        after = InsertAfter(after, "[dbo].[REQ_RequestType].[IsShort] = @IsShort",
            "\t\t\t\t\tAND [dbo].[REQ_RequestType].[IsActive] = 1",
            "\t\t\t\t\tAND [dbo].[REQ_RequestType].[IsVisible] = 1");

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-7", script, after);
        Assert.True(built.Ok, built.Error);

        string drifted = Reformat(LiveDefinition(script));
        PatchAttempt attempt = PatchPlanner.Plan(Client, Module(drifted), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome}: {attempt.Summary}");

        // Formatting aside, the result is exactly the reference's new version.
        Assert.Equal(Keys(LiveDefinition(after)), Keys(attempt.PatchedBody!));

        // And the formatting is the client's, not the reference's, wherever the change did not reach.
        string[] resultLines = Lines(attempt.PatchedBody!);
        string[] driftedLines = Lines(drifted);
        int keptVerbatim = driftedLines.Count(l => resultLines.Contains(l));
        Assert.True(keptVerbatim >= driftedLines.Length - 10, $"only {keptVerbatim} of {driftedLines.Length} client lines were kept verbatim");

        // Every hunk was placed exactly - the reformatting alone never pushes a change to the tolerant tier.
        Assert.All(attempt.HunkAttempts, h => Assert.Equal(PatchTier.ExactAnchor, h.Tier));
    }

    [Fact]
    public void A_line_added_anywhere_lands_on_a_reformatted_client()
    {
        string script = Script;
        string drifted = Reformat(LiveDefinition(script));
        string[] scriptLines = Lines(script);

        var failures = new List<string>();
        int applied = 0;

        for (int at = 1; at < scriptLines.Length - 1; at++)
        {
            var edited = new List<string>(scriptLines);
            edited.Insert(at, "\t\tAND\t\t[dbo].[REQ_Request].[IsDeleted] = 0");
            string after = string.Join('\n', edited);

            PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("sweep", script, after);
            if (!built.Ok)
            {
                continue;
            }

            PatchAttempt attempt = PatchPlanner.Plan(Client, Module(drifted), built.Patch!);
            if (attempt.PatchedBody is null)
            {
                failures.Add($"line {at}: refused ({attempt.Summary})");
                continue;
            }

            applied++;
            if (!Keys(LiveDefinition(after)).SequenceEqual(Keys(attempt.PatchedBody)))
            {
                failures.Add($"line {at}: applied, but not to the intended result");
            }
        }

        Assert.True(applied > 100, $"only {applied} positions exercised");
        Assert.Empty(failures);
    }
}
