using SqlHelper.Core.Auditing;

namespace SqlHelper.Core.Tests.Auditing;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-audit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AuditEvent Event(DateTimeOffset when, string action = "select") => new()
    {
        Operator = "DOMAIN\\user",
        Machine = "PC1",
        Action = action,
        TimestampUtc = when,
        Statement = "SELECT 1",
        Targets =
        [
            new AuditTargetResult("Acme", "sql-01", "AcmeDb", "Production", "succeeded", 3, null, null, null),
        ],
    };

    [Fact]
    public async Task Appended_entries_are_read_back_in_the_same_month()
    {
        var log = new AuditLog(_dir);
        var when = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

        await log.AppendAsync(Event(when));
        await log.AppendAsync(Event(when.AddMinutes(5), "nonquery"));

        IReadOnlyList<AuditEvent> month = await log.ReadMonthAsync(2026, 9);

        Assert.Equal(2, month.Count);
        Assert.Equal("select", month[0].Action);
        Assert.Equal("nonquery", month[1].Action);
    }

    [Fact]
    public async Task Reading_an_empty_month_returns_nothing()
    {
        var log = new AuditLog(_dir);

        Assert.Empty(await log.ReadMonthAsync(2020, 1));
    }

    [Fact]
    public async Task Round_trips_target_results_and_counts()
    {
        var log = new AuditLog(_dir);
        var when = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(Event(when));

        AuditEvent read = (await log.ReadMonthAsync(2026, 9))[0];

        Assert.Equal(1, read.SucceededCount);
        Assert.Equal(0, read.FailedCount);
        Assert.Equal("Acme", read.Targets[0].ClientName);
        Assert.Equal(3, read.Targets[0].RowsAffected);
    }

    [Fact]
    public async Task ReadRangeAsync_spans_multiple_monthly_files()
    {
        var log = new AuditLog(_dir);
        await log.AppendAsync(Event(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));
        await log.AppendAsync(Event(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)));
        await log.AppendAsync(Event(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)));

        IReadOnlyList<AuditEvent> range = await log.ReadRangeAsync(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(range);
        Assert.Equal(8, range[0].TimestampUtc.Month);
    }

    [Fact]
    public async Task Concurrent_appends_are_all_preserved()
    {
        var log = new AuditLog(_dir);
        var when = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

        await Task.WhenAll(Enumerable.Range(0, 25).Select(i => log.AppendAsync(Event(when.AddSeconds(i)))));

        IReadOnlyList<AuditEvent> month = await log.ReadMonthAsync(2026, 9);
        Assert.Equal(25, month.Count);
    }
}
