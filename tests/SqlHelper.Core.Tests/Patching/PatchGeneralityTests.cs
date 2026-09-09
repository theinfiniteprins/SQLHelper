using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

/// <summary>
/// The real-procedure tests prove one procedure works. These prove the <em>rule</em> works, across
/// the shapes real code actually takes: procedures, functions, views, triggers; bodies that repeat
/// themselves; long uniform column lists where every line looks like its neighbours; dynamic SQL
/// full of quotes; block comments; CRLF and LF; and the SSMS habit of scripting an object as ALTER
/// when the server has it stored as CREATE.
///
/// One property is asserted everywhere, because it is the only one that matters against a
/// production database: applying a change either produces <em>exactly</em> the intended text, or
/// the client is reported as needing a hand edit. There is no third outcome.
/// </summary>
public sealed class PatchGeneralityTests
{
    private static DatabaseTarget Client(string name = "Acme") => new()
    {
        ClientName = name, ServerName = "sql-01", DatabaseName = "ClientDb", Environment = ServerEnvironment.Production,
    };

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string LiveDefinition(string script)
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(script);
        Assert.True(extraction.Ok, extraction.Error);
        return extraction.ModuleText;
    }

    private static ProgrammableObject Module(string definition, ObjectName name, ProgrammableObjectKind kind) => new(
        name, kind, definition,
        UsesAnsiNulls: true, UsesQuotedIdentifier: true, ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow, ModifiedServerTime: DateTime.UtcNow, IsEncrypted: false);

    /// <summary>
    /// Adds a line at every position in turn and checks the whole pipeline each time. Returns how
    /// many positions actually exercised the patcher.
    /// </summary>
    private static int SweepEveryInsertionPoint(
        string script, ObjectName name, ProgrammableObjectKind kind, string lineToAdd)
    {
        string live = LiveDefinition(script);
        string[] scriptLines = Lines(script);

        var refused = new List<int>();
        var wrong = new List<int>();
        int applied = 0;

        for (int at = 1; at < scriptLines.Length; at++)
        {
            var edited = new List<string>(scriptLines);
            edited.Insert(at, lineToAdd);
            string after = string.Join('\n', edited);

            PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("sweep", script, after);
            if (!built.Ok)
            {
                continue; // the line does not belong there - not a patching question
            }

            PatchAttempt attempt = PatchPlanner.Plan(Client(), Module(live, name, kind), built.Patch!);
            if (attempt.PatchedBody is null)
            {
                refused.Add(at);
                continue;
            }

            applied++;
            if (!string.Equals(Normalise(attempt.PatchedBody), Normalise(LiveDefinition(after)), StringComparison.Ordinal))
            {
                wrong.Add(at);
            }
        }

        Assert.Empty(wrong);
        Assert.Empty(refused);
        return applied;
    }

    // ------------------------------------------------------------------ shapes of object

    public static TheoryData<string, string, string, ProgrammableObjectKind, string> Objects()
    {
        var data = new TheoryData<string, string, string, ProgrammableObjectKind, string>();

        data.Add(
            "procedure",
            "dbo.usp_Simple",
            "CREATE PROCEDURE dbo.usp_Simple @id int AS\nBEGIN\n    SELECT [A] FROM dbo.T WHERE Id = @id\n    SELECT [B] FROM dbo.T WHERE Id = @id\n    SELECT [C] FROM dbo.T WHERE Id = @id\nEND",
            ProgrammableObjectKind.StoredProcedure,
            "    SELECT [Added] FROM dbo.T WHERE Id = @id");

        data.Add(
            "scalar function",
            "dbo.fn_Tax",
            "CREATE FUNCTION dbo.fn_Tax(@amt money)\nRETURNS money\nAS\nBEGIN\n    DECLARE @r money = @amt\n    SET @r = @r * 1.1\n    SET @r = @r * 1.2\n    RETURN @r\nEND",
            ProgrammableObjectKind.ScalarFunction,
            "    SET @r = @r * 1.3");

        data.Add(
            "table valued function",
            "dbo.tvf_Orders",
            "CREATE FUNCTION dbo.tvf_Orders(@id int)\nRETURNS TABLE\nAS\nRETURN\n(\n    SELECT [A], [B]\n    FROM dbo.Orders\n    WHERE Id = @id\n)",
            ProgrammableObjectKind.TableValuedFunction,
            "    AND [Active] = 1");

        data.Add(
            "view",
            "dbo.vw_Orders",
            "CREATE VIEW dbo.vw_Orders\nAS\nSELECT\n    [A],\n    [B],\n    [C]\nFROM dbo.Orders",
            ProgrammableObjectKind.View,
            "    [D],");

        data.Add(
            "trigger",
            "dbo.tr_Orders",
            "CREATE TRIGGER dbo.tr_Orders ON dbo.Orders\nAFTER INSERT\nAS\nBEGIN\n    SET NOCOUNT ON\n    UPDATE dbo.Orders SET [Touched] = 1\n    UPDATE dbo.Orders SET [Seen] = 1\nEND",
            ProgrammableObjectKind.Trigger,
            "    UPDATE dbo.Orders SET [Extra] = 1");

        return data;
    }

    [Theory]
    [MemberData(nameof(Objects))]
    public void Every_kind_of_programmable_object_patches_exactly(
        string label, string objectName, string body, ProgrammableObjectKind kind, string lineToAdd)
    {
        Assert.NotEmpty(label);
        int applied = SweepEveryInsertionPoint(body, ObjectName.Parse(objectName), kind, lineToAdd);
        Assert.True(applied > 0, $"{label}: the sweep never reached the patcher");
    }

    // ------------------------------------------------------------------ shapes of body

    /// <summary>A body whose middle repeats verbatim — the case that broke in production.</summary>
    private static string RepeatedBlockProcedure(int repeats)
    {
        var body = new System.Text.StringBuilder();
        body.Append("CREATE PROCEDURE dbo.usp_Repeat @id int AS\nBEGIN\n");
        for (int i = 0; i < repeats; i++)
        {
            body.Append("    IF @id > 0\n");
            body.Append("    BEGIN\n");
            body.Append("        SELECT [A] FROM dbo.T WHERE Id = @id\n");
            body.Append("        SELECT [B] FROM dbo.T WHERE Id = @id\n");
            body.Append("    END\n");
        }

        body.Append("END");
        return body.ToString();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void A_body_that_repeats_itself_many_times_still_patches_the_right_copy(int repeats)
    {
        string script = RepeatedBlockProcedure(repeats);
        int applied = SweepEveryInsertionPoint(
            script, ObjectName.Parse("dbo.usp_Repeat"), ProgrammableObjectKind.StoredProcedure,
            "        SELECT [Added] FROM dbo.T WHERE Id = @id");

        Assert.True(applied > 0);
    }

    [Fact]
    public void A_long_uniform_column_list_where_every_line_looks_alike_still_patches_exactly()
    {
        var body = new System.Text.StringBuilder("CREATE VIEW dbo.vw_Wide\nAS\nSELECT\n");
        for (int i = 1; i <= 60; i++)
        {
            body.Append($"    [Column{i}],\n");
        }

        body.Append("    [Last]\nFROM dbo.Wide");

        int applied = SweepEveryInsertionPoint(
            body.ToString(), ObjectName.Parse("dbo.vw_Wide"), ProgrammableObjectKind.View, "    [Inserted],");

        Assert.True(applied > 0);
    }

    [Fact]
    public void Dynamic_sql_full_of_quotes_is_patched_without_disturbing_the_literals()
    {
        const string body =
            "CREATE PROCEDURE dbo.usp_Dynamic @id int AS\n" +
            "BEGIN\n" +
            "    DECLARE @sql nvarchar(max) = N''\n" +
            "    SET @sql = @sql + N'SELECT * FROM dbo.T WHERE Id = ' + CAST(@id AS nvarchar(20))\n" +
            "    SET @sql = @sql + N' AND Name = ''O''''Brien'''\n" +
            "    SET @sql = @sql + N' -- not a comment, it is inside a literal'\n" +
            "    EXEC sp_executesql @sql\n" +
            "END";

        int applied = SweepEveryInsertionPoint(
            body, ObjectName.Parse("dbo.usp_Dynamic"), ProgrammableObjectKind.StoredProcedure,
            "    SET @sql = @sql + N' AND Extra = 1'");

        Assert.True(applied > 0);
    }

    [Fact]
    public void Block_comments_inside_the_body_are_carried_through_untouched()
    {
        const string body =
            "CREATE PROCEDURE dbo.usp_Commented AS\n" +
            "BEGIN\n" +
            "    /* step one\n" +
            "       keeps going */\n" +
            "    SELECT 1\n" +
            "    /* step two */\n" +
            "    SELECT 2\n" +
            "END";

        int applied = SweepEveryInsertionPoint(
            body, ObjectName.Parse("dbo.usp_Commented"), ProgrammableObjectKind.StoredProcedure, "    SELECT 3");

        Assert.True(applied > 0);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Either_line_ending_works_and_the_clients_own_ending_is_preserved(string ending)
    {
        string body = string.Join(ending,
            "CREATE PROCEDURE dbo.usp_Endings AS", "BEGIN", "    SELECT 1", "    SELECT 2", "    SELECT 3", "END");
        string after = string.Join(ending,
            "CREATE PROCEDURE dbo.usp_Endings AS", "BEGIN", "    SELECT 1", "    SELECT 99", "    SELECT 2", "    SELECT 3", "END");

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("t", body, after);
        Assert.True(built.Ok, built.Error);

        // Whatever the reference used, the client keeps its own line endings.
        foreach (string clientEnding in new[] { "\n", "\r\n" })
        {
            string live = LiveDefinition(string.Join(clientEnding,
                "CREATE PROCEDURE dbo.usp_Endings AS", "BEGIN", "    SELECT 1", "    SELECT 2", "    SELECT 3", "END"));

            PatchAttempt attempt = PatchPlanner.Plan(
                Client(), Module(live, ObjectName.Parse("dbo.usp_Endings"), ProgrammableObjectKind.StoredProcedure), built.Patch!);

            Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome}: {attempt.Summary}");
            Assert.Contains("SELECT 99", attempt.PatchedBody!, StringComparison.Ordinal);

            if (clientEnding == "\r\n")
            {
                Assert.DoesNotContain("\n\n", attempt.PatchedBody!.Replace("\r\n", "\r", StringComparison.Ordinal), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_reference_scripted_as_ALTER_patches_a_client_stored_as_CREATE()
    {
        // SSMS scripts an existing object as ALTER; the server may have it stored as CREATE.
        const string createBody =
            "CREATE PROCEDURE dbo.usp_Verb AS\nBEGIN\n    SELECT 1\n    SELECT 2\n    SELECT 3\nEND";
        string alterBefore = createBody.Replace("CREATE PROCEDURE", "ALTER PROCEDURE", StringComparison.Ordinal);
        string alterAfter = alterBefore.Replace("    SELECT 2", "    SELECT 2\n    SELECT 22", StringComparison.Ordinal);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("t", alterBefore, alterAfter);
        Assert.True(built.Ok, built.Error);

        PatchAttempt attempt = PatchPlanner.Plan(
            Client(), Module(createBody, ObjectName.Parse("dbo.usp_Verb"), ProgrammableObjectKind.StoredProcedure), built.Patch!);

        Assert.True(attempt.PatchedBody is not null, $"{attempt.Outcome}: {attempt.Summary}");
        Assert.Contains("SELECT 22", attempt.PatchedBody!, StringComparison.Ordinal);

        // The client's own verb is not rewritten by the patch itself.
        Assert.StartsWith("CREATE PROCEDURE", attempt.PatchedBody!.TrimStart(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ many random changes

    [Fact]
    public void Across_hundreds_of_generated_procedures_and_edits_the_result_is_exact_or_refused()
    {
        var random = new Random(20260909); // fixed seed: a failure here is reproducible
        int exercised = 0;
        var failures = new List<string>();

        for (int iteration = 0; iteration < 250; iteration++)
        {
            string script = GenerateProcedure(random);
            string[] lines = Lines(script);

            // One to four scattered edits, the way a real out-of-band fix looks. Never ask for
            // more distinct positions than the procedure actually has room for.
            var edited = new List<string>(lines);
            int room = Math.Max(1, lines.Length - 4);
            int edits = 1 + random.Next(Math.Min(4, room));
            var positions = new SortedSet<int>();
            for (int tries = 0; tries < 100 && positions.Count < edits; tries++)
            {
                positions.Add(2 + random.Next(room));
            }

            foreach (int at in positions.Reverse())
            {
                if (at >= edited.Count)
                {
                    continue;
                }

                edited.Insert(at, $"        SELECT [Added{at}] FROM dbo.T");
            }

            string after = string.Join('\n', edited);

            PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("gen", script, after);
            if (!built.Ok)
            {
                continue;
            }

            string live = LiveDefinition(script);
            PatchAttempt attempt = PatchPlanner.Plan(
                Client(), Module(live, ObjectName.Parse("dbo.usp_Gen"), ProgrammableObjectKind.StoredProcedure), built.Patch!);

            if (attempt.PatchedBody is null)
            {
                failures.Add($"iteration {iteration}: refused on an identical client ({attempt.Summary})");
                continue;
            }

            exercised++;
            if (!string.Equals(Normalise(attempt.PatchedBody), Normalise(LiveDefinition(after)), StringComparison.Ordinal))
            {
                failures.Add($"iteration {iteration}: applied but the text is not what was intended");
            }
        }

        Assert.True(exercised > 100, $"only {exercised} generated cases reached the patcher");
        Assert.Empty(failures);
    }

    /// <summary>A random procedure with repeated blocks, uniform column lists and comments.</summary>
    private static string GenerateProcedure(Random random)
    {
        var body = new System.Text.StringBuilder("CREATE PROCEDURE dbo.usp_Gen @id int AS\nBEGIN\n");

        int sections = 2 + random.Next(4);
        for (int s = 0; s < sections; s++)
        {
            switch (random.Next(4))
            {
                case 0: // a block repeated verbatim
                    int repeats = 2 + random.Next(3);
                    for (int r = 0; r < repeats; r++)
                    {
                        body.Append("    IF @id > 0\n    BEGIN\n        SELECT [A] FROM dbo.T\n        SELECT [B] FROM dbo.T\n    END\n");
                    }

                    break;

                case 1: // a uniform column list
                    body.Append("    SELECT\n");
                    int columns = 3 + random.Next(20);
                    for (int c = 1; c <= columns; c++)
                    {
                        body.Append($"        [Column{c}],\n");
                    }

                    body.Append("        [Tail]\n    FROM dbo.T\n");
                    break;

                case 2: // comments
                    body.Append("    -- a note\n    /* a longer\n       note */\n    SELECT 1\n");
                    break;

                default:
                    body.Append($"    SELECT [S{s}] FROM dbo.T WHERE Id = @id\n");
                    break;
            }
        }

        body.Append("END");
        return body.ToString();
    }
}
