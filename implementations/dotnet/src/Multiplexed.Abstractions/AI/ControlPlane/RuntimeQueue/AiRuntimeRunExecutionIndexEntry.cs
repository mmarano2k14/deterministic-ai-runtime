using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Represents an indexed local runtime run and its associated durable DAG execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry is written when a local runtime run is queued and then updated as the
    /// runtime background controller creates, starts, completes, fails, or cancels the
    /// associated DAG execution.
    /// </para>
    /// <para>
    /// <see cref="ExecutionContextSnapshot"/> is part of the durable index entry so that
    /// Redis-backed and multi-instance implementations can enforce tenant isolation
    /// when resolving <see cref="RunId"/> to <see cref="ExecutionId"/>.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeRunExecutionIndexEntry
    {
        /// <summary>
        /// Gets the local runtime queue run identifier.
        /// </summary>
        public required string RunId { get; init; }

        /// <summary>
        /// Gets the durable DAG execution identifier associated with the runtime run,
        /// when the DAG execution has already been created.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the runtime instance that owns or created this local runtime run.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the current indexed status of the runtime run.
        /// </summary>
        public string? Status { get; init; }

        /// <summary>
        /// Gets the failure or cancellation reason associated with the runtime run,
        /// when applicable.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets the durable execution context snapshot captured when the runtime run
        /// was queued.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ExecutionContextSnapshot.TenantId"/> is the durable tenant boundary
        /// and must be used by Redis-backed implementations to isolate reads and mutations.
        /// </para>
        /// <para>
        /// <see cref="ExecutionContextSnapshot.ContextKey"/> is volatile and must not be
        /// used as a durable partition key.
        /// </para>
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the UTC timestamp at which the index entry was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp at which the runtime run started, when available.
        /// </summary>
        public DateTimeOffset? StartedAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp at which the runtime run reached a terminal state,
        /// when available.
        /// </summary>
        public DateTimeOffset? CompletedAtUtc { get; init; }

        /// <summary>
        /// Gets additional metadata associated with the runtime run index entry.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}
