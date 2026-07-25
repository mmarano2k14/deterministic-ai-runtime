namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Describes a test-only durable crash checkpoint embedded in one pipeline DAG.
    /// </summary>
    public sealed class McpTestCrashCheckpointDefinition
    {
        /// <summary>
        /// Gets the external step key used by the durable crash checkpoint.
        /// </summary>
        public const string StepKey = "distributed.chaos.crash-checkpoint";

        /// <summary>
        /// Gets the one-based pipeline step index at which execution must stop.
        /// </summary>
        public required int StepIndex { get; init; }

        /// <summary>
        /// Gets the durable Redis state key.
        /// </summary>
        public required string StateKey { get; init; }

        /// <summary>
        /// Gets the Redis channel published when the checkpoint has been reached.
        /// </summary>
        public required string ReachedChannel { get; init; }

        /// <summary>
        /// Gets the Redis channel published when the checkpoint is released.
        /// </summary>
        public required string ReleasedChannel { get; init; }

        /// <summary>
        /// Gets the durable checkpoint state time-to-live in seconds.
        /// </summary>
        public required int TtlSeconds { get; init; }
    }
}
