using System.Text;
using System.Text.RegularExpressions;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Backup;

/// <summary>
/// Wraps a stored module definition into a runnable script with the correct <c>SET</c> options
/// and batch separators — the form SSMS would produce, and the form a rollback needs.
/// </summary>
public static partial class ModuleScript
{
    [GeneratedRegex(@"\G(CREATE\s+OR\s+ALTER|CREATE|ALTER)(\s+)(PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbAt();

    /// <summary>
    /// Rewrites the leading verb to <c>CREATE OR ALTER</c> so the script applies whether or not the
    /// object exists.
    ///
    /// The verb is rarely the first thing in the text: a definition read back from
    /// <c>sys.sql_modules</c> keeps whatever header comment the author wrote above
    /// <c>CREATE PROCEDURE</c>, and those comments must survive. So the comments (and any string
    /// literals, which can contain anything) are skipped over to find where the statement really
    /// begins, and only the verb itself is rewritten.
    /// </summary>
    public static string AsCreateOrAlter(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        int verbStart = FindStatementStart(definition);
        if (verbStart < 0)
        {
            return definition;
        }

        Match match = VerbAt().Match(definition, verbStart);
        if (!match.Success)
        {
            return definition;
        }

        // Already CREATE OR ALTER: leave the author's own spacing alone.
        if (match.Groups[1].Value.Contains("OR", StringComparison.OrdinalIgnoreCase))
        {
            return definition;
        }

        return string.Concat(
            definition.AsSpan(0, match.Index),
            "CREATE OR ALTER",
            definition.AsSpan(match.Index + match.Groups[1].Length));
    }

    /// <summary>True when the text carries a leading verb this can rewrite. Checked before firing anything.</summary>
    public static bool CanRewriteVerb(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int start = FindStatementStart(definition);
        return start >= 0 && VerbAt().IsMatch(definition, start);
    }

    /// <summary>Full runnable script: SET options, then the definition, each in its own batch.</summary>
    public static string ToRunnableScript(ProgrammableObject module, bool forceCreateOrAlter = true)
    {
        ArgumentNullException.ThrowIfNull(module);

        string body = forceCreateOrAlter ? AsCreateOrAlter(module.Definition) : module.Definition.Trim();

        var builder = new StringBuilder();
        builder.Append("SET ANSI_NULLS ").Append(module.UsesAnsiNulls ? "ON" : "OFF").Append("\r\nGO\r\n");
        builder.Append("SET QUOTED_IDENTIFIER ").Append(module.UsesQuotedIdentifier ? "ON" : "OFF").Append("\r\nGO\r\n");
        builder.Append(body.TrimEnd()).Append("\r\nGO\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// Index of the first character of real code, skipping whitespace, <c>--</c> line comments and
    /// <c>/* */</c> block comments (which nest in T-SQL). Returns -1 when there is nothing but
    /// comments. Deliberately hand-written rather than parsed: it must also work on a definition
    /// ScriptDom cannot parse, and it must never rewrite anything inside a comment.
    /// </summary>
    private static int FindStatementStart(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int newline = text.IndexOf('\n', i);
                if (newline < 0)
                {
                    return -1;
                }

                i = newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            return i;
        }

        return -1;
    }
}
