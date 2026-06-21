using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Represents the result of starting or attaching a runtime instance through the runtime host manager.
    /// </summary>
    public sealed record AiRuntimeHostStartResult
    {
        /// <summary>
        /// Gets a value indicating whether the runtime host operation succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the execution context snapshot carried by the runtime host operation.
        /// </summary>
        /// <remarks>
        /// The execution context snapshot is the durable authority for tenant/runtime isolation.
        /// Consumers must not derive tenant ownership from diagnostics-only metadata when this snapshot is available.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier returned by the host manager.
        /// </summary>
        public string RuntimeInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the provider name that owns the runtime instance.
        /// </summary>
        public string ProviderName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional transport name exposed by the runtime instance.
        /// </summary>
        public string? TransportName { get; init; }

        /// <summary>
        /// Gets the optional transport endpoint exposed by the runtime instance.
        /// </summary>
        public string? TransportEndpoint { get; init; }

        /// <summary>
        /// Gets the structured failure reason when the host operation failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the failure can be retried safely.
        /// </summary>
        public bool Retryable { get; init; }

        /// <summary>
        /// Gets host-manager metadata returned by the operation.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();


        /// <summary>
        /// Creates a successful runtime host start result.
        /// </summary>
        public static AiRuntimeHostStartResult Started(
            ExecutionContextSnapshot executionContextSnapshot,
            string runtimeInstanceId,
            string providerName,
            string? transportName,
            string? transportEndpoint,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeHostStartResult
            {
                Success = true,
                ExecutionContextSnapshot = executionContextSnapshot,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderName = providerName,
                TransportName = transportName,
                TransportEndpoint = transportEndpoint,
                Retryable = false,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Creates a rejected runtime host start result.
        /// </summary>
        public static AiRuntimeHostStartResult Rejected(
            ExecutionContextSnapshot executionContextSnapshot,
            string runtimeInstanceId,
            string providerName,
            string? transportName,
            string? transportEndpoint,
            string failureReason,
            bool retryable = false,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeHostStartResult
            {
                Success = false,
                ExecutionContextSnapshot = executionContextSnapshot,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderName = providerName,
                TransportName = transportName,
                TransportEndpoint = transportEndpoint,
                FailureReason = failureReason,
                Retryable = retryable,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}