using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHelper.Core.Guard;

/// <summary>One GO-delimited batch from a script.</summary>
/// <param name="Text">The batch text, trimmed of surrounding blank lines.</param>
/// <param name="RepeatCount">From <c>GO &lt;n&gt;</c>; 1 when no count is given.</param>
/// <param name="StartLine">1-based line in the original script where the batch begins.</param>
public sealed record ScriptBatch(string Text, int RepeatCount, int StartLine);

/// <summary>
/// Splits a T-SQL script on <c>GO</c> batch separators. <c>GO</c> is an SSMS/sqlcmd convention,
/// not T-SQL — ADO.NET rejects a multi-batch script — so any script with <c>CREATE/ALTER</c> of
/// more than one object, or the usual <c>SET ANSI_NULLS … GO</c> preamble, must be split first.
/// Splitting is done from the ScriptDom <em>token stream</em>, so a <c>GO</c> inside a string or
/// comment is never mistaken for a separator.
/// </summary>
public static class GoBatchSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script, TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Length == 0)
        {
            return [];
        }

        TSqlParser parser = TSqlParsing.CreateParser(level);
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(script))
        {
            tokens = parser.GetTokenStream(reader, out _);
        }

        var batches = new List<ScriptBatch>();
        int segmentStart = 0;
        int segmentStartLine = 1;

        for (int i = 0; i < tokens.Count; i++)
        {
            TSqlParserToken token = tokens[i];

            if (token.TokenType == TSqlTokenType.EndOfFile)
            {
                Emit(script[segmentStart..], segmentStartLine, repeat: 1);
                break;
            }

            if (token.TokenType != TSqlTokenType.Go)
            {
                continue;
            }

            Emit(script[segmentStart..token.Offset], segmentStartLine, ReadRepeatCount(tokens, i, token.Line));

            int afterGo = token.Offset + token.Text.Length;
            int newline = script.IndexOf('\n', afterGo);
            segmentStart = newline < 0 ? script.Length : newline + 1;
            segmentStartLine = token.Line + 1;
        }

        return batches;

        void Emit(string text, int line, int repeat)
        {
            string trimmed = text.Trim();
            if (trimmed.Length != 0)
            {
                batches.Add(new ScriptBatch(trimmed, repeat, line));
            }
        }
    }

    private static int ReadRepeatCount(IList<TSqlParserToken> tokens, int goIndex, int goLine)
    {
        for (int j = goIndex + 1; j < tokens.Count; j++)
        {
            TSqlParserToken next = tokens[j];
            if (next.TokenType is TSqlTokenType.WhiteSpace)
            {
                continue;
            }

            if (next.TokenType == TSqlTokenType.Integer
                && next.Line == goLine
                && int.TryParse(next.Text, out int count)
                && count > 0)
            {
                return count;
            }

            break;
        }

        return 1;
    }
}
