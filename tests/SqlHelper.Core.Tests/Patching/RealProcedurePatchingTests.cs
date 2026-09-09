using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

/// <summary>
/// Patching exercised against a real production stored procedure — 570 lines, deeply nested, and
/// containing blocks that repeat verbatim (the same <c>@StatusType</c> filter appears under two
/// different joins).
///
/// That repetition is what broke this in production: with a fixed two lines of context, a fifth of
/// all single-line edits produced an anchor matching in two places, the tool could not tell which
/// was meant, and refused. Because a patch is all-or-nothing, four scattered edits then failed
/// more often than they succeeded — on every client at once, including ones byte-identical to the
/// reference.
///
/// The bar these tests hold: for every place a line can be added, the change must either apply and
/// produce *exactly* the intended text, or be reported as needing a hand edit. Never anything else.
/// </summary>
public sealed class RealProcedurePatchingTests
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

    private static DatabaseTarget Client(string name = "Acme") => new()
    {
        ClientName = name, ServerName = "sql-01", DatabaseName = "ClientDb", Environment = ServerEnvironment.Production,
    };

    /// <summary>The definition as the server hands it back: the batch after the last GO.</summary>
    private static string LiveDefinition(string script)
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);
        Assert.True(extraction.Ok, extraction.Error);
        return extraction.ModuleText;
    }

    private static ProgrammableObject Module(string definition) => new(
        ObjectName.Parse("dbo.usp_Request_SelectForApproval"),
        ProgrammableObjectKind.StoredProcedure, definition,
        UsesAnsiNulls: true, UsesQuotedIdentifier: true, ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow, ModifiedServerTime: DateTime.UtcNow, IsEncrypted: false);

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string WithLineInsertedAt(string text, int index, string line)
    {
        var lines = new List<string>(Lines(text));
        lines.Insert(index, line);
        return string.Join('\n', lines);
    }

    private const string AddedLine = "		AND		[dbo].[REQ_Request].[IsDeleted] = 0";

    /// <summary>Inserts directly below the first line containing <paramref name="marker"/>.</summary>
    private static string InsertAfter(string text, string marker, string newLine)
    {
        string[] lines = Lines(text);
        int at = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(at >= 0, $"fixture no longer contains: {marker}");
        return WithLineInsertedAt(text, at + 1, newLine);
    }

    // ------------------------------------------------------------------ the whole surface

    [Fact]
    public void A_line_added_anywhere_in_the_procedure_patches_a_matching_client_exactly()
    {
        string script = Script;
        string live = LiveDefinition(script);
        string[] scriptLines = Lines(script);

        var refused = new List<int>();
        var wrong = new List<string>();
        int applied = 0;

        for (int at = 1; at < scriptLines.Length - 1; at++)
        {
            string after = WithLineInsertedAt(script, at, AddedLine);

            PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("sweep", script, after);
            if (!built.Ok)
            {
                continue; // e.g. the line landed outside the module body
            }

            PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(live), built.Patch!);

            if (attempt.PatchedBody is null)
            {
                refused.Add(at);
                continue;
            }

            applied++;

            // The client is identical to the reference, so the result must be exactly the
            // reference's "after" - nothing more, nothing less, nothing moved.
            string expected = LiveDefinition(after);
            if (!string.Equals(Normalise(attempt.PatchedBody), Normalise(expected), StringComparison.Ordinal))
            {
                wrong.Add($"line {at}");
            }
        }

        Assert.True(applied > 100, $"expected the sweep to actually exercise the patcher, applied only {applied}");
        Assert.Empty(wrong);

        // Refusing is safe, but on a client identical to the reference it should never be needed.
        Assert.Empty(refused);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    // ------------------------------------------------------- the change that failed in production

    [Fact]
    public void Four_lines_added_in_four_separate_places_all_land_on_an_identical_client()
    {
        string script = Script;

        // Four separate one-line additions, several lines apart, each valid where it sits -
        // the shape of the change that failed on all 27 clients.
        string after = script;
        after = InsertAfter(after, "[CancellationDateTime] IS NULL", AddedLine);
        after = InsertAfter(after, "[dbo].[REQ_Request].[TotalDays],", "					[dbo].[REQ_Request].[NewColumnOne],");
        after = InsertAfter(after, "[dbo].[REQ_Request].[Reason],", "					[dbo].[REQ_Request].[NewColumnTwo],");
        after = InsertAfter(after, "[dbo].[REQ_RequestType].[IsShort] = @IsShort", "					AND [dbo].[REQ_RequestType].[IsActive] = 1");

        Assert.Equal(Lines(script).Length + 4, Lines(after).Length);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", script, after);
        Assert.True(built.Ok, built.Error);
        Assert.Equal(4, built.Patch!.Hunks.Count);

        PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(LiveDefinition(script)), built.Patch!);

        Assert.True(
            attempt.Outcome is PatchOutcome.Applied or PatchOutcome.NeedsReview,
            $"outcome was {attempt.Outcome}: {attempt.Summary}");
        Assert.Equal(Normalise(LiveDefinition(after)), Normalise(attempt.PatchedBody!));
    }

    [Fact]
    public void All_twenty_seven_identical_clients_get_the_same_change()
    {
        string script = Script;
        string after = WithLineInsertedAt(script, 170, AddedLine);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", script, after);
        Assert.True(built.Ok, built.Error);

        string live = LiveDefinition(script);
        string expected = Normalise(LiveDefinition(after));

        for (int i = 1; i <= 27; i++)
        {
            PatchAttempt attempt = PatchPlanner.Plan(Client($"Client-{i}"), Module(live), built.Patch!);
            Assert.True(attempt.PatchedBody is not null, $"Client-{i}: {attempt.Outcome} - {attempt.Summary}");
            Assert.Equal(expected, Normalise(attempt.PatchedBody!));
        }
    }

    // --------------------------------------------------------- the repeated block, specifically

    [Fact]
    public void A_change_inside_a_block_that_appears_twice_lands_on_the_right_one()
    {
        string script = Script;
        string[] lines = Lines(script);

        // The "@StatusType = 'All'" filter is present verbatim in two places. Find both, and edit
        // inside the first.
        var occurrences = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "@StatusType = 'All'")
            {
                occurrences.Add(i);
            }
        }

        Assert.Equal(2, occurrences.Count);

        string after = WithLineInsertedAt(script, occurrences[0] + 1, "					-- first block only");

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-2", script, after);
        Assert.True(built.Ok, built.Error);

        PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(LiveDefinition(script)), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome} - {attempt.Summary}");
        Assert.Equal(Normalise(LiveDefinition(after)), Normalise(attempt.PatchedBody!));

        // Exactly one comment was added - the second block was left alone.
        Assert.Equal(1, Lines(attempt.PatchedBody!).Count(l => l.Trim() == "-- first block only"));
    }

    [Fact]
    public void The_anchor_for_a_repeated_block_is_grown_until_it_is_unique()
    {
        string script = Script;
        string[] lines = Lines(script);
        int firstBlock = Array.FindIndex(lines, l => l.Trim() == "@StatusType = 'All'");

        string after = WithLineInsertedAt(script, firstBlock + 1, "					-- marker");
        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-2", script, after);

        Assert.True(built.Ok, built.Error);
        PatchHunk hunk = Assert.Single(built.Patch!.Hunks);

        // A two-line context would have matched twice; this one has grown well past that.
        Assert.True(
            AnchorMatcher.EffectiveAnchorLines(hunk.AnchorText).Length > 5,
            "the anchor should have grown beyond the default context to become unique");

        AnchorMatchResult match = AnchorMatcher.TryApply(LiveDefinition(script), hunk.AnchorText, hunk.ReplacementText);
        Assert.Equal(AnchorMatchStatus.Unique, match.Status);
    }

    // ------------------------------------------------------------------ clients that have drifted

    [Fact]
    public void A_client_that_has_drifted_elsewhere_still_gets_the_change_and_keeps_its_own_code()
    {
        string script = Script;
        string after = WithLineInsertedAt(script, 170, AddedLine);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", script, after);
        Assert.True(built.Ok, built.Error);

        // This client has its own customisation, far away from the change.
        string drifted = LiveDefinition(script).Replace(
            "		ORDER BY [dbo].[REQ_Request].[ApplicationDate] DESC",
            "		-- client-specific ordering, do not remove\r\n		ORDER BY [dbo].[REQ_Request].[ApplicationDate] DESC",
            StringComparison.Ordinal);
        Assert.NotEqual(LiveDefinition(script), drifted);

        PatchAttempt attempt = PatchPlanner.Plan(Client("Drifted"), Module(drifted), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome} - {attempt.Summary}");
        Assert.Contains(AddedLine.Trim(), attempt.PatchedBody!, StringComparison.Ordinal);
        Assert.Contains("client-specific ordering, do not remove", attempt.PatchedBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_missing_the_region_entirely_is_flagged_for_a_hand_edit_and_left_untouched()
    {
        string script = Script;
        string after = InsertAfter(script, "[CancellationDateTime] IS NULL", AddedLine);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", script, after);
        Assert.True(built.Ok, built.Error);

        // This client's filter block was rewritten wholesale - the change has nowhere to go.
        string[] lines = Lines(LiveDefinition(script));
        int at = Array.FindIndex(lines, l => l.Contains("[CancellationDateTime] IS NULL", StringComparison.Ordinal));
        Assert.True(at > 12);

        var rewritten = new List<string>(lines);
        rewritten.RemoveRange(at - 12, 24);
        string diverged = string.Join('\n', rewritten);

        PatchAttempt attempt = PatchPlanner.Plan(Client("Diverged"), Module(diverged), built.Patch!);

        Assert.True(
            attempt.Outcome is PatchOutcome.ManualRequired or PatchOutcome.ValidationFailed,
            $"expected a hand edit to be required, got {attempt.Outcome}: {attempt.Summary}");
        Assert.Null(attempt.PatchedBody);
    }

    // --------------------------------------------------------------- changes close to each other

    [Fact]
    public void Two_changes_a_line_apart_are_merged_into_one_hunk_and_both_land()
    {
        string script = Script;

        // Close enough that their contexts overlap; applied as two hunks the second could never
        // find its anchor, because the first would already have rewritten those lines.
        string after = WithLineInsertedAt(script, 171, "		-- second");
        after = WithLineInsertedAt(after, 170, "		-- first");

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-3", script, after);
        Assert.True(built.Ok, built.Error);
        Assert.Single(built.Patch!.Hunks);

        PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(LiveDefinition(script)), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome} - {attempt.Summary}");
        Assert.Equal(Normalise(LiveDefinition(after)), Normalise(attempt.PatchedBody!));
    }

    [Fact]
    public void A_removed_line_and_an_added_line_are_both_carried_across()
    {
        string script = Script;
        string[] lines = Lines(script);

        int at = Array.FindIndex(lines, l => l.Contains("AND\t\tISNULL([dbo].[REQ_Request].[IsApprovalDelayPenalty],0) = 0", StringComparison.Ordinal));
        Assert.True(at > 0);

        var edited = new List<string>(lines);
        edited[at] = "		AND		ISNULL([dbo].[REQ_Request].[IsApprovalDelayPenalty],0) = 1";
        string after = string.Join('\n', edited);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-4", script, after);
        Assert.True(built.Ok, built.Error);

        PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(LiveDefinition(script)), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome} - {attempt.Summary}");
        Assert.Equal(Normalise(LiveDefinition(after)), Normalise(attempt.PatchedBody!));
    }

    // ---------------------------------------------------------------------- the SSMS paste shape

    [Fact]
    public void The_script_pasted_straight_out_of_SSMS_produces_a_patch_that_matches_the_live_object()
    {
        // The operator pastes the whole SSMS output - USE/GO/SET preamble, the "Script Date"
        // banner, the header comments - while the server stores only the batch after the last GO.
        string script = Script;
        Assert.Contains("SET ANSI_NULLS ON", script, StringComparison.Ordinal);
        Assert.Contains("Script Date:", script, StringComparison.Ordinal);

        string after = WithLineInsertedAt(script, 200, AddedLine);
        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-5", script, after);
        Assert.True(built.Ok, built.Error);

        // No hunk may carry any of the preamble into the change.
        foreach (PatchHunk hunk in built.Patch!.Hunks)
        {
            Assert.DoesNotContain("SET ANSI_NULLS", hunk.AnchorText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Script Date:", hunk.AnchorText, StringComparison.Ordinal);
        }

        PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(LiveDefinition(script)), built.Patch!);
        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome} - {attempt.Summary}");
    }
}
