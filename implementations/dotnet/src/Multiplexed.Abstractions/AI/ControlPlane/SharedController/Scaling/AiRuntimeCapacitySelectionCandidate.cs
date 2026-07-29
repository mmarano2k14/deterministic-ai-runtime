namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents one typed candidate considered by hierarchical runtime capacity
    /// selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime and host identity remain first-class. <see cref="PoolId" />,
    /// <see cref="HostId" />, and <see cref="RuntimeInstanceId" /> must not be inferred
    /// from <see cref="Metadata" />.
    /// </para>
    /// <para>
    /// A candidate describes current selection evidence only. It does not replace the
    /// existing atomic runtime admission reservation store and does not itself mutate
    /// runtime, Pod, or node capacity.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeCapacitySelectionCandidate
    {
        /// <summary>
        /// Gets or sets the hierarchy level represented by this candidate.
        /// </summary>
        public AiRuntimeCapacitySelectionLevel Level { get; set; }

        /// <summary>
        /// Gets or sets the logical Runtime Pool identifier.
        /// </summary>
        public string? PoolId { get; set; }

        /// <summary>
        /// Gets or sets the immutable host incarnation identifier.
        /// </summary>
        /// <remarks>
        /// For Kubernetes Runtime Pool Pods, this value is the Kubernetes Pod UID.
        /// </remarks>
        public string? HostId { get; set; }

        /// <summary>
        /// Gets or sets the independently registered runtime instance identifier.
        /// </summary>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance provider name associated with the candidate.
        /// </summary>
        public string? ProviderName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the candidate is compatible with the
        /// tenant, isolation, transport, and execution requirements of the request.
        /// </summary>
        public bool IsCompatible { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the candidate currently exposes the
        /// capacity required by its hierarchy level.
        /// </summary>
        public bool IsAvailable { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the candidate is draining and must
        /// therefore be excluded from new admission or scale-out placement.
        /// </summary>
        public bool IsDraining { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the candidate has been suppressed by
        /// authoritative failure or capacity-safety evidence.
        /// </summary>
        public bool IsSuppressed { get; set; }

        /// <summary>
        /// Gets or sets the raw available run-slot count published by the runtime
        /// capacity heartbeat before temporary admission reservations are applied.
        /// </summary>
        public int PublishedAvailableRunSlots { get; set; }

        /// <summary>
        /// Gets or sets the current authoritative temporary admission reservation
        /// count for the runtime instance.
        /// </summary>
        public int ReservedRunSlots { get; set; }

        /// <summary>
        /// Gets or sets the effective available run-slot count after current admission
        /// reservations are subtracted from published runtime capacity.
        /// </summary>
        public int AvailableRunSlots { get; set; }

        /// <summary>
        /// Gets or sets the currently available child-process slot count in an existing
        /// Runtime Pool Pod.
        /// </summary>
        public int AvailableProcessSlots { get; set; }

        /// <summary>
        /// Gets or sets an optional human-readable candidate reason.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Gets or sets non-authoritative diagnostic metadata.
        /// </summary>
        /// <remarks>
        /// Metadata must not override typed identity, lifecycle, compatibility, safety,
        /// or available-capacity fields.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }
}
