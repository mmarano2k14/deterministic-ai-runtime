using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Projects the existing distributed runtime capacity inventory into typed
    /// hierarchical selection candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime identity, provider compatibility, tenant isolation, lifecycle state,
    /// published run slots, current admission reservations, and capacity suppression
    /// are evaluated from existing first-class authorities. Diagnostic metadata is
    /// copied to the candidate but is never used to repair missing identity or
    /// compatibility fields.
    /// </para>
    /// <para>
    /// Structurally valid but unavailable, draining, incompatible, or suppressed
    /// candidates remain in the projected inventory with explicit flags. The existing
    /// hierarchical selector owns their deterministic exclusion.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeCapacitySelectionInventoryBuilder :
        IAiRuntimeCapacitySelectionInventoryBuilder
    {
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator;
        private readonly IAiRuntimePoolCapacitySafetyReader capacitySafetyReader;
        private readonly IAiRuntimeAdmissionReservationStore reservationStore;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimeCapacitySelectionInventoryBuilder" /> class.
        /// </summary>
        /// <param name="capacityStore">
        /// The existing distributed runtime capacity store.
        /// </param>
        /// <param name="visibilityEvaluator">
        /// The existing tenant runtime visibility evaluator.
        /// </param>
        /// <param name="capacitySafetyReader">
        /// The existing exact Runtime Pool capacity-safety reader.
        /// </param>
        /// <param name="reservationStore">
        /// The existing temporary admission reservation authority.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when a required dependency is <see langword="null" />.
        /// </exception>
        public AiRuntimeCapacitySelectionInventoryBuilder(
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator,
            IAiRuntimePoolCapacitySafetyReader capacitySafetyReader,
            IAiRuntimeAdmissionReservationStore reservationStore)
        {
            this.capacityStore =
                capacityStore ??
                throw new ArgumentNullException(nameof(capacityStore));

            this.visibilityEvaluator =
                visibilityEvaluator ??
                throw new ArgumentNullException(nameof(visibilityEvaluator));

            this.capacitySafetyReader =
                capacitySafetyReader ??
                throw new ArgumentNullException(nameof(capacitySafetyReader));

            this.reservationStore =
                reservationStore ??
                throw new ArgumentNullException(nameof(reservationStore));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeCapacitySelectionCandidate>>
            BuildAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var descriptors =
                await this.capacityStore
                    .ListAsync(cancellationToken)
                    .ConfigureAwait(false);

            var eligibleDescriptors =
                descriptors
                    .Where(descriptor =>
                        HasAuthoritativeRuntimeIdentity(descriptor) &&
                        descriptor.Role ==
                            AiRuntimeInstanceRole.Runtime &&
                        this.IsVisibleToRequest(
                            request,
                            descriptor))
                    .ToArray();

            var suppressedRuntimeIdentities =
                await this.LoadSuppressedRuntimeIdentitiesAsync(
                        eligibleDescriptors,
                        cancellationToken)
                    .ConfigureAwait(false);

            var candidates =
                new List<AiRuntimeCapacitySelectionCandidate>(
                    eligibleDescriptors.Length);

            foreach (var descriptor in eligibleDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var publishedAvailableRunSlots =
                    ResolvePublishedAvailableRunSlots(descriptor);

                var reservedRunSlots =
                    await this.reservationStore
                        .GetReservedRunCountAsync(
                            descriptor.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var effectiveAvailableRunSlots =
                    Math.Max(
                        0,
                        publishedAvailableRunSlots - reservedRunSlots);

                var isCompatible =
                    IsProviderCompatible(
                        request.ProviderHint,
                        descriptor.ProviderName!);

                var isWarm =
                    isCompatible &&
                    IsWarmRuntime(
                        descriptor,
                        reservedRunSlots,
                        effectiveAvailableRunSlots);

                var isSuppressed =
                    suppressedRuntimeIdentities.Contains(
                        (
                            descriptor.PoolId!,
                            descriptor.HostId!,
                            descriptor.RuntimeInstanceId));

                var isDraining =
                    descriptor.Status ==
                    AiRuntimeInstanceStatus.Draining;

                var isAvailable =
                    IsRuntimeCapacityAvailable(
                        descriptor,
                        effectiveAvailableRunSlots);

                candidates.Add(
                    new AiRuntimeCapacitySelectionCandidate
                    {
                        Level =
                            isWarm
                                ? AiRuntimeCapacitySelectionLevel
                                    .CompatibleWarmRuntime
                                : AiRuntimeCapacitySelectionLevel
                                    .ExistingPoolRuntimeSlot,
                        PoolId = descriptor.PoolId,
                        HostId = descriptor.HostId,
                        RuntimeInstanceId =
                            descriptor.RuntimeInstanceId,
                        ProviderName = descriptor.ProviderName,
                        IsCompatible = isCompatible,
                        IsAvailable = isAvailable,
                        IsDraining = isDraining,
                        IsSuppressed = isSuppressed,
                        PublishedAvailableRunSlots =
                            publishedAvailableRunSlots,
                        ReservedRunSlots = reservedRunSlots,
                        AvailableRunSlots =
                            effectiveAvailableRunSlots,
                        Reason =
                            ResolveCandidateReason(
                                isWarm,
                                isCompatible,
                                isAvailable,
                                isDraining,
                                isSuppressed),
                        Metadata = descriptor.Metadata
                    });
            }

            return candidates
                .OrderBy(
                    candidate => candidate.PoolId,
                    StringComparer.Ordinal)
                .ThenBy(
                    candidate => candidate.HostId,
                    StringComparer.Ordinal)
                .ThenBy(
                    candidate => candidate.RuntimeInstanceId,
                    StringComparer.Ordinal)
                .ThenBy(
                    candidate => candidate.ProviderName,
                    StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Loads exact suppression evidence once per host and indexes it by complete
        /// Runtime Pool identity.
        /// </summary>
        /// <param name="descriptors">The structurally eligible runtime descriptors.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The exact suppressed <c>PoolId</c>, <c>HostId</c>, and
        /// <c>RuntimeInstanceId</c> identities.
        /// </returns>
        private async Task<HashSet<(
            string PoolId,
            string HostId,
            string RuntimeInstanceId)>>
            LoadSuppressedRuntimeIdentitiesAsync(
                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> descriptors,
                CancellationToken cancellationToken)
        {
            var suppressedRuntimeIdentities =
                new HashSet<(
                    string PoolId,
                    string HostId,
                    string RuntimeInstanceId)>();

            var hostIds =
                descriptors
                    .Select(descriptor => descriptor.HostId!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(hostId => hostId, StringComparer.Ordinal);

            foreach (var hostId in hostIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var suppressions =
                    await this.capacitySafetyReader
                        .ListByHostIdAsync(
                            hostId,
                            cancellationToken)
                        .ConfigureAwait(false);

                foreach (var suppression in suppressions)
                {
                    if (string.IsNullOrWhiteSpace(suppression.PoolId) ||
                        string.IsNullOrWhiteSpace(suppression.HostId) ||
                        string.IsNullOrWhiteSpace(
                            suppression.RuntimeInstanceId))
                    {
                        continue;
                    }

                    suppressedRuntimeIdentities.Add(
                        (
                            suppression.PoolId,
                            suppression.HostId,
                            suppression.RuntimeInstanceId));
                }
            }

            return suppressedRuntimeIdentities;
        }

        /// <summary>
        /// Determines whether the descriptor exposes complete first-class identity for
        /// runtime-level hierarchical selection.
        /// </summary>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <returns>
        /// <see langword="true" /> when the descriptor is structurally authoritative;
        /// otherwise, <see langword="false" />.
        /// </returns>
        private static bool HasAuthoritativeRuntimeIdentity(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            return descriptor is not null &&
                   !string.IsNullOrWhiteSpace(
                       descriptor.RuntimeInstanceId) &&
                   !string.IsNullOrWhiteSpace(descriptor.PoolId) &&
                   !string.IsNullOrWhiteSpace(descriptor.HostId) &&
                   !string.IsNullOrWhiteSpace(descriptor.ProviderName);
        }

        /// <summary>
        /// Determines whether the runtime provider is compatible with the request's
        /// existing provider hint.
        /// </summary>
        /// <param name="providerHint">The requested provider hint.</param>
        /// <param name="providerName">The descriptor provider name.</param>
        /// <returns>
        /// <see langword="true" /> when provider selection may use the runtime;
        /// otherwise, <see langword="false" />.
        /// </returns>
        private static bool IsProviderCompatible(
            string? providerHint,
            string providerName)
        {
            return string.IsNullOrWhiteSpace(providerHint) ||
                   StringComparer.OrdinalIgnoreCase.Equals(
                       providerHint.Trim(),
                       providerName.Trim());
        }

        /// <summary>
        /// Determines whether the descriptor is visible to the tenant authority carried
        /// by the existing provider request.
        /// </summary>
        /// <param name="request">The provider-level request.</param>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <returns>
        /// <see langword="true" /> when the runtime is visible; otherwise,
        /// <see langword="false" />.
        /// </returns>
        private bool IsVisibleToRequest(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            return this.visibilityEvaluator.IsVisible(
                request.ExecutionContextSnapshot.TenantId,
                request.ExecutionContextSnapshot.TenantGroupId,
                new AiRuntimeInstanceVisibilityDescriptor
                {
                    RuntimeInstanceId =
                        descriptor.RuntimeInstanceId,
                    TenantId = descriptor.TenantId,
                    TenantGroupId = descriptor.TenantGroupId,
                    IsolationMode = descriptor.IsolationMode,
                    AllowSharedFallback =
                        descriptor.AllowSharedFallback,
                    PreferDedicatedCapacity =
                        descriptor.PreferDedicatedCapacity,
                    Metadata = descriptor.Metadata
                });
        }

        /// <summary>
        /// Resolves the raw available run-slot count published by the runtime heartbeat.
        /// </summary>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <returns>The non-negative published run-slot count.</returns>
        private static int ResolvePublishedAvailableRunSlots(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            if (descriptor.AvailableRunSlots.HasValue)
            {
                return Math.Max(
                    0,
                    descriptor.AvailableRunSlots.Value);
            }

            return Math.Max(
                0,
                descriptor.EffectiveAvailableRunSlots ?? 0);
        }

        /// <summary>
        /// Determines whether the runtime is idle, ready, unreserved, and already warm.
        /// </summary>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <param name="reservedRunSlots">
        /// The current authoritative admission reservation count.
        /// </param>
        /// <param name="effectiveAvailableRunSlots">
        /// The effective available run slots.
        /// </param>
        /// <returns>
        /// <see langword="true" /> when the runtime is a warm candidate; otherwise,
        /// <see langword="false" />.
        /// </returns>
        private static bool IsWarmRuntime(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            int reservedRunSlots,
            int effectiveAvailableRunSlots)
        {
            return descriptor.Status == AiRuntimeInstanceStatus.Ready &&
                   descriptor.ActiveRunCount == 0 &&
                   descriptor.RunningRunCount == 0 &&
                   descriptor.QueuedRunCount == 0 &&
                   reservedRunSlots == 0 &&
                   effectiveAvailableRunSlots > 0;
        }

        /// <summary>
        /// Determines whether the descriptor currently exposes usable runtime capacity.
        /// </summary>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <param name="effectiveAvailableRunSlots">
        /// The effective available run slots.
        /// </param>
        /// <returns>
        /// <see langword="true" /> when the runtime can currently accept a run;
        /// otherwise, <see langword="false" />.
        /// </returns>
        private static bool IsRuntimeCapacityAvailable(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            int effectiveAvailableRunSlots)
        {
            var statusAllowsAdmission =
                descriptor.Status is
                    AiRuntimeInstanceStatus.Ready or
                    AiRuntimeInstanceStatus.Busy;

            var minimumWorkers =
                Math.Max(
                    1,
                    descriptor.MinWorkersRequiredPerRun);

            return statusAllowsAdmission &&
                   !descriptor.IsQueuePaused &&
                   descriptor.CanAcceptRun &&
                   effectiveAvailableRunSlots > 0 &&
                   descriptor.AvailableWorkerCount >= minimumWorkers;
        }

        /// <summary>
        /// Resolves one deterministic diagnostic reason for the projected candidate.
        /// </summary>
        /// <param name="isWarm">Whether the runtime is warm.</param>
        /// <param name="isCompatible">Whether provider compatibility succeeded.</param>
        /// <param name="isAvailable">Whether runtime capacity is available.</param>
        /// <param name="isDraining">Whether the runtime is draining.</param>
        /// <param name="isSuppressed">Whether exact capacity suppression exists.</param>
        /// <returns>The candidate reason.</returns>
        private static string ResolveCandidateReason(
            bool isWarm,
            bool isCompatible,
            bool isAvailable,
            bool isDraining,
            bool isSuppressed)
        {
            if (isSuppressed)
            {
                return "runtime-capacity-suppressed";
            }

            if (isDraining)
            {
                return "runtime-capacity-draining";
            }

            if (!isCompatible)
            {
                return "runtime-provider-incompatible";
            }

            if (!isAvailable)
            {
                return "runtime-capacity-unavailable";
            }

            return isWarm
                ? "compatible-warm-runtime"
                : "existing-pool-runtime-slot";
        }
    }
}
