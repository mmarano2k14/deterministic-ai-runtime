using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Represents a command request sent to a runtime instance through a provider transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This request is transport-oriented. It does not replace the existing runtime
    /// queue control-plane request/result models and it does not replace shared run
    /// dispatch request/result models.
    /// </para>
    ///
    /// <para>
    /// It wraps the existing models so Redis, HTTP, gRPC, Kubernetes, or another
    /// future transport can carry the same runtime operations without duplicating
    /// business-level DTOs.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceCommandRequest
    {
        /// <summary>
        /// Gets the command operation.
        /// </summary>
        public required AiRuntimeInstanceCommandOperation Operation { get; init; }

        /// <summary>
        /// Gets the target runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the target runtime instance capacity descriptor.
        /// </summary>
        public AiRuntimeInstanceCapacityDescriptor? Descriptor { get; init; }

        /// <summary>
        /// Gets the shared runtime instance dispatch request for dispatch operations.
        /// </summary>
        public AiSharedRuntimeInstanceDispatchRequest? DispatchRequest { get; init; }

        /// <summary>
        /// Gets the runtime queue control-plane request for status or control operations.
        /// </summary>
        public AiRuntimeQueueControlPlaneRequest? QueueRequest { get; init; }

        /// <summary>
        /// Gets the optional correlation identifier.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Gets the caller identity.
        /// </summary>
        public string? RequestedBy { get; init; }

        /// <summary>
        /// Gets the source adapter.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Gets the optional command reason.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets when the command was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; init; } =
            DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets optional metadata used by diagnostics, dashboards, routing, Kubernetes,
        /// tenants, zones, or transport-specific labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}