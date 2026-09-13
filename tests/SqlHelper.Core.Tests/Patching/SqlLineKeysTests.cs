using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

/// <summary>
/// The key decides what counts as "the same line". Getting it too strict refuses changes that
/// should apply; getting it too loose would let a line that does something different pass as a
/// match — on a production database, the one failure that is not acceptable. Both directions are
/// tested, and the second far more heavily.
/// </summary>
public sealed class SqlLineKeysTests
{
    private static bool Same(string a, string b) => SqlLineKeys.Of(a) == SqlLineKeys.Of(b);

    // ------------------------------------------------------------ formatting is ignored

    [Theory]
    [InlineData("\t\tAND\t\t[dbo].[T].[Col] = 0", "\t\tAND\t\t\t[dbo].[T].[Col] = 0")]   // the reported case: one extra tab
    [InlineData("AND [x] = 1", "AND\t[x]\t=\t1")]
    [InlineData("    SELECT a, b, c", "SELECT a,b,c")]
    [InlineData("WHERE Id = @id", "WHERE Id=@id")]
    [InlineData("IF ( @a > 0 )", "IF (@a>0)")]
    [InlineData("SET @x = @x + 1 ;", "SET @x=@x+1;")]
    [InlineData("DECLARE @t TABLE ( id int )", "DECLARE @t TABLE (id int)")]
    [InlineData("COALESCE([a] , [b])", "COALESCE([a],[b])")]
    [InlineData("SELECT a  -- note", "SELECT a -- note")]
    [InlineData("--\tModified by A. Developer", "-- Modified by A. Developer")]
    [InlineData("/*  block  comment  */", "/* block comment */")]
    [InlineData("", "   \t  ")]
    public void Lines_that_differ_only_in_formatting_are_the_same(string a, string b)
    {
        Assert.True(Same(a, b), $"expected [{SqlLineKeys.Of(a)}] == [{SqlLineKeys.Of(b)}]");
    }

    // ------------------------------------------------------------ meaning is never ignored

    [Theory]
    [InlineData("SELECT 'a b'", "SELECT 'a  b'")]              // inside a string literal
    [InlineData("SELECT N'x'", "SELECT N 'x'")]                // unicode literal vs column N aliased 'x'
    [InlineData("SELECT a b", "SELECT ab")]                    // alias vs one column
    [InlineData("SELECT [a b]", "SELECT [a  b]")]              // bracketed identifier
    [InlineData("SELECT \"a b\"", "SELECT \"a  b\"")]          // quoted identifier / literal
    [InlineData("x = a - -1", "x = a --1")]                    // minus minus vs comment
    [InlineData("x = a / *b", "x = a /*b")]                    // divide-pointer vs comment
    [InlineData("SET @a + = 1", "SET @a += 1")]                // compound assignment
    [InlineData("a < > b", "a <> b")]
    [InlineData("a < < 2", "a << 2")]                          // bit shift (SQL Server 2022)
    [InlineData("a | | b", "a || b")]                          // concatenation (SQL Server 2025)
    [InlineData("SELECT 'x' 'y'", "SELECT 'x''y'")]        // two literals vs one containing a quote
    [InlineData("1 e5", "1e5")]                                // column alias vs float literal
    [InlineData("dbo . t", "dbo.t")]                           // treated as different: conservative
    [InlineData("SELECT 1", "SELECT 2")]
    [InlineData("IS NULL", "IS NOT NULL")]
    [InlineData("AND x = 1", "OR x = 1")]
    public void Lines_that_could_mean_something_different_are_never_the_same(string a, string b)
    {
        Assert.False(Same(a, b), $"[{a}] and [{b}] must not be treated as the same line");
    }

    // ------------------------------------------------------------ state across lines

    [Fact]
    public void Whitespace_inside_a_string_literal_that_spans_lines_is_significant_on_every_line()
    {
        string[] a = ["SET @sql = 'SELECT *", "    FROM  dbo.T", "  WHERE x = 1'"];
        string[] b = ["SET @sql = 'SELECT *", "  FROM dbo.T", "  WHERE x = 1'"];

        string[] ka = SqlLineKeys.Compute(a);
        string[] kb = SqlLineKeys.Compute(b);

        Assert.Equal(ka[0], kb[0]);
        Assert.NotEqual(ka[1], kb[1]); // this line is inside the literal
    }

    [Fact]
    public void A_block_comment_spanning_lines_is_known_to_be_a_comment_on_every_line()
    {
        SqlLineKeys.Analysis analysis = SqlLineKeys.Analyse(["SELECT 1 /* start", "   middle   text", "end */ SELECT 2"]);

        Assert.True(analysis.StartsInCode[0]);
        Assert.False(analysis.StartsInCode[1]);
        Assert.False(analysis.StartsInCode[2]);
        Assert.Equal("middletext", analysis.Keys[1]);
    }

    [Fact]
    public void Doubled_quotes_do_not_end_a_literal_early()
    {
        SqlLineKeys.Analysis analysis = SqlLineKeys.Analyse(["SET @s = 'O''Brien", "  still inside'", "SELECT  1"]);

        Assert.False(analysis.StartsInCode[1]);
        Assert.True(analysis.StartsInCode[2]);
        Assert.Equal("SELECT 1", analysis.Keys[2]);
    }

    [Fact]
    public void A_line_comment_does_not_carry_into_the_next_line()
    {
        SqlLineKeys.Analysis analysis = SqlLineKeys.Analyse(["SELECT 1 -- it's a comment", "SELECT   2"]);

        Assert.True(analysis.StartsInCode[1]);
        Assert.Equal("SELECT 2", analysis.Keys[1]);
    }

    [Fact]
    public void Nested_block_comments_close_at_the_right_place()
    {
        SqlLineKeys.Analysis analysis = SqlLineKeys.Analyse(["/* a /* b */ still", "comment */", "SELECT  1"]);

        Assert.False(analysis.StartsInCode[1]);
        Assert.True(analysis.StartsInCode[2]);
    }

    // ------------------------------------------------------------ the no-false-match property

    [Theory]
    [InlineData(4242)]
    [InlineData(7)]
    [InlineData(99991)]
    [InlineData(20260913)]
    public void Removing_the_whitespace_the_key_ignores_never_changes_how_the_line_tokenises(int seed)
    {
        // For many generated lines: any two lines with the same key must tokenise identically once
        // comments and whitespace are set aside. This is the guarantee the matcher relies on.
        var random = new Random(seed);
        string[] tokens =
        [
            "SELECT", "a", "b", "@x", "1", "e5", "N", "'s t'", "'x'", "[c d]", "(", ")", ",", ";", "=", "-", "+", "*",
            "/", "<", ">", "!", ".", "dbo", "AND", "OR", "0x1F", "--c", "/*c*/", "+=", "<>", ">=", "<<", ">>", "||", "''", "\"q\"", ":", "::", "%", "&", "|", "^", "~", "#t", "$p",
        ];
        string[] gaps = ["", " ", "  ", "\t", " \t "];

        int checkedPairs = 0;
        for (int n = 0; n < 40000; n++)
        {
            var parts = Enumerable.Range(0, 2 + random.Next(6)).Select(_ => tokens[random.Next(tokens.Length)]).ToList();
            string a = Render(parts, gaps, random);
            string b = Render(parts, gaps, random);

            if (!Same(a, b))
            {
                continue;
            }

            checkedPairs++;
            Assert.True(
                Tokenise(a).SequenceEqual(Tokenise(b)),
                $"[{a}] and [{b}] share a key but tokenise differently");
        }

        Assert.True(checkedPairs > 500);
    }

    private static string Render(List<string> parts, string[] gaps, Random random)
    {
        var text = new System.Text.StringBuilder();
        foreach (string part in parts)
        {
            text.Append(gaps[random.Next(gaps.Length)]).Append(part);
        }

        return text.ToString();
    }

    /// <summary>Tokens as ScriptDom sees them, with whitespace and comments set aside.</summary>
    private static IEnumerable<string> Tokenise(string text)
    {
        var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true);
        using var reader = new StringReader(text);
        IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> stream = parser.GetTokenStream(reader, out _);

        return stream
            .Where(t => t.TokenType is not (
                Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.WhiteSpace
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.SingleLineComment
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.MultilineComment
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.EndOfFile))
            .Select(t => $"{t.TokenType}:{t.Text}")
            .ToList();
    }
}
