using Multiplexed.Abstractions.Core.ExecutionContext;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Represents a provider-agnostic request to start or attach a runtime instance.
    /// </summary>
    public sealed record AiRuntimeHostStartRequest
    {
        /// <summary>
        /// Gets the scale-out request identifier that caused this runtime host request.
        /// </summary>
        public string RequestId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the control-plane identifier that owns the requested runtime instance.
        /// </summary>
        public string ControlPlaneId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the execution context snapshot carried by the scale-out request.
        /// </summary>
        /// <remarks>
        /// The execution context snapshot is the durable authority for tenant/runtime isolation.
        /// Host manager implementations must not derive tenant ownership from diagnostics-only metadata
        /// when this snapshot is available.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier to start or attach.
        /// </summary>
        public string RuntimeInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the runtime instance identifier prefix used to create runtime instance identifiers.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; init; } = string.Empty;

        /// <summary>
        /// Gets the provider name that will own the runtime instance.
        /// </summary>
        /// <example>local, http, grpc, kubernetes.</example>
        public string ProviderName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional transport name exposed by the runtime instance.
        /// </summary>
        /// <example>in-process, http, grpc, kubernetes-service.</example>
        public string? TransportName { get; init; }

        /// <summary>
        /// Gets the optional transport endpoint expected for the runtime instance.
        /// </summary>
        public string? TransportEndpoint { get; init; }

        /// <summary>
        /// Gets the tenant identifier associated with the requested runtime instance.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier associated with the requested runtime instance.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the runtime isolation mode requested for this runtime instance.
        /// </summary>
        public string? IsolationMode { get; init; }

        /// <summary>
        /// Gets a value indicating whether dedicated capacity should be preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; init; }

        /// <summary>
        /// Gets a value indicating whether shared capacity fallback is allowed.
        /// </summary>
        public bool AllowSharedFallback { get; init; }

        /// <summary>
        /// Gets the worker count requested for the runtime instance.
        /// </summary>
        public int WorkerCountPerInstance { get; init; }

        /// <summary>
        /// Gets the maximum number of concurrent runs allowed for the runtime instance.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; init; }

        /// <summary>
        /// Gets the local queue capacity requested for the runtime instance.
        /// </summary>
        public int LocalQueueCapacity { get; init; }

        /// <summary>
        /// Gets the optional maximum runtime instance count for the tenant/provider scope.
        /// </summary>
        public int? MaxRuntimeInstances { get; init; }

        /// <summary>
        /// Gets provider-specific metadata carried with the host manager request.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}