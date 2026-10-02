using System.Diagnostics;
using Multiplexed.AI.Sdk.Contracts.Replay;

namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal sealed record ReplayReadinessOutcome(
    AiSdkExecutionReplayResponse Response,
    int Attempts,
    int SnapshotMissingCount,
    long ElapsedMs);

/// <summary>
/// Bounded readiness for public audit replay after terminal DAG observation.
/// The callback must reuse the same request/ExecutionId and never submit/restore.
/// </summary>
internal static class ReplaySnapshotReadiness
{
    internal const string SnapshotMissingReason = "Snapshot not found.";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<ReplayReadinessOutcome> ExecuteAsync(
        string executionId,
        bool executionCompleted,
        Func<CancellationToken, Task<AiSdkExecutionReplayResponse>> replay,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(replay);
        var budget = timeout ?? DefaultTimeout;
        var interval = pollInterval ?? DefaultPollInterval;
        if (budget <= TimeSpan.Zero || budget.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        var clock = Stopwatch.StartNew();
        var attempts = 0;
        var missingCount = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        TimeoutException TimedOut() => new(
            $"Replay snapshot readiness timed out after {budget.TotalSeconds:g}s " +
            $"for {executionId}; attempts={attempts}; snapshotMisses={missingCount}.");
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget) throw TimedOut();
                attempts++;
                var response = await replay(deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget) throw TimedOut();
                if (!string.Equals(response.ExecutionId, executionId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Replay response changed the durable ExecutionId.");

                var pending = executionCompleted && !response.Succeeded &&
                    response.Deterministic != true &&
                    string.Equals(response.FailureReason, SnapshotMissingReason, StringComparison.Ordinal);
                var outcome = pending ? "SnapshotPending" :
                    response.Succeeded && response.Deterministic == true ? "Ready" : "ReplayRejected";
                trace?.Invoke(
                    $"[REPLAY READINESS] ExecutionId={executionId}; Attempt={attempts}; " +
                    $"Outcome={outcome}; ElapsedMs={clock.ElapsedMilliseconds}");
                if (!pending)
                    return new ReplayReadinessOutcome(response, attempts, missingCount, clock.ElapsedMilliseconds);

                missingCount++;
                var remaining = budget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw TimedOut();
                await Task.Delay(interval < remaining ? interval : remaining, deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw TimedOut();
        }
    }
}
