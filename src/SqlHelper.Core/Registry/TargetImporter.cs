using SqlHelper.Core.Model;

namespace SqlHelper.Core.Registry;

/// <summary>One row parsed from a pasted or imported grid, before it becomes a <see cref="DatabaseTarget"/>.</summary>
public sealed record ImportedTargetRow(
    string ClientName,
    string ServerName,
    string DatabaseName,
    AuthMode AuthMode,
    string? UserId,
    ServerEnvironment Environment,
    IReadOnlyList<string> Warnings,
    bool TrustServerCertificate = false)
{
    public DatabaseTarget ToTarget() => new()
    {
        ClientName = ClientName,
        ServerName = ServerName,
        DatabaseName = DatabaseName,
        AuthMode = AuthMode,
        UserId = string.IsNullOrWhiteSpace(UserId) ? null : UserId,
        Environment = Environment,
        TrustServerCertificate = TrustServerCertificate,
    };
}

/// <summary>
/// Parses a block of tab-separated text — exactly what lands on the clipboard when an operator
/// copies a range out of Excel — into target rows. Columns, in order: Client, Server, Database,
/// Auth (Windows/Sql), User, Environment, Trust certificate (yes/no). A header row is detected and
/// skipped automatically; trailing columns may be omitted.
/// </summary>
public static class TargetImporter
{
    public static IReadOnlyList<ImportedTargetRow> ParseTabSeparated(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rows = new List<ImportedTargetRow>();

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            string[] cells = rawLine.Split('\t');
            if (rows.Count == 0 && LooksLikeHeader(cells))
            {
                continue;
            }

            rows.Add(ParseRow(cells));
        }

        return rows;
    }

    private static bool LooksLikeHeader(string[] cells) =>
        cells.Length > 0 && cells[0].Trim().Equals("client", StringComparison.OrdinalIgnoreCase);

    private static ImportedTargetRow ParseRow(string[] cells)
    {
        var warnings = new List<string>();

        string client = Cell(cells, 0);
        string server = Cell(cells, 1);
        string database = Cell(cells, 2);

        if (client.Length == 0)
        {
            warnings.Add("Client name is blank.");
        }

        if (server.Length == 0)
        {
            warnings.Add("Server name is blank.");
        }

        if (database.Length == 0)
        {
            warnings.Add("Database name is blank.");
        }

        AuthMode authMode = ParseAuthMode(Cell(cells, 3), warnings);
        string? userId = Cell(cells, 4) is { Length: > 0 } u ? u : null;
        ServerEnvironment environment = ParseEnvironment(Cell(cells, 5), warnings);

        if (authMode == AuthMode.SqlLogin && userId is null)
        {
            warnings.Add("SQL login selected but no user id given.");
        }

        bool trustCertificate = ParseYesNo(Cell(cells, 6));

        return new ImportedTargetRow(client, server, database, authMode, userId, environment, warnings, trustCertificate);
    }

    /// <summary>Accepts the shapes people actually type in a spreadsheet for a yes/no column.</summary>
    private static bool ParseYesNo(string text) => text.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "1" or "x" or "trust" => true,
        _ => false,
    };

    private static string Cell(string[] cells, int index) => index < cells.Length ? cells[index].Trim() : string.Empty;

    private static AuthMode ParseAuthMode(string text, List<string> warnings)
    {
        if (text.Length == 0)
        {
            return AuthMode.WindowsIntegrated;
        }

        if (text.Contains("sql", StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.SqlLogin;
        }

        if (text.Contains("win", StringComparison.OrdinalIgnoreCase) || text.Contains("integrated", StringComparison.OrdinalIgnoreCase) || text.Contains("trusted", StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.WindowsIntegrated;
        }

        warnings.Add($"Unrecognised auth mode '{text}' — defaulted to Windows.");
        return AuthMode.WindowsIntegrated;
    }

    private static ServerEnvironment ParseEnvironment(string text, List<string> warnings)
    {
        if (text.Length == 0)
        {
            return ServerEnvironment.Production;
        }

        foreach (ServerEnvironment candidate in Enum.GetValues<ServerEnvironment>())
        {
            if (candidate.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        warnings.Add($"Unrecognised environment '{text}' — defaulted to Production.");
        return ServerEnvironment.Production;
    }
}
