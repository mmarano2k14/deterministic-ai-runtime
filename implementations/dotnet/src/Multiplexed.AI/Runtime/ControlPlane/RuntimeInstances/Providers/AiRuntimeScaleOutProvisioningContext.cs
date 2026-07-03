using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Holds resolved values required to provision one runtime instance from a scale-out provider.
    /// </summary>
    internal sealed class AiRuntimeScaleOutProvisioningContext
    {
        /// <summary>
        /// Gets the resolved tenant id.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the resolved tenant group id.
        /// </summary>
        public required string TenantGroupId { get; init; }

        /// <summary>
        /// Gets the resolved isolation mode.
        /// </summary>
        public required AiRuntimeInstanceIsolationMode IsolationMode { get; init; }

        /// <summary>
        /// Gets a value indicating whether dedicated capacity is preferred.
        /// </summary>
        public required bool PreferDedicatedCapacity { get; init; }

        /// <summary>
        /// Gets a value indicating whether shared fallback is allowed.
        /// </summary>
        public required bool AllowSharedFallback { get; init; }

        /// <summary>
        /// Gets the resolved runtime instance id.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the resolved runtime instance id prefix.
        /// </summary>
        public required string RuntimeInstanceIdPrefix { get; init; }

        /// <summary>
        /// Gets the resolved endpoint.
        /// </summary>
        public required string Endpoint { get; init; }

        /// <summary>
        /// Gets the resolved worker count.
        /// </summary>
        public required int WorkerCount { get; init; }

        /// <summary>
        /// Gets the resolved max concurrent runs.
        /// </summary>
        public required int MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Gets the resolved queue capacity.
        /// </summary>
        public required int QueueCapacity { get; init; }

        /// <summary>
        /// Gets the resolved max runtime instances.
        /// </summary>
        public int? MaxRuntimeInstances { get; init; }

        /// <summary>
        /// Gets the resolved metadata.
        /// </summary>
        public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    }
}