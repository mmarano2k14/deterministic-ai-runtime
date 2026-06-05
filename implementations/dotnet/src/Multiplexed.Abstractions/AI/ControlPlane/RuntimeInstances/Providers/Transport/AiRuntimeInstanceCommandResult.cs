using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Represents a command result returned by a runtime instance transport.
    /// </summary>
    /// <remarks>
    /// This result wraps existing runtime dispatch and queue control-plane results.
    /// It should not duplicate those models.
    /// </remarks>
    public sealed class AiRuntimeInstanceCommandResult
    {
        /// <summary>
        /// Gets a value indicating whether the command succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Gets the command operation.
        /// </summary>
        public required AiRuntimeInstanceCommandOperation Operation { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the dispatch result when the command was a dispatch operation.
        /// </summary>
        public AiSharedRuntimeInstanceDispatchResult? DispatchResult { get; init; }

        /// <summary>
        /// Gets the runtime queue control-plane result when the command was a queue status
        /// or control operation.
        /// </summary>
        public AiRuntimeQueueControlPlaneResult? QueueResult { get; init; }

        /// <summary>
        /// Gets a human-readable message.
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// Gets the failure reason when the command failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets when the command started.
        /// </summary>
        public DateTimeOffset StartedAtUtc { get; init; }

        /// <summary>
        /// Gets when the command completed.
        /// </summary>
        public DateTimeOffset CompletedAtUtc { get; init; }

        /// <summary>
        /// Gets the command duration in milliseconds.
        /// </summary>
        public long DurationMs { get; init; }

        /// <summary>
        /// Gets additional metadata associated with the command result.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}