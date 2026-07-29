using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Creates one bounded runtime child process through the existing local
    /// <see cref="IAiRuntimeProcessPoolManager" /> authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executor never constructs child processes directly. It asks the existing
    /// Runtime Pool Manager to converge to an exact target process count so the
    /// manager's lifecycle gate, identity validation, and child factory remain the only
    /// process-creation authority.
    /// </para>
    /// <para>
    /// Requests are deduplicated by provider request identifier for the lifetime of the
    /// exact host incarnation. Distinct requests may each add one process until the
    /// manager reaches its authoritative maximum process count.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePoolProcessCreationExecutor :
        IAiRuntimePoolProcessCreationExecutor
    {
        private readonly IAiRuntimeProcessPoolManager processPoolManager;
        private readonly SemaphoreSlim executionGate = new(1, 1);
        private readonly Dictionary<string, AiRuntimePoolProcessCreationResult>
            appliedRequests = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolProcessCreationExecutor" /> class.
        /// </summary>
        /// <param name="processPoolManager">
        /// The existing exact Runtime Pool Manager authority.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="processPoolManager" /> is
        /// <see langword="null" />.
        /// </exception>
        public AiRuntimePoolProcessCreationExecutor(
            IAiRuntimeProcessPoolManager processPoolManager)
        {
            this.processPoolManager =
                processPoolManager ??
                throw new ArgumentNullException(nameof(processPoolManager));
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolProcessCreationResult> ExecuteAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);

            ValidateCandidate(candidate);

            cancellationToken.ThrowIfCancellationRequested();

            await this.executionGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var requestKey = CreateRequestKey(request, candidate);

                if (this.appliedRequests.TryGetValue(
                        requestKey,
                        out var appliedResult))
                {
                    return appliedResult with
                    {
                        Status =
                            AiRuntimePoolProcessCreationStatus.AlreadyApplied,
                        CreatedRuntimeInstanceIds = Array.Empty<string>()
                    };
                }

                var before =
                    await this.processPoolManager
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);

                ValidateManagerIdentity(candidate, before);

                var currentProcessCount = before.Children.Count;
                var maximumProcessCount = before.MaximumProcessCount;

                if (currentProcessCount >= maximumProcessCount)
                {
                    var unavailable =
                        new AiRuntimePoolProcessCreationResult
                        {
                            RequestId = request.RequestId,
                            PoolId = before.PoolId,
                            HostId = before.HostId,
                            Status =
                                AiRuntimePoolProcessCreationStatus
                                    .CapacityUnavailable,
                            ProcessCountBefore = currentProcessCount,
                            ProcessCountAfter = currentProcessCount,
                            MaximumProcessCount = maximumProcessCount
                        };

                    return unavailable;
                }

                var targetProcessCount =
                    checked(currentProcessCount + 1);

                var after =
                    await this.processPoolManager
                        .EnsureCapacityAsync(
                            targetProcessCount,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateManagerIdentity(candidate, after);
                ValidateConvergedCapacity(
                    targetProcessCount,
                    maximumProcessCount,
                    after);

                var beforeRuntimeIds =
                    before.Children
                        .Select(child => child.RuntimeInstanceId)
                        .ToHashSet(StringComparer.Ordinal);

                var createdRuntimeIds =
                    after.Children
                        .Select(child => child.RuntimeInstanceId)
                        .Where(runtimeInstanceId =>
                            !beforeRuntimeIds.Contains(runtimeInstanceId))
                        .OrderBy(
                            runtimeInstanceId => runtimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                var result =
                    new AiRuntimePoolProcessCreationResult
                    {
                        RequestId = request.RequestId,
                        PoolId = after.PoolId,
                        HostId = after.HostId,
                        Status = createdRuntimeIds.Length > 0
                            ? AiRuntimePoolProcessCreationStatus.Created
                            : AiRuntimePoolProcessCreationStatus.AlreadyApplied,
                        ProcessCountBefore = currentProcessCount,
                        ProcessCountAfter = after.Children.Count,
                        MaximumProcessCount = after.MaximumProcessCount,
                        CreatedRuntimeInstanceIds = createdRuntimeIds
                    };

                this.appliedRequests.Add(requestKey, result);
                return result;
            }
            finally
            {
                this.executionGate.Release();
            }
        }

        /// <summary>
        /// Validates that the selected hierarchy candidate targets process creation in
        /// one exact existing Runtime Pool host.
        /// </summary>
        /// <param name="candidate">The selected candidate.</param>
        private static void ValidateCandidate(
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            if (candidate.Level !=
                    AiRuntimeCapacitySelectionLevel
                        .ExistingPoolPodProcessCreation ||
                string.IsNullOrWhiteSpace(candidate.PoolId) ||
                string.IsNullOrWhiteSpace(candidate.HostId) ||
                !string.IsNullOrWhiteSpace(candidate.RuntimeInstanceId) ||
                !candidate.IsCompatible ||
                !candidate.IsAvailable ||
                candidate.IsDraining ||
                candidate.IsSuppressed ||
                candidate.AvailableProcessSlots <= 0)
            {
                throw new InvalidOperationException(
                    "The process creation candidate must identify one exact existing Runtime Pool host with available process capacity.");
            }
        }

        /// <summary>
        /// Validates that the manager snapshot belongs to the selected Pool and exact
        /// host incarnation.
        /// </summary>
        /// <param name="candidate">The selected hierarchy candidate.</param>
        /// <param name="snapshot">The manager snapshot.</param>
        private static void ValidateManagerIdentity(
            AiRuntimeCapacitySelectionCandidate candidate,
            AiRuntimeProcessPoolSnapshot snapshot)
        {
            if (!StringComparer.Ordinal.Equals(
                    candidate.PoolId,
                    snapshot.PoolId) ||
                !StringComparer.Ordinal.Equals(
                    candidate.HostId,
                    snapshot.HostId))
            {
                throw new InvalidOperationException(
                    "The selected Runtime Pool host does not match the local process manager identity.");
            }
        }

        /// <summary>
        /// Validates exact target convergence without exceeding the authoritative host
        /// process limit.
        /// </summary>
        /// <param name="targetProcessCount">The exact requested target.</param>
        /// <param name="maximumProcessCount">
        /// The maximum observed before mutation.
        /// </param>
        /// <param name="snapshot">The converged manager snapshot.</param>
        private static void ValidateConvergedCapacity(
            int targetProcessCount,
            int maximumProcessCount,
            AiRuntimeProcessPoolSnapshot snapshot)
        {
            if (snapshot.MaximumProcessCount != maximumProcessCount ||
                snapshot.Children.Count < targetProcessCount ||
                snapshot.Children.Count > snapshot.MaximumProcessCount)
            {
                throw new InvalidOperationException(
                    "The Runtime Pool Manager did not converge to bounded process capacity.");
            }
        }

        /// <summary>
        /// Creates the exact host-scoped idempotency key for one provider request.
        /// </summary>
        /// <param name="request">The provider-level request.</param>
        /// <param name="candidate">The selected host candidate.</param>
        /// <returns>The exact request key.</returns>
        private static string CreateRequestKey(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            return string.Concat(
                candidate.PoolId,
                "\n",
                candidate.HostId,
                "\n",
                request.RequestId);
        }
    }
}
