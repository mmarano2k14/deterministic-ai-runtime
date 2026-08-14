using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Selects runtime capacity according to the deterministic Runtime Pool capacity
    /// hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selector is intentionally side-effect free. It does not reserve run slots,
    /// create runtime processes, create Runtime Pool Pods, or request Kubernetes nodes.
    /// It chooses the first safe action that later execution components may attempt.
    /// </para>
    /// <para>
    /// Candidates are ordered first by <see cref="AiRuntimeCapacitySelectionLevel" />
    /// and then by first-class pool, host, runtime, and provider identity. This makes
    /// equivalent inventories converge on the same decision regardless of enumeration
    /// order.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeHierarchicalCapacitySelector :
        IAiRuntimeHierarchicalCapacitySelector
    {
        /// <inheritdoc />
        public Task<AiRuntimeCapacitySelectionDecision> SelectAsync(
            AiRuntimeScaleOutProviderRequest request,
            IReadOnlyList<AiRuntimeCapacitySelectionCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidates);

            cancellationToken.ThrowIfCancellationRequested();

            var selected =
                candidates
                    .Where(IsSelectable)
                    .OrderBy(
                        candidate => (int)candidate.Level)
                    .ThenBy(
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
                    .FirstOrDefault();

            if (selected is null)
            {
                return Task.FromResult(
                    new AiRuntimeCapacitySelectionDecision
                    {
                        Level =
                            AiRuntimeCapacitySelectionLevel.Backpressure,
                        Candidate = null,
                        EvaluatedCandidateCount = candidates.Count,
                        Reason = "hierarchical-capacity-exhausted"
                    });
            }

            return Task.FromResult(
                new AiRuntimeCapacitySelectionDecision
                {
                    Level = selected.Level,
                    Candidate = selected,
                    EvaluatedCandidateCount = candidates.Count,
                    Reason =
                        string.IsNullOrWhiteSpace(selected.Reason)
                            ? "hierarchical-capacity-selected"
                            : selected.Reason
                });
        }

        /// <summary>
        /// Returns whether one candidate is safe and structurally authoritative for its
        /// hierarchy level.
        /// </summary>
        /// <param name="candidate">The candidate.</param>
        /// <returns>
        /// <see langword="true" /> when the candidate may be selected; otherwise,
        /// <see langword="false" />.
        /// </returns>
        private static bool IsSelectable(
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            if (candidate is null ||
                !candidate.IsCompatible ||
                !candidate.IsAvailable ||
                candidate.IsDraining ||
                candidate.IsSuppressed)
            {
                return false;
            }

            return candidate.Level switch
            {
                AiRuntimeCapacitySelectionLevel.CompatibleWarmRuntime =>
                    HasValue(candidate.PoolId) &&
                    HasValue(candidate.HostId) &&
                    HasValue(candidate.RuntimeInstanceId) &&
                    candidate.AvailableRunSlots > 0,

                AiRuntimeCapacitySelectionLevel.ExistingPoolRuntimeSlot =>
                    HasValue(candidate.PoolId) &&
                    HasValue(candidate.HostId) &&
                    HasValue(candidate.RuntimeInstanceId) &&
                    candidate.AvailableRunSlots > 0,

                AiRuntimeCapacitySelectionLevel
                    .ExistingPoolPodProcessCreation =>
                    HasValue(candidate.PoolId) &&
                    HasValue(candidate.HostId) &&
                    !HasValue(candidate.RuntimeInstanceId) &&
                    candidate.AvailableProcessSlots > 0,

                AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation =>
                    HasValue(candidate.PoolId) &&
                    !HasValue(candidate.HostId) &&
                    !HasValue(candidate.RuntimeInstanceId),

                AiRuntimeCapacitySelectionLevel
                    .ExternalNodeCapacityRequest =>
                    !HasValue(candidate.HostId) &&
                    !HasValue(candidate.RuntimeInstanceId),

                AiRuntimeCapacitySelectionLevel.Backpressure =>
                    false,

                _ =>
                    false
            };
        }

        /// <summary>
        /// Returns whether one identity value is populated.
        /// </summary>
        /// <param name="value">The identity value.</param>
        /// <returns>
        /// <see langword="true" /> when the value is populated; otherwise,
        /// <see langword="false" />.
        /// </returns>
        private static bool HasValue(
            string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
