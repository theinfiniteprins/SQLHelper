using Microsoft.Data.SqlClient;

namespace SqlHelper.Core.Execution;

/// <summary>
/// Recognises SQL Server errors that are safe to retry because nothing was committed: connection
/// drops, login throttling, transient network faults, deadlock victims. Command timeouts and
/// anything that might have partially run are deliberately <em>not</em> treated as transient.
/// </summary>
public static class TransientError
{
    private static readonly HashSet<int> RetryableNumbers =
    [
        // Connection / infrastructure
        233, 64, 20, 121, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920,
        4060, 4221,
        // Deadlock victim (the whole transaction is rolled back by the engine, so a retry is clean)
        1205,
    ];

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => RetryableNumbers.Contains(e.Number)),
        TimeoutException => false,
        _ => false,
    };
}
