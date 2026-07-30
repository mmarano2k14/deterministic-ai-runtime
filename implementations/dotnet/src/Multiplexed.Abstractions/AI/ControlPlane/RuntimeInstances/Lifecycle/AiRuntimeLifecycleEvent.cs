using System;
using System.Collections.Generic;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Represents one append-only runtime infrastructure lifecycle event.
    /// </summary>
    /// <remarks>
    /// Correctness, routing, lifecycle, recovery, and correlation identities are first-class
    /// properties. <see cref="Metadata"/> is reserved for non-authoritative diagnostics and
    /// provider-specific details.
    /// </remarks>
    public sealed record AiRuntimeLifecycleEvent
    {
        /// <summary>
        /// Gets the stable event identifier.
        /// </summary>
        public required string EventId { get; init; }

        /// <summary>
        /// Gets the event type.
        /// </summary>
        public required string EventType { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the event happened.
        /// </summary>
        public required DateTimeOffset TimestampUtc { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier that emitted the event.
        /// </summary>
        public required string ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the physical host creation mode when known.
        /// </summary>
        public AiRuntimeHostCreationMode? HostCreationMode { get; init; }

        /// <summary>
        /// Gets the runtime provider name when known.
        /// </summary>
        public string? ProviderName { get; init; }

        /// <summary>
        /// Gets the logical runtime pool identifier when known.
        /// </summary>
        public string? PoolId { get; init; }

        /// <summary>
        /// Gets the immutable physical host incarnation identifier when known.
        /// </summary>
        public string? HostId { get; init; }

        /// <summary>
        /// Gets the Kubernetes Pod UID when the host is a Kubernetes Pod.
        /// </summary>
        public string? KubernetesPodUid { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace when known.
        /// </summary>
        public string? KubernetesNamespace { get; init; }

        /// <summary>
        /// Gets the Kubernetes Pod name when known.
        /// </summary>
        public string? KubernetesPodName { get; init; }

        /// <summary>
        /// Gets the Kubernetes node name when known.
        /// </summary>
        public string? KubernetesNodeName { get; init; }

        /// <summary>
        /// Gets the independently addressable runtime instance identifier when known.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the logical runtime identity inside its owning host when known.
        /// </summary>
        public string? RuntimeId { get; init; }

        /// <summary>
        /// Gets the operating-system process identifier when known.
        /// </summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// Gets the tenant identifier for tenant-scoped work events.
        /// </summary>
        /// <remarks>
        /// Shared infrastructure events must leave this value null rather than assigning the
        /// infrastructure to the first tenant that uses it.
        /// </remarks>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant-group identifier for group-scoped work events.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the durable shared-run identifier when known.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier when known.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier when known.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the infrastructure failure incident identifier when known.
        /// </summary>
        public string? RuntimeFailureIncidentId { get; init; }

        /// <summary>
        /// Gets the related decision-ledger entry identifier when known.
        /// </summary>
        public string? LedgerEntryId { get; init; }

        /// <summary>
        /// Gets the related recovery-forensics identifier when known.
        /// </summary>
        public string? ForensicsId { get; init; }

        /// <summary>
        /// Gets the correlation identifier shared by the causal operation.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Gets the identifier of the event or decision that caused this event.
        /// </summary>
        public string? CausationId { get; init; }

        /// <summary>
        /// Gets the previous lifecycle status when the event represents a transition.
        /// </summary>
        public string? PreviousStatus { get; init; }

        /// <summary>
        /// Gets the current lifecycle status when the event represents a transition.
        /// </summary>
        public string? CurrentStatus { get; init; }

        /// <summary>
        /// Gets the event reason when known.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets optional non-authoritative diagnostic metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
