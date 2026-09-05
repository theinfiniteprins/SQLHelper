namespace SqlHelper.App.ViewModels;

/// <summary>One database's outcome on the Update/Delete screen.</summary>
public sealed class NonQueryResultRow
{
    public required string ClientName { get; init; }

    public required bool Success { get; init; }

    public int RowsAffected { get; init; }

    public bool WasRolledBack { get; init; }

    public string? Error { get; init; }

    public string Status => !Success ? "Failed" : WasRolledBack ? "Dry run" : "Committed";
}
