using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;
using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness
{
    /// <summary>
    /// Represents a provider-agnostic request to wait for a runtime instance to become ready.
    /// </summary>
    public sealed record AiRuntimeInstanceReadinessRequest
    {
        /// <summary>
        /// Gets the control-plane identifier that owns the runtime instance.
        /// </summary>
        public string ControlPlaneId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the execution context snapshot carried by the scale-out request.
        /// </summary>
        /// <remarks>
        /// The execution context snapshot is the durable authority for tenant/runtime isolation.
        /// Readiness checks must prefer this snapshot over metadata when validating tenant ownership.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier to wait for.
        /// </summary>
        public string RuntimeInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the expected provider name.
        /// </summary>
        /// <example>local, http, grpc, kubernetes.</example>
        public string ProviderName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional expected transport name.
        /// </summary>
        public string? TransportName { get; init; }

        /// <summary>
        /// Gets a value indicating whether a transport endpoint must be present.
        /// </summary>
        public bool RequireTransportEndpoint { get; init; }

        /// <summary>
        /// Gets the maximum duration to wait for readiness.
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets the delay between readiness checks.
        /// </summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Gets the optional transport endpoint that must become reachable before the runtime instance is considered ready.
        /// </summary>
        public string? TransportEndpoint { get; init; }
    }
}