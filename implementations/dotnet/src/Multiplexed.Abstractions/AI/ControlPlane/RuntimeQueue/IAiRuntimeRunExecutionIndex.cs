namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Tracks the durable relationship between a local runtime queue run and
    /// the DAG execution created from that run.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Allows the control plane to resolve LocalRunId -> ExecutionId even after
    ///   the local queue item has been consumed by the background controller.
    /// - Makes runtime run status observable after dequeue/start.
    /// - Provides the missing bridge required by shared queue / multi-instance
    ///   execution tests and future Kubernetes observability.
    ///
    /// IMPORTANT:
    /// - This is not the shared/global queue.
    /// - This index belongs to one runtime instance.
    /// - Implementations can be in-memory for tests or Redis-backed later.
    /// </remarks>
    public interface IAiRuntimeRunExecutionIndex
    {
        Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default);

        Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default);

        Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default);

        Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default);
    }
}