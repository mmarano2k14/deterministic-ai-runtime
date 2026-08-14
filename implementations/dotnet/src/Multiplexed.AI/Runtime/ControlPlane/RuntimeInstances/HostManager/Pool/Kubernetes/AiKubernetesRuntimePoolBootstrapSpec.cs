using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Represents the strongly typed in-Pod bootstrap contract for one Runtime Pool host.
    /// </summary>
    /// <remarks>
    /// This contract deliberately avoids environment-variable-based test configuration.
    /// A later package will map it to the concrete container bootstrap mechanism.
    /// </remarks>
    public sealed record AiKubernetesRuntimePoolBootstrapSpec
    {
        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable Pod creation request identity.
        /// </summary>
        public required string PodRequestId { get; init; }

        /// <summary>
        /// Gets the runtime provider name.
        /// </summary>
        public required string ProviderName { get; init; }

        /// <summary>
        /// Gets the command transport name.
        /// </summary>
        public required string TransportName { get; init; }

        /// <summary>
        /// Gets the stable pool transport port.
        /// </summary>
        public int StableTransportPort { get; init; }

        /// <summary>
        /// Gets the dedicated HTTP/1 Kubernetes readiness port.
        /// </summary>
        public int ReadinessPort { get; init; }

        /// <summary>
        /// Gets the configured initial child count.
        /// </summary>
        public int InitialRuntimeInstanceCount { get; init; }

        /// <summary>
        /// Gets the configured minimum healthy child count.
        /// </summary>
        public int MinimumRuntimeInstanceCount { get; init; }

        /// <summary>
        /// Gets the configured maximum child count.
        /// </summary>
        public int MaximumRuntimeInstanceCount { get; init; }

        /// <summary>
        /// Gets the maximum number of parallel child startups.
        /// </summary>
        public int StartupParallelism { get; init; }

        /// <summary>
        /// Gets the graceful shutdown timeout in seconds.
        /// </summary>
        public int ShutdownTimeoutSeconds { get; init; }

        /// <summary>
        /// Gets the independently identifiable child runtime plans.
        /// </summary>
        public required IReadOnlyList<AiKubernetesRuntimePoolRuntimeInstancePlan>
            RuntimeInstances
        {
            get;
            init;
        }
    }
}
