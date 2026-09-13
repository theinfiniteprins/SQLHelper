using System.Text;

namespace SqlHelper.Core.Patching;

/// <summary>
/// Comparison keys for lines of T-SQL that ignore formatting but never ignore meaning.
///
/// Two copies of the same procedure on different clients are rarely byte-identical: someone
/// re-aligned a column list with tabs, or indented a block differently. Comparing lines exactly —
/// or trimming only their ends, which is all DiffPlex's "ignore whitespace" does — treats those
/// copies as different, so a change that should apply is refused, and a tab added mid-line shows
/// up as a difference even with "Ignore whitespace" ticked.
///
/// Whitespace is not always formatting, though, so the key is built by actually lexing the text:
///
///   • Inside a string literal, a [bracketed] or "quoted" identifier, every character is kept.
///     <c>'a  b'</c> and <c>'a b'</c> are different values.
///   • In comments, whitespace is ignored entirely — a comment is not code.
///   • In code, whitespace is dropped where it cannot change how the text tokenises (next to
///     <c>( ) , ; = + &lt; &gt;</c> and similar) and kept as a single space where it can:
///     between two words (<c>SELECT a b</c> is not <c>SELECT ab</c>), before a quote
///     (<c>N 'x'</c> is not <c>N'x'</c>), around a dot, and between characters that would fuse into
///     a different token (<c>- -</c> is not <c>--</c>, <c>/ *</c> is not <c>/*</c>).
///
/// Lexer state carries across lines — a string literal or block comment can span many — so keys
/// are only correct when computed over text that starts in ordinary code. Callers that key a
/// fragment make sure the fragment starts there (see <see cref="Analyse"/>).
/// </summary>
public static class SqlLineKeys
{
    private enum State
    {
        Code,
        String,
        Bracket,
        DoubleQuote,
        BlockComment,
    }

    /// <summary>Keys for each line, and whether each line begins in ordinary code rather than inside a literal or comment.</summary>
    public sealed record Analysis(IReadOnlyList<string> Keys, IReadOnlyList<bool> StartsInCode);

    public static string[] Compute(IReadOnlyList<string> lines) => [.. Analyse(lines).Keys];

    /// <summary>The key of a single line, assuming it starts in ordinary code.</summary>
    public static string Of(string line) => Compute([line])[0];

    public static Analysis Analyse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var keys = new string[lines.Count];
        var startsInCode = new bool[lines.Count];

        State state = State.Code;
        int commentDepth = 0;
        var key = new StringBuilder();

        for (int l = 0; l < lines.Count; l++)
        {
            string line = lines[l] ?? string.Empty;
            startsInCode[l] = state == State.Code;
            key.Clear();

            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];

                switch (state)
                {
                    case State.String:
                        key.Append(c);
                        if (c == '\'')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '\'')
                            {
                                key.Append('\'');
                                i += 2;
                                continue;
                            }

                            state = State.Code;
                        }

                        i++;
                        continue;

                    case State.Bracket:
                        key.Append(c);
                        if (c == ']')
                        {
                            if (i + 1 < line.Length && line[i + 1] == ']')
                            {
                                key.Append(']');
                                i += 2;
                                continue;
                            }

                            state = State.Code;
                        }

                        i++;
                        continue;

                    case State.DoubleQuote:
                        key.Append(c);
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                key.Append('"');
                                i += 2;
                                continue;
                            }

                            state = State.Code;
                        }

                        i++;
                        continue;

                    case State.BlockComment:
                        if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                        {
                            commentDepth++;
                            key.Append("/*");
                            i += 2;
                            continue;
                        }

                        if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                        {
                            commentDepth--;
                            key.Append("*/");
                            i += 2;
                            if (commentDepth == 0)
                            {
                                state = State.Code;
                            }

                            continue;
                        }

                        i = AppendCommentChar(key, line, i);
                        continue;
                }

                // ---- ordinary code ----
                if (char.IsWhiteSpace(c))
                {
                    int next = i;
                    while (next < line.Length && char.IsWhiteSpace(line[next]))
                    {
                        next++;
                    }

                    if (key.Length > 0 && next < line.Length && IsSignificantGap(key[^1], line[next], line, next))
                    {
                        key.Append(' ');
                    }

                    i = next;
                    continue;
                }

                if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    // Line comment: to the end of the line, with its whitespace collapsed.
                    key.Append("--");
                    i += 2;
                    while (i < line.Length)
                    {
                        i = AppendCommentChar(key, line, i);
                    }

                    TrimTrailingSpace(key);
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    state = State.BlockComment;
                    commentDepth = 1;
                    key.Append("/*");
                    i += 2;
                    continue;
                }

                key.Append(c);
                state = c switch
                {
                    '\'' => State.String,
                    '[' => State.Bracket,
                    '"' => State.DoubleQuote,
                    _ => State.Code,
                };
                i++;
            }

            if (state is State.Code or State.BlockComment)
            {
                TrimTrailingSpace(key);
            }

            keys[l] = key.ToString();
        }

        return new Analysis(keys, startsInCode);
    }

    /// <summary>
    /// Comment text, with its whitespace removed entirely. A comment has no meaning to the server,
    /// so "--	Modified by" and "-- Modified by" are the same line for matching purposes.
    /// </summary>
    private static int AppendCommentChar(StringBuilder key, string line, int i)
    {
        char c = line[i];
        if (!char.IsWhiteSpace(c))
        {
            key.Append(c);
        }

        return i + 1;
    }

    private static void TrimTrailingSpace(StringBuilder key)
    {
        while (key.Length > 0 && key[^1] == ' ')
        {
            key.Length--;
        }
    }

    /// <summary>
    /// Whether whitespace between <paramref name="before"/> and <paramref name="after"/> can change
    /// the meaning of the text. When in doubt this says yes: calling a formatting difference
    /// significant only means two lines fail to match, whereas the reverse could match two lines
    /// that do different things.
    ///
    /// The rule, by character class:
    ///   • Next to structure — <c>( ) , ; [ ] { }</c> — whitespace never matters.
    ///   • Between an operator character and anything else, it never matters: <c>a = b</c>, <c>a=b</c>.
    ///   • Between two operator characters it always matters: they may fuse into another operator
    ///     (<c>&lt; &lt;</c> vs the <c>&lt;&lt;</c> shift, <c>- -</c> vs a <c>--</c> comment, <c>+ =</c> vs <c>+=</c>).
    ///   • Between two other characters — words, numbers, variables, quotes, dots — it always
    ///     matters: <c>a b</c> vs <c>ab</c>, <c>N 'x'</c> vs <c>N'x'</c>, <c>'x' 'y'</c> vs <c>'x''y'</c>.
    /// </summary>
    private static bool IsSignificantGap(char before, char after, string line, int afterIndex)
    {
        _ = line;
        _ = afterIndex;

        if (IsStructural(before) || IsStructural(after))
        {
            return false;
        }

        return IsOperator(before) == IsOperator(after);
    }

    private static bool IsStructural(char c) => c is '(' or ')' or ',' or ';' or '[' or ']' or '{' or '}';

    private static bool IsOperator(char c) =>
        c is '<' or '>' or '=' or '!' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '~';

    // ':' is deliberately not an operator here: "name:" lexes as a GOTO label, "name :" does not.
}
