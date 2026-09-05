using System.Diagnostics;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Execution;

/// <summary>Outcome of running one operation against one target.</summary>
public sealed record TargetRun<T>(
    DatabaseTarget Target,
    TargetStatus Status,
    T? Result,
    string? Error,
    TimeSpan Duration,
    int Attempts)
{
    public bool IsSuccess => Status == TargetStatus.Succeeded;
}

public sealed record FanOutOptions
{
    /// <summary>Maximum databases worked on at once. Low by default — production servers, not a load test.</summary>
    public int MaxConcurrency { get; init; } = 6;

    /// <summary>Total attempts per target when the failure looks transient (connection drop, deadlock). 1 = no retry.</summary>
    public int MaxAttempts { get; init; } = 1;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static FanOutOptions Default { get; } = new();
}

/// <summary>
/// Runs the same operation against many databases with a small concurrency cap, per-target
/// isolation (one failure never stops the others), optional transient-error retry, and progress
/// reporting. Re-running only the failures is just a second call with the failed subset.
/// </summary>
public sealed class FanOutRunner
{
    private readonly FanOutOptions _options;

    public FanOutRunner(FanOutOptions? options = null)
    {
        _options = options ?? FanOutOptions.Default;
        if (_options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        }

        if (_options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be at least 1.");
        }
    }

    public async Task<IReadOnlyList<TargetRun<T>>> RunAsync<T>(
        IEnumerable<DatabaseTarget> targets,
        Func<DatabaseTarget, CancellationToken, Task<T>> operation,
        IProgress<TargetRun<T>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(operation);

        var pending = targets.Where(t => t.Enabled).ToList();
        using var gate = new SemaphoreSlim(_options.MaxConcurrency);
        var results = new TargetRun<T>[pending.Count];

        var tasks = pending.Select(async (target, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TargetRun<T> run = await ExecuteOneAsync(target, operation, cancellationToken).ConfigureAwait(false);
                results[index] = run;
                progress?.Report(run);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            for (int i = 0; i < results.Length; i++)
            {
                results[i] ??= new TargetRun<T>(pending[i], TargetStatus.Cancelled, default, "Cancelled.", TimeSpan.Zero, 0);
            }
        }

        return results;
    }

    private async Task<TargetRun<T>> ExecuteOneAsync<T>(
        DatabaseTarget target,
        Func<DatabaseTarget, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? last = null;

        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                T value = await operation(target, cancellationToken).ConfigureAwait(false);
                return new TargetRun<T>(target, TargetStatus.Succeeded, value, null, stopwatch.Elapsed, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new TargetRun<T>(target, TargetStatus.Cancelled, default, "Cancelled.", stopwatch.Elapsed, attempt);
            }
            catch (Exception ex)
            {
                last = ex;
                bool willRetry = attempt < _options.MaxAttempts && TransientError.IsTransient(ex);
                if (!willRetry)
                {
                    break;
                }

                await Task.Delay(_options.RetryBaseDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return new TargetRun<T>(target, TargetStatus.Failed, default, last?.Message ?? "Unknown error.", stopwatch.Elapsed, _options.MaxAttempts);
    }
}
