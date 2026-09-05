using System.Text.RegularExpressions;

namespace SqlHelper.Core.Auditing;

/// <summary>
/// Strips credentials out of a statement before it is written to the audit log.
///
/// The audit trail records what was run, verbatim, which is exactly what you want for a change
/// you have to answer for later — except when the statement itself carries a secret. A
/// <c>CREATE LOGIN ... WITH PASSWORD = '...'</c> run across every client would otherwise leave
/// that password sitting in a plain JSON file on disk. The statement is still recorded in full;
/// only the literal after a password-ish keyword is replaced.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "'***redacted***'";

    /// <summary>Returns the statement with any inline credential literal replaced.</summary>
    public static string? Scrub(string? statement)
    {
        if (string.IsNullOrEmpty(statement))
        {
            return statement;
        }

        return CredentialLiteral().Replace(statement, m => m.Groups["kw"].Value + Placeholder);
    }

    // PASSWORD / SECRET / PWD (and the HASHED / OLD_PASSWORD variants) followed by = and a
    // literal — quoted, N-prefixed, or a 0x binary hash.
    [GeneratedRegex(
        @"(?<kw>\b(?:OLD_PASSWORD|PASSWORD|SECRET|PWD)\b\s*=\s*)(?:N?'(?:[^']|'')*'|0x[0-9A-Fa-f]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialLiteral();
}
