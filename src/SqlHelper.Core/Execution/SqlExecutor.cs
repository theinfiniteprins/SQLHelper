using System.Data;
using Microsoft.Data.SqlClient;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Execution;

/// <summary>Result of probing a target's connectivity and effective permissions.</summary>
public sealed record ConnectionProbe(
    bool Ok,
    string? ServerVersion,
    string? DatabaseName,
    string? LoginName,
    bool CanWrite,
    string? Error,
    TimeSpan Duration);

/// <summary>
/// Runs SQL against a single database. All methods open a fresh connection via the injected
/// <see cref="ITargetConnectionFactory"/> and dispose it before returning.
/// </summary>
public sealed class SqlExecutor
{
    private readonly ITargetConnectionFactory _connections;

    public SqlExecutor(ITargetConnectionFactory connections)
        => _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>Runs a read query. By default wrapped in a rolled-back transaction as a safety net.</summary>
    public async Task<QueryResult> ExecuteQueryAsync(
        DatabaseTarget target,
        string sql,
        QueryExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        options ??= QueryExecutionOptions.Default;

        var result = new QueryResult();

        await using SqlConnection connection = await _connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        void OnInfo(object _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                result.Messages.Add(error.Message);
            }
        }

        connection.InfoMessage += OnInfo;
        try
        {
            SqlTransaction? transaction = options.WrapInRollbackTransaction
                ? (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
                : null;

            await using (transaction)
            {
                await using SqlCommand command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = target.CommandTimeoutSeconds;
                command.Transaction = transaction;

                // Close the reader before touching the transaction — MARS is off, so an open
                // reader blocks the rollback below.
                await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    do
                    {
                        if (reader.FieldCount == 0)
                        {
                            continue;
                        }

                        result.Tables.Add(await ReadTableAsync(reader, options.MaxRows, cancellationToken).ConfigureAwait(false));
                    }
                    while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                }

                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }

        return result;
    }

    /// <summary>Runs an INSERT/UPDATE/DELETE script batch by batch, reporting rows affected. Honours dry-run and transaction options.</summary>
    public async Task<NonQueryResult> ExecuteNonQueryAsync(
        DatabaseTarget target,
        string script,
        NonQueryExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        options ??= NonQueryExecutionOptions.Default;

        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split(script, options.Compatibility);
        if (batches.Count == 0)
        {
            throw new InvalidOperationException("The script contains no executable statements.");
        }

        bool useTransaction = options.DryRun || options.TransactionMode == NonQueryTransactionMode.WholeScript;
        var result = new NonQueryResult { WasRolledBack = options.DryRun };

        await using SqlConnection connection = await _connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        void OnInfo(object _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                result.Messages.Add(error.Message);
            }
        }

        connection.InfoMessage += OnInfo;
        try
        {
            SqlTransaction? transaction = useTransaction
                ? (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;

            await using (transaction)
            {
                bool aborted = false;
                for (int i = 0; i < batches.Count && !aborted; i++)
                {
                    ScriptBatch batch = batches[i];
                    for (int run = 0; run < batch.RepeatCount && !aborted; run++)
                    {
                        try
                        {
                            await using SqlCommand command = connection.CreateCommand();
                            command.CommandText = batch.Text;
                            command.CommandTimeout = target.CommandTimeoutSeconds;
                            command.Transaction = transaction;

                            int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                            result.Batches.Add(new BatchOutcome(i + 1, batch.StartLine, rows, batch.Text));
                        }
                        catch (SqlException ex)
                        {
                            result.Messages.Add($"Batch {i + 1} (line {batch.StartLine}): {ex.Message}");
                            result.Batches.Add(new BatchOutcome(i + 1, batch.StartLine, -1, batch.Text));
                            if (options.StopOnError)
                            {
                                aborted = true;
                            }
                        }
                    }
                }

                if (transaction is not null)
                {
                    if (options.DryRun || aborted)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        result.WasRolledBack = true;
                    }
                    else
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }

        return result;
    }

    public async Task<ConnectionProbe> TestConnectionAsync(DatabaseTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using SqlConnection connection = await _connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                       DB_NAME(),
                       SUSER_SNAME(),
                       CONVERT(int, IS_ROLEMEMBER('db_owner'))
                       | CONVERT(int, IS_ROLEMEMBER('db_ddladmin'))
                       | CONVERT(int, IS_ROLEMEMBER('db_datawriter'));
                """;
            command.CommandTimeout = Math.Max(target.ConnectTimeoutSeconds, 10);

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            return new ConnectionProbe(
                Ok: true,
                ServerVersion: reader.IsDBNull(0) ? null : reader.GetString(0),
                DatabaseName: reader.IsDBNull(1) ? null : reader.GetString(1),
                LoginName: reader.IsDBNull(2) ? null : reader.GetString(2),
                CanWrite: !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                Error: null,
                Duration: stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException or InvalidOperationException)
        {
            return new ConnectionProbe(false, null, null, null, false, ex.Message, stopwatch.Elapsed);
        }
    }

    private static async Task<QueryTable> ReadTableAsync(SqlDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var columns = new QueryColumn[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = new QueryColumn(
                // SELECT COUNT(*) with no alias has no column name at all. Label it the way SSMS
                // does, so the grid and the Excel export both have something to show.
                reader.GetName(i) is { Length: > 0 } name ? name : "(No column name)",
                reader.GetFieldType(i)?.Name ?? "object",
                reader.GetDataTypeName(i));
        }

        var rows = new List<object?[]>();
        bool truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is DBNull)
                {
                    values[i] = null;
                }
            }

            rows.Add(values);
        }

        return new QueryTable { Columns = columns, Rows = rows, Truncated = truncated };
    }
}
