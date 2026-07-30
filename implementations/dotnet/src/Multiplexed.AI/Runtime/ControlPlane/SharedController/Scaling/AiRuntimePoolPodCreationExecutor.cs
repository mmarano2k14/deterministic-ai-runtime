using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Creates one deterministic Kubernetes Runtime Pool Pod through the canonical
    /// <see cref="IAiRuntimeHostManager" /> lifecycle boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executor does not construct Kubernetes resources directly. It validates the
    /// single existing <see cref="AiRuntimeHostCreationMode.KubernetesPool" /> strategy,
    /// invokes it through the runtime host manager, supplies retry-stable first-class
    /// identities, and waits for exact shared-registry membership convergence.
    /// </para>
    /// <para>
    /// Successful requests are deduplicated for the lifetime of this singleton
    /// executor. Rejected requests are not cached, so a retry reuses the same
    /// deterministic host and runtime identities.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePoolPodCreationExecutor :
        IAiRuntimePoolPodCreationExecutor
    {
        private readonly IReadOnlyList<IAiRuntimeHostCreationStrategy>
            hostCreationStrategies;
        private readonly IAiKubernetesRuntimePoolPodMembershipEnumerator
            membershipEnumerator;
        private readonly IAiRuntimeHostManager? runtimeHostManager;
        private readonly AiKubernetesRuntimePoolOptions poolOptions;
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;
        private readonly SemaphoreSlim executionGate = new(1, 1);
        private readonly Dictionary<string, AiRuntimePoolPodCreationResult>
            appliedRequests = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolPodCreationExecutor" /> class.
        /// </summary>
        /// <param name="hostCreationStrategies">
        /// The registered host lifecycle strategies.
        /// </param>
        /// <param name="membershipEnumerator">
        /// The exact shared-registry Pod membership authority.
        /// </param>
        /// <param name="poolOptions">The Kubernetes Runtime Pool options.</param>
        /// <param name="hostOptions">The Kubernetes Runtime Pool host options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when a required dependency is <see langword="null" />.
        /// </exception>
        public AiRuntimePoolPodCreationExecutor(
            IEnumerable<IAiRuntimeHostCreationStrategy>
                hostCreationStrategies,
            IAiKubernetesRuntimePoolPodMembershipEnumerator
                membershipEnumerator,
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions)
            : this(
                MaterializeStrategies(hostCreationStrategies),
                membershipEnumerator,
                poolOptions,
                hostOptions,
                runtimeHostManager: null)
        {
        }

        /// <summary>
        /// Initializes a new instance that routes Pod creation through the canonical
        /// runtime host manager observability boundary.
        /// </summary>
        /// <param name="hostCreationStrategies">
        /// The registered host lifecycle strategies.
        /// </param>
        /// <param name="membershipEnumerator">
        /// The exact shared-registry Pod membership authority.
        /// </param>
        /// <param name="poolOptions">The Kubernetes Runtime Pool options.</param>
        /// <param name="hostOptions">The Kubernetes Runtime Pool host options.</param>
        /// <param name="runtimeHostManager">
        /// The canonical runtime host manager that records host lifecycle evidence.
        /// </param>
        public AiRuntimePoolPodCreationExecutor(
            IEnumerable<IAiRuntimeHostCreationStrategy>
                hostCreationStrategies,
            IAiKubernetesRuntimePoolPodMembershipEnumerator
                membershipEnumerator,
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions,
            IAiRuntimeHostManager runtimeHostManager)
            : this(
                MaterializeStrategies(hostCreationStrategies),
                membershipEnumerator,
                poolOptions,
                hostOptions,
                runtimeHostManager ??
                    throw new ArgumentNullException(
                        nameof(runtimeHostManager)))
        {
        }

        private AiRuntimePoolPodCreationExecutor(
            IReadOnlyList<IAiRuntimeHostCreationStrategy>
                hostCreationStrategies,
            IAiKubernetesRuntimePoolPodMembershipEnumerator
                membershipEnumerator,
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions,
            IAiRuntimeHostManager? runtimeHostManager)
        {
            this.hostCreationStrategies = hostCreationStrategies;

            this.membershipEnumerator =
                membershipEnumerator ??
                throw new ArgumentNullException(
                    nameof(membershipEnumerator));

            this.poolOptions =
                poolOptions?.Value ??
                throw new ArgumentNullException(nameof(poolOptions));

            this.hostOptions =
                hostOptions?.Value ??
                throw new ArgumentNullException(nameof(hostOptions));

            this.runtimeHostManager = runtimeHostManager;
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.ControlPlaneId);
            ArgumentNullException.ThrowIfNull(
                request.ExecutionContextSnapshot);

            this.ValidateCandidate(candidate);
            cancellationToken.ThrowIfCancellationRequested();

            var hostRequestId =
                AiRuntimePoolPodCreationIdentityFactory
                    .CreateHostRequestId(request, candidate);

            var runtimeInstanceIdPrefix =
                ResolveRuntimeInstanceIdPrefix(
                    request,
                    this.poolOptions);

            var primaryRuntimeInstanceId =
                AiRuntimePoolPodCreationIdentityFactory
                    .CreatePrimaryRuntimeInstanceId(
                        runtimeInstanceIdPrefix,
                        request,
                        candidate);

            var requestKey =
                CreateRequestKey(
                    hostRequestId,
                    candidate.PoolId!,
                    primaryRuntimeInstanceId);

            await this.executionGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.appliedRequests.TryGetValue(
                        requestKey,
                        out var applied))
                {
                    return applied with
                    {
                        Status =
                            AiRuntimePoolPodCreationStatus.AlreadyApplied
                    };
                }

                var strategy = this.ResolveKubernetesPoolStrategy();

                var hostRequest =
                    CreateHostRequest(
                        request,
                        candidate,
                        hostRequestId,
                        runtimeInstanceIdPrefix,
                        primaryRuntimeInstanceId,
                        this.poolOptions);

                var startResult =
                    this.runtimeHostManager is null
                        ? await strategy
                            .StartAsync(
                                hostRequest,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await this.runtimeHostManager
                            .StartRuntimeAsync(
                                hostRequest,
                                cancellationToken)
                            .ConfigureAwait(false);

                if (!startResult.Success)
                {
                    return CreateRejected(
                        request,
                        candidate,
                        hostRequestId,
                        primaryRuntimeInstanceId,
                        startResult,
                        startResult.FailureReason ??
                            "kubernetes-runtime-pool-pod-create-rejected",
                        startResult.Retryable);
                }

                if (!StringComparer.Ordinal.Equals(
                        startResult.RuntimeInstanceId,
                        primaryRuntimeInstanceId))
                {
                    return CreateRejected(
                        request,
                        candidate,
                        hostRequestId,
                        primaryRuntimeInstanceId,
                        startResult,
                        "The KubernetesPool host strategy returned a different primary runtime identity.",
                        retryable: false);
                }

                var podUid =
                    ResolvePodUid(startResult);

                ValidateStartMetadata(
                    candidate.PoolId!,
                    podUid,
                    startResult);

                var runtimeInstanceIds =
                    await this.WaitForReadyMembershipAsync(
                            candidate.PoolId!,
                            podUid,
                            primaryRuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var result =
                    new AiRuntimePoolPodCreationResult
                    {
                        RequestId = request.RequestId,
                        PoolId = candidate.PoolId!,
                        HostRequestId = hostRequestId,
                        PrimaryRuntimeInstanceId =
                            primaryRuntimeInstanceId,
                        PodUid = podUid,
                        Status =
                            AiRuntimePoolPodCreationStatus.Created,
                        HostStartResult = startResult,
                        RuntimeInstanceIds = runtimeInstanceIds
                    };

                this.appliedRequests[requestKey] = result;
                return result;
            }
            catch (TimeoutException exception)
            {
                return new AiRuntimePoolPodCreationResult
                {
                    RequestId = request.RequestId,
                    PoolId = candidate.PoolId!,
                    HostRequestId = hostRequestId,
                    PrimaryRuntimeInstanceId =
                        primaryRuntimeInstanceId,
                    Status = AiRuntimePoolPodCreationStatus.Rejected,
                    FailureReason = exception.Message,
                    Retryable = true
                };
            }
            finally
            {
                this.executionGate.Release();
            }
        }

        private static IReadOnlyList<IAiRuntimeHostCreationStrategy>
            MaterializeStrategies(
                IEnumerable<IAiRuntimeHostCreationStrategy>
                    hostCreationStrategies)
        {
            return hostCreationStrategies?.ToArray() ??
                throw new ArgumentNullException(
                    nameof(hostCreationStrategies));
        }

        /// <summary>
        /// Resolves the single registered Kubernetes Runtime Pool host strategy.
        /// </summary>
        private IAiRuntimeHostCreationStrategy
            ResolveKubernetesPoolStrategy()
        {
            var strategies =
                this.hostCreationStrategies
                    .Where(
                        item =>
                            item.Mode ==
                            AiRuntimeHostCreationMode.KubernetesPool)
                    .ToArray();

            if (strategies.Length != 1)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Expected exactly one KubernetesPool host creation strategy, but found ",
                        strategies.Length,
                        "."));
            }

            return strategies[0];
        }

        /// <summary>
        /// Waits until the new Pod exposes exactly the planned ready membership.
        /// </summary>
        private async Task<IReadOnlyList<string>>
            WaitForReadyMembershipAsync(
                string poolId,
                string podUid,
                string primaryRuntimeInstanceId,
                CancellationToken cancellationToken)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(
                    this.hostOptions.StartupTimeout);

            string? lastPendingReason = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var membership =
                        await this.membershipEnumerator
                            .EnumerateAsync(
                                poolId,
                                podUid,
                                cancellationToken)
                            .ConfigureAwait(false);

                    ValidateMembershipAuthority(
                        poolId,
                        podUid,
                        membership);

                    if (membership.Members.Count >
                        this.poolOptions.InitialRuntimeInstanceCount)
                    {
                        throw new InvalidOperationException(
                            string.Concat(
                                "Runtime Pool Pod registered ",
                                membership.Members.Count,
                                " members while exactly ",
                                this.poolOptions
                                    .InitialRuntimeInstanceCount,
                                " were planned."));
                    }

                    if (membership.Members.Count <
                        this.poolOptions.InitialRuntimeInstanceCount)
                    {
                        lastPendingReason =
                            string.Concat(
                                "Runtime Pool Pod has ",
                                membership.Members.Count,
                                " of ",
                                this.poolOptions
                                    .InitialRuntimeInstanceCount,
                                " registered members.");
                    }
                    else if (membership.Members.Any(
                        member =>
                            member.Status !=
                                AiRuntimeInstanceStatus.Ready ||
                            !member.CanAcceptRun))
                    {
                        lastPendingReason =
                            "Runtime Pool Pod membership exists but not every runtime is Ready and selectable.";
                    }
                    else if (!membership.Members.Any(
                        member =>
                            StringComparer.Ordinal.Equals(
                                member.RuntimeInstanceId,
                                primaryRuntimeInstanceId)))
                    {
                        throw new InvalidOperationException(
                            "The deterministic primary runtime did not register in the new Pod membership.");
                    }
                    else
                    {
                        return membership.Members
                            .Select(
                                member =>
                                    member.RuntimeInstanceId)
                            .OrderBy(
                                runtimeInstanceId =>
                                    runtimeInstanceId,
                                StringComparer.Ordinal)
                            .ToArray();
                    }
                }
                catch (
                    AiKubernetesRuntimePoolPodMembershipAuthorityException
                    exception)
                    when (exception.Reason ==
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .MembershipNotFound)
                {
                    lastPendingReason = exception.Message;
                }

                await Task
                    .Delay(
                        this.hostOptions.ReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                lastPendingReason ??
                "Runtime Pool Pod membership did not become exactly ready before the configured deadline.");
        }

        /// <summary>
        /// Validates the selected first-class Pod-creation candidate.
        /// </summary>
        private void ValidateCandidate(
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            var valid =
                candidate.Level ==
                    AiRuntimeCapacitySelectionLevel
                        .RuntimePoolPodCreation &&
                StringComparer.Ordinal.Equals(
                    candidate.PoolId,
                    this.poolOptions.PoolId) &&
                string.IsNullOrWhiteSpace(candidate.HostId) &&
                string.IsNullOrWhiteSpace(
                    candidate.RuntimeInstanceId) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    candidate.ProviderName,
                    this.poolOptions.ProviderName) &&
                candidate.IsCompatible &&
                candidate.IsAvailable &&
                !candidate.IsDraining &&
                !candidate.IsSuppressed;

            if (!valid)
            {
                throw new InvalidOperationException(
                    "The selected candidate is not an exact safe Runtime Pool Pod creation authority.");
            }
        }

        /// <summary>
        /// Validates exact shared-registry membership authority.
        /// </summary>
        private static void ValidateMembershipAuthority(
            string poolId,
            string podUid,
            AiKubernetesRuntimePoolPodMembership membership)
        {
            ArgumentNullException.ThrowIfNull(membership);

            if (!StringComparer.Ordinal.Equals(
                    membership.PoolId,
                    poolId) ||
                !StringComparer.Ordinal.Equals(
                    membership.PodUid,
                    podUid) ||
                membership.Members.Any(
                    member =>
                        !StringComparer.Ordinal.Equals(
                            member.PoolId,
                            poolId) ||
                        !StringComparer.Ordinal.Equals(
                            member.PodUid,
                            podUid)))
            {
                throw new InvalidOperationException(
                    "Runtime Pool Pod membership crossed its exact PoolId or PodUid boundary.");
            }
        }

        /// <summary>
        /// Resolves the exact Kubernetes Pod UID from host metadata.
        /// </summary>
        private static string ResolvePodUid(
            AiRuntimeHostStartResult startResult)
        {
            if (!startResult.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostId,
                    out var podUid) ||
                string.IsNullOrWhiteSpace(podUid))
            {
                throw new InvalidOperationException(
                    "The KubernetesPool host strategy did not return the exact Pod UID.");
            }

            return podUid.Trim();
        }

        /// <summary>
        /// Validates diagnostic host metadata against first-class authority.
        /// </summary>
        private static void ValidateStartMetadata(
            string poolId,
            string podUid,
            AiRuntimeHostStartResult startResult)
        {
            if (!startResult.Metadata.TryGetValue(
                    "runtime.pool.id",
                    out var metadataPoolId) ||
                !StringComparer.Ordinal.Equals(
                    metadataPoolId,
                    poolId))
            {
                throw new InvalidOperationException(
                    "Runtime Pool Pod host metadata does not preserve the exact PoolId authority.");
            }

            if (startResult.Metadata.TryGetValue(
                    "kubernetes.pod.uid",
                    out var metadataPodUid) &&
                !StringComparer.Ordinal.Equals(
                    metadataPodUid,
                    podUid))
            {
                throw new InvalidOperationException(
                    "Runtime Pool Pod host metadata returned conflicting Kubernetes Pod UIDs.");
            }
        }

        /// <summary>
        /// Creates the provider-agnostic host request for the existing KubernetesPool
        /// strategy.
        /// </summary>
        private static AiRuntimeHostStartRequest CreateHostRequest(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            string hostRequestId,
            string runtimeInstanceIdPrefix,
            string primaryRuntimeInstanceId,
            AiKubernetesRuntimePoolOptions poolOptions)
        {
            var snapshot = request.ExecutionContextSnapshot;

            return new AiRuntimeHostStartRequest
            {
                RequestId = hostRequestId,
                ControlPlaneId = request.ControlPlaneId,
                ExecutionContextSnapshot = snapshot,
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = candidate.PoolId,
                HostId = null,
                RuntimeInstanceId =
                    primaryRuntimeInstanceId,
                RuntimeInstanceIdPrefix =
                    runtimeInstanceIdPrefix,
                ProviderName = poolOptions.ProviderName,
                TransportName = poolOptions.TransportName,
                TransportEndpoint = null,
                TenantId = snapshot.TenantId,
                TenantGroupId = snapshot.TenantGroupId,
                IsolationMode = request.IsolationMode.ToString(),
                PreferDedicatedCapacity =
                    request.PreferDedicatedCapacity,
                AllowSharedFallback =
                    request.AllowSharedFallback,
                WorkerCountPerInstance =
                    ResolvePositiveOrDefault(
                        request.WorkerCountPerInstance,
                        1),
                MaxConcurrentRunsPerInstance =
                    ResolvePositiveOrDefault(
                        request.MaxConcurrentRunsPerInstance,
                        1),
                LocalQueueCapacity =
                    ResolvePositiveOrDefault(
                        request.LocalQueueCapacity,
                        100),
                MaxRuntimeInstances =
                    request.MaxRuntimeInstances,
                Metadata =
                    new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Resolves the authoritative runtime identity prefix.
        /// </summary>
        private static string ResolveRuntimeInstanceIdPrefix(
            AiRuntimeScaleOutProviderRequest request,
            AiKubernetesRuntimePoolOptions poolOptions)
        {
            var prefix =
                string.IsNullOrWhiteSpace(
                    request.RuntimeInstanceIdPrefix)
                    ? poolOptions.RuntimeInstanceIdPrefix
                    : request.RuntimeInstanceIdPrefix;

            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new InvalidOperationException(
                    "Runtime Pool Pod creation requires a runtime instance identity prefix.");
            }

            return prefix.Trim();
        }

        /// <summary>
        /// Resolves one optional positive request value.
        /// </summary>
        private static int ResolvePositiveOrDefault(
            int? value,
            int defaultValue)
        {
            return value is > 0
                ? value.Value
                : defaultValue;
        }

        /// <summary>
        /// Creates a successful-request deduplication key.
        /// </summary>
        private static string CreateRequestKey(
            string hostRequestId,
            string poolId,
            string primaryRuntimeInstanceId)
        {
            return string.Join(
                "|",
                hostRequestId,
                poolId,
                primaryRuntimeInstanceId);
        }

        /// <summary>
        /// Creates a rejected Pod-creation result.
        /// </summary>
        private static AiRuntimePoolPodCreationResult CreateRejected(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            string hostRequestId,
            string primaryRuntimeInstanceId,
            AiRuntimeHostStartResult startResult,
            string failureReason,
            bool retryable)
        {
            return new AiRuntimePoolPodCreationResult
            {
                RequestId = request.RequestId,
                PoolId = candidate.PoolId!,
                HostRequestId = hostRequestId,
                PrimaryRuntimeInstanceId =
                    primaryRuntimeInstanceId,
                Status = AiRuntimePoolPodCreationStatus.Rejected,
                HostStartResult = startResult,
                FailureReason = failureReason,
                Retryable = retryable
            };
        }
    }
}
