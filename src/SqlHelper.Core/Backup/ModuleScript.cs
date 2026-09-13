using System.Text;
using System.Text.RegularExpressions;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Backup;

/// <summary>The verb a module script is run with.</summary>
public enum ModuleVerb
{
    /// <summary>For an object that does not exist yet.</summary>
    Create,

    /// <summary>For an object that already exists — what SSMS's "Modify" and "Script as ALTER" use.</summary>
    Alter,
}

/// <summary>
/// Wraps a stored module definition into a runnable script with the correct <c>SET</c> options
/// and batch separators — the form SSMS would produce, and the form a rollback needs.
///
/// Why never <c>CREATE OR ALTER</c>: SQL Server does not store the verb that was run. It rewrites it
/// to <c>CREATE</c> and keeps every other character exactly — so <c>ALTER PROCEDURE</c> is stored as
/// <c>CREATE PROCEDURE</c>, but <c>CREATE OR ALTER PROCEDURE</c> is stored as
/// <c>CREATE···PROCEDURE</c>: the words "OR ALTER" vanish and the two spaces around them stay.
/// Every deployment through <c>CREATE OR ALTER</c> therefore added two spaces after the verb, and
/// they piled up — one, three, five — on production objects. Running <c>ALTER</c> for an existing
/// object and <c>CREATE</c> for a new one stores exactly what was reviewed, the same as SSMS does.
/// </summary>
public static partial class ModuleScript
{
    [GeneratedRegex(@"\G(CREATE\s+OR\s+ALTER|CREATE|ALTER)(\s+)(PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbAt();

    /// <summary>
    /// Replaces the leading verb with <paramref name="verb"/>, touching nothing else.
    ///
    /// The one exception is the gap between the verb and the object keyword: when it is spaces or
    /// tabs on one line, it becomes a single space. That undoes the extra spaces earlier
    /// <c>CREATE OR ALTER</c> deployments left behind, instead of carrying them forward forever. A gap
    /// that contains a line break is left exactly as it is.
    ///
    /// The verb is rarely the first thing in the text: a definition read back from
    /// <c>sys.sql_modules</c> keeps whatever header comment the author wrote above it, and those
    /// comments must survive. So comments are skipped to find where the statement really begins.
    /// </summary>
    public static string WithVerb(string definition, ModuleVerb verb)
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

        Group gap = match.Groups[2];
        string newGap = gap.Value.Contains('\n', StringComparison.Ordinal) || gap.Value.Contains('\r', StringComparison.Ordinal)
            ? gap.Value
            : " ";

        return string.Concat(
            definition.AsSpan(0, match.Index),
            verb == ModuleVerb.Alter ? "ALTER" : "CREATE",
            newGap,
            definition.AsSpan(gap.Index + gap.Length));
    }

    /// <summary>
    /// What <c>sys.sql_modules</c> will hold once <paramref name="definition"/> has been run: SQL Server
    /// stores the text with its verb rewritten to <c>CREATE</c> and nothing else changed. Used to
    /// confirm, before committing, that the server holds exactly what was reviewed.
    /// </summary>
    public static string ExpectedStoredDefinition(string definition) => WithVerb(definition, ModuleVerb.Create);

    /// <summary>True when the text carries a leading verb this can rewrite. Checked before firing anything.</summary>
    public static bool CanRewriteVerb(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int start = FindStatementStart(definition);
        return start >= 0 && VerbAt().IsMatch(definition, start);
    }

    /// <summary>Full runnable script: SET options, then the definition, each in its own batch.</summary>
    public static string ToRunnableScript(ProgrammableObject module, ModuleVerb verb = ModuleVerb.Alter)
    {
        ArgumentNullException.ThrowIfNull(module);

        string body = WithVerb(module.Definition, verb);

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
