using System.Text;
using System.Text.Json;
using SqlHelper.Core.Serialization;

namespace SqlHelper.Core.Auditing;

/// <summary>
/// Append-only audit trail, one JSON Lines file per month. Entries are appended and read; there
/// is no update or delete path — the log is meant to be trustworthy evidence of what ran.
/// </summary>
public sealed class AuditLog : IDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AuditLog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        string line = JsonSerializer.Serialize(auditEvent, Json.Compact);
        string file = FileFor(auditEvent.TimestampUtc);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(file, line + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads every entry for one calendar month (UTC), oldest first. Empty when the month has no log file.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ReadMonthAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        string file = Path.Combine(_directory, $"{year:D4}-{month:D2}.jsonl");
        if (!File.Exists(file))
        {
            return [];
        }

        var results = new List<AuditEvent>();
        await foreach (string line in File.ReadLinesAsync(file, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AuditEvent? entry = JsonSerializer.Deserialize<AuditEvent>(line, Json.Compact);
            if (entry is not null)
            {
                results.Add(entry);
            }
        }

        return results;
    }

    /// <summary>Reads every entry between two UTC instants (inclusive), across however many monthly files that spans.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ReadRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AuditEvent>();
        DateTimeOffset cursor = new(fromUtc.Year, fromUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        while (cursor <= toUtc)
        {
            IReadOnlyList<AuditEvent> month = await ReadMonthAsync(cursor.Year, cursor.Month, cancellationToken).ConfigureAwait(false);
            results.AddRange(month.Where(e => e.TimestampUtc >= fromUtc && e.TimestampUtc <= toUtc));
            cursor = cursor.AddMonths(1);
        }

        return [.. results.OrderBy(e => e.TimestampUtc)];
    }

    private string FileFor(DateTimeOffset timestampUtc) =>
        Path.Combine(_directory, $"{timestampUtc.Year:D4}-{timestampUtc.Month:D2}.jsonl");

    public void Dispose() => _writeLock.Dispose();
}
