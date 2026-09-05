using Microsoft.Data.SqlClient;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Scripting;

/// <summary>
/// Reads programmable-object definitions and signatures from a live connection. The caller owns
/// the connection — during a deployment the same connection is used to read, back up, alter and
/// verify one database.
/// </summary>
public sealed class ObjectInspector
{
    public async Task<ProgrammableObject?> GetAsync(SqlConnection connection, ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);

        const string sql =
            """
            SELECT o.object_id,
                   s.name AS schema_name,
                   o.name AS object_name,
                   o.type,
                   m.definition,
                   CAST(COALESCE(m.uses_ansi_nulls, 1) AS bit),
                   CAST(COALESCE(m.uses_quoted_identifier, 1) AS bit),
                   o.create_date,
                   o.modify_date,
                   CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.object_id = OBJECT_ID(@name)
              AND o.type IN ('P', 'FN', 'TF', 'IF', 'V', 'TR');
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@name", name.Plain));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string sysType = reader.GetString(3);
        ProgrammableObjectKind? kind = ProgrammableObjectKinds.FromSysType(sysType);
        if (kind is null)
        {
            return null;
        }

        bool encrypted = reader.GetBoolean(9);
        string definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

        return new ProgrammableObject(
            new ObjectName(reader.GetString(1), reader.GetString(2)),
            kind.Value,
            definition,
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetInt32(0),
            reader.GetDateTime(7),
            reader.GetDateTime(8),
            encrypted);
    }

    public async Task<bool> ExistsAsync(SqlConnection connection, ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@name);";
        command.Parameters.Add(new SqlParameter("@name", name.Plain));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    /// <summary>
    /// Reads the routine's parameter signature from its definition text. Parsing the definition —
    /// not <c>sys.parameters</c> — is deliberate: SQL Server does not expose T-SQL parameter
    /// defaults in metadata, but they matter for drift ("a new parameter with a default is
    /// backward-compatible").
    /// </summary>
    public async Task<RoutineSignature?> GetSignatureAsync(SqlConnection connection, ObjectName name, CancellationToken cancellationToken = default)
    {
        ProgrammableObject? obj = await GetAsync(connection, name, cancellationToken).ConfigureAwait(false);
        if (obj is null || !obj.HasDefinition)
        {
            return null;
        }

        return RoutineSignature.FromDefinition(obj.Definition, obj.Kind);
    }

    public async Task<IReadOnlyList<ObjectSummary>> ListAsync(
        SqlConnection connection,
        IReadOnlyCollection<ProgrammableObjectKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        const string sql =
            """
            SELECT s.name, o.name, o.type, o.modify_date
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P', 'FN', 'TF', 'IF', 'V', 'TR')
              AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        var results = new List<ObjectSummary>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ProgrammableObjectKind? kind = ProgrammableObjectKinds.FromSysType(reader.GetString(2));
            if (kind is null || (kinds is { Count: > 0 } && !kinds.Contains(kind.Value)))
            {
                continue;
            }

            results.Add(new ObjectSummary(
                new ObjectName(reader.GetString(0), reader.GetString(1)),
                kind.Value,
                reader.GetDateTime(3)));
        }

        return results;
    }
}
