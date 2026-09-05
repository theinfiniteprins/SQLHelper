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
    [GeneratedRegex(@"^\s*(CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVerb();

    /// <summary>Rewrites the leading verb to <c>CREATE OR ALTER</c> so the script applies whether or not the object exists.</summary>
    public static string AsCreateOrAlter(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return LeadingVerb().Replace(definition.TrimStart(), "CREATE OR ALTER $2", 1);
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
}
