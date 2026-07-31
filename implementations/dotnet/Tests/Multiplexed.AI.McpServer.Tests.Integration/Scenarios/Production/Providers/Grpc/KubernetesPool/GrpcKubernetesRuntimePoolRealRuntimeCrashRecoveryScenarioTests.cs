using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.KubernetesPool
{
    /// <summary>
    /// Proves runtime-process and whole-Pod recovery in one real bounded gRPC Kubernetes Runtime Pool scenario.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GrpcKubernetesRuntimePoolCrashRecoveryCollection
    {
        /// <summary>
        /// Gets the non-parallel collection name used by the destructive Kubernetes proof.
        /// </summary>
        public const string Name =
            "gRPC Kubernetes Runtime Pool crash recovery collection";
    }

    /// <summary>
    /// Provides shared real-Kubernetes process and Pod failure authority for gRPC Runtime Pool scenarios.
    /// </summary>
    public abstract class GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase :
        ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper output;
        private readonly IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile;
        private readonly ConcurrentDictionary<string, RuntimePoolAllInOneFailureState> states =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the bounded Runtime Pool scenario profile used by this proof.
        /// </summary>
        protected IRuntimePoolCrashRecoveryScenarioRuntimeProfile RuntimePoolProfile =>
            profile;

        /// <summary>
        /// Initializes a real gRPC Kubernetes Runtime Pool crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The bounded Runtime Pool scenario profile.</param>
        protected GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase(
            ITestOutputHelper output,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
            : base(
                output,
                profile)
        {
            this.output = output;
            this.profile = profile;
        }

        /// <inheritdoc />
        protected override AiRunPlacementDirective? CreateRemainingInventoryRunPlacementDirective(
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return new AiRunPlacementDirective
            {
                Target = new AiRunPlacementTarget
                {
                    RuntimeInstanceId = runtimeInstanceId
                },
                Requirement = AiRunPlacementRequirement.Required,
                Fallback = AiRunPlacementFallback.Reject
            };
        }

        /// <inheritdoc />
        protected override async Task AssertRuntimeBelongsToTenantAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(tenant);

            var snapshot =
                await GetRequiredRuntimeSnapshotAsync(
                        registry,
                        runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(
                tenant.TenantId,
                snapshot.TenantId);
        }

        /// <inheritdoc />
        protected override IAiRuntimeHostProcessControl ResolveProcessControl(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return UnsupportedRuntimePoolProcessControl.Instance;
        }

        /// <inheritdoc />
        protected override Task OnCrashRecoveryScenarioStartingAsync(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            states.TryAdd(
                controlPlaneId,
                new RuntimePoolAllInOneFailureState());

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override async Task OnCrashRecoveryScenarioCompletedAsync(
            string controlPlaneId)
        {
            await CleanupControlPlanePodsAsync(
                    controlPlaneId)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecuteImpactedTenantFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var phase =
                context.RuntimePoolFailurePhase
                ?? throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool scenario requires an explicit physical failure phase.");

            var state =
                states.GetOrAdd(
                    context.ControlPlaneId,
                    _ => new RuntimePoolAllInOneFailureState());

            return phase.FailureKind switch
            {
                RuntimePoolCrashFailureKind.RuntimeProcess =>
                    await ExecuteRuntimeProcessFailureAsync(
                            context,
                            state)
                        .ConfigureAwait(false),

                RuntimePoolCrashFailureKind.KubernetesPod =>
                    await ExecutePodFailureAsync(
                            context,
                            state)
                        .ConfigureAwait(false),

                _ =>
                    throw new InvalidOperationException(
                        string.Concat(
                            "Unsupported Runtime Pool failure kind '",
                            phase.FailureKind,
                            "'."))
            };
        }

        private async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecuteRuntimeProcessFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context,
                RuntimePoolAllInOneFailureState state)
        {
            try
            {
                var poolId = ResolvePoolId(context.ControlPlaneId);
                var target =
                    await GetRequiredRuntimeSnapshotAsync(
                            context.Registry,
                            context.Inventory.RuntimeInstanceId)
                        .ConfigureAwait(false);

                AssertRuntimePoolIdentity(
                    target,
                    poolId);

                await state.TrackCurrentPoolPodsAsync(
                        context.Registry,
                        poolId)
                    .ConfigureAwait(false);

                var membershipEnumerator =
                    context.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodMembershipEnumerator>();

                var membership =
                    await membershipEnumerator
                        .EnumerateAsync(
                            poolId,
                            target.HostId!)
                        .ConfigureAwait(false);

                Assert.Equal(
                    profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                    membership.Members.Count);

                var siblingRuntimeInstanceIds =
                    membership.Members
                        .Where(
                            member =>
                                !StringComparer.Ordinal.Equals(
                                    member.RuntimeInstanceId,
                                    target.RuntimeInstanceId))
                        .Select(member => member.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                Assert.NotEmpty(siblingRuntimeInstanceIds);
                state.SetRuntimeFailureHostId(target.HostId!);

                var recovery =
                    await ExecuteAssignedInventoryFailureAsync(
                            context,
                            new KubernetesRuntimePoolChildProcessControl(
                                context.Registry,
                                poolId,
                                output))
                        .ConfigureAwait(false);

                await AssertExactSiblingsRemainReadyAsync(
                        context.Registry,
                        target.HostId!,
                        siblingRuntimeInstanceIds,
                        context.RedispatchTimeout)
                    .ConfigureAwait(false);

                await AssertBoundedPhysicalPodCountAsync(
                        state)
                    .ConfigureAwait(false);

                state.CompleteRuntimeFailure();
                return recovery;
            }
            catch (Exception exception)
            {
                state.FailRuntimeFailure(exception);
                throw;
            }
        }

        private async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecutePodFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context,
                RuntimePoolAllInOneFailureState state)
        {
            var hasPriorRuntimeFailure =
                profile.CrashRecoveryPlan.FailurePhases.Any(
                    phase =>
                        phase.FailureKind ==
                            RuntimePoolCrashFailureKind.RuntimeProcess);

            if (hasPriorRuntimeFailure)
            {
                await state.RuntimeFailureCompletion
                    .WaitAsync(TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);
            }

            var poolId = ResolvePoolId(context.ControlPlaneId);
            var target =
                await GetRequiredRuntimeSnapshotAsync(
                        context.Registry,
                        context.Inventory.RuntimeInstanceId)
                    .ConfigureAwait(false);

            AssertRuntimePoolIdentity(
                target,
                poolId);

            if (hasPriorRuntimeFailure)
            {
                Assert.NotEqual(
                    state.RuntimeFailureHostId,
                    target.HostId);
            }

            await state.TrackCurrentPoolPodsAsync(
                    context.Registry,
                    poolId)
                .ConfigureAwait(false);

            var membershipEnumerator =
                context.Services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodMembershipEnumerator>();

            var failedMembership =
                await membershipEnumerator
                    .EnumerateAsync(
                        poolId,
                        target.HostId!)
                    .ConfigureAwait(false);

            Assert.Equal(
                profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                failedMembership.Members.Count);

            var survivingHostIds =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        profile.CrashRecoveryPlan.InitialPodCount,
                        context.RedispatchTimeout)
                    .ConfigureAwait(false);

            Assert.Equal(
                profile.CrashRecoveryPlan.InitialPodCount,
                survivingHostIds.Count);

            survivingHostIds.Remove(target.HostId!);

            if (hasPriorRuntimeFailure)
            {
                Assert.Contains(
                    state.RuntimeFailureHostId,
                    survivingHostIds);
            }

            Assert.NotEmpty(survivingHostIds);

            var coordinator =
                context.Services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator>();

            var podControl =
                new KubernetesRuntimePoolPodFailureProcessControl(
                    target,
                    coordinator,
                    CreateHostStartTemplate(
                        context,
                        target,
                        poolId),
                    output);

            var recovery =
                await ExecuteAssignedInventoryFailureAsync(
                        context,
                        podControl)
                    .ConfigureAwait(false);

            var podRecovery =
                await podControl.RecoveryTask
                    .WaitAsync(
                        TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                podRecovery.Status);
            Assert.NotNull(podRecovery.Replacement);
            Assert.NotNull(podRecovery.Recovery);
            Assert.NotEqual(
                target.HostId,
                podRecovery.Replacement!.ReplacementPodUid);
            Assert.Equal(
                profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                podRecovery.Replacement.Membership.Members.Count);

            var failedRuntimeInstanceIds =
                failedMembership.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.DoesNotContain(
                podRecovery.Replacement.Membership.Members,
                member =>
                    failedRuntimeInstanceIds.Contains(
                        member.RuntimeInstanceId));

            if (podRecovery.Replacement.HostStartResult.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostName,
                    out var replacementPodName) &&
                !string.IsNullOrWhiteSpace(replacementPodName))
            {
                state.TrackPod(
                    target.KubernetesNamespace!,
                    replacementPodName);
            }

            await AssertSurvivingHostsRemainReadyAsync(
                    context.Registry,
                    poolId,
                    survivingHostIds,
                    context.RedispatchTimeout)
                .ConfigureAwait(false);

            await AssertBoundedPhysicalPodCountAsync(
                    state)
                .ConfigureAwait(false);

            return recovery;
        }

        protected string ResolvePoolId(
            string controlPlaneId)
        {
            return RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                profile.PoolIdPrefix,
                controlPlaneId);
        }

        private async Task AssertBoundedPhysicalPodCountAsync(
            RuntimePoolAllInOneFailureState state)
        {
            var trackedPods = state.GetTrackedPods();

            var existenceResults =
                await Task.WhenAll(
                        trackedPods.Select(
                            trackedPod =>
                                RunKubectlAsync(
                                    CancellationToken.None,
                                    "get",
                                    "pod",
                                    trackedPod.PodName,
                                    "--namespace",
                                    trackedPod.Namespace,
                                    "--output=name")))
                    .ConfigureAwait(false);

            var existingPodCount =
                existenceResults.Count(result => result.ExitCode == 0);

            Assert.InRange(
                existingPodCount,
                1,
                profile.CrashRecoveryPlan.MaximumPodCount);
        }

        private static async Task<HashSet<string>> GetActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        protected static async Task<HashSet<string>> WaitForActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            int expectedHostCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfLessThan(expectedHostCount, 1);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            HashSet<string> activeHostIds =
                new(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                activeHostIds =
                    await GetActiveHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (activeHostIds.Count == expectedHostCount)
                {
                    return activeHostIds;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "The bounded Runtime Pool did not expose the expected active Pod count before failure injection. PoolId='",
                    poolId,
                    "', ExpectedHostCount='",
                    expectedHostCount,
                    "', ActualHostCount='",
                    activeHostIds.Count,
                    "'."));
        }

        private static async Task<HashSet<string>> GetSelectableHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        snapshot.CanAcceptRun &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static async Task AssertExactSiblingsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string hostId,
            IReadOnlyCollection<string> siblingRuntimeInstanceIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await Task.WhenAll(
                            siblingRuntimeInstanceIds.Select(
                                runtimeInstanceId =>
                                    registry.GetAsync(runtimeInstanceId)))
                        .ConfigureAwait(false);

                if (snapshots.All(
                        snapshot =>
                            snapshot is not null &&
                            StringComparer.Ordinal.Equals(
                                snapshot.HostId,
                                hostId) &&
                            snapshot.Status ==
                                AiRuntimeInstanceStatus.Ready))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "The healthy Runtime Pool siblings did not remain ready after the exact child-process kill.");
        }

        private static async Task AssertSurvivingHostsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            IReadOnlySet<string> survivingHostIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var activeHostIds =
                    await GetSelectableHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (survivingHostIds.All(activeHostIds.Contains))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "At least one healthy Runtime Pool Pod lost selectable membership during another Pod's recovery.");
        }

        protected static async Task<AiRuntimeInstanceSnapshot>
            GetRequiredRuntimeSnapshotAsync(
                IAiRuntimeInstanceRegistry registry,
                string runtimeInstanceId)
        {
            var snapshot =
                await registry
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            return snapshot
                ?? throw new InvalidOperationException(
                    string.Concat(
                        "Runtime instance '",
                        runtimeInstanceId,
                        "' was not found in the shared registry."));
        }

        private static void AssertRuntimePoolIdentity(
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            Assert.Equal(poolId, snapshot.PoolId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HostId));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesNamespace));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesPodName));
        }

        private static AiRuntimeHostStartRequest CreateHostStartTemplate(
            ProcessHostCrashRecoveryFailureExecutionContext context,
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            var tenantId =
                snapshot.TenantId
                ?? throw new InvalidOperationException(
                    "The failed Kubernetes Runtime Pool member must expose its first-class TenantId.");

            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        "mcp-pod-recovery-template-",
                        context.ControlPlaneId),
                ControlPlaneId = context.ControlPlaneId,
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                RuntimeInstanceIdPrefix =
                    string.Concat(poolId, "-runtime"),
                ProviderName = "grpc",
                TransportName = "grpc",
                TenantId = tenantId,
                TenantGroupId = snapshot.TenantGroupId,
                IsolationMode = "Dedicated",
                PreferDedicatedCapacity = true,
                AllowSharedFallback = true,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 2,
                MaxRuntimeInstances = 3,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-pod-recovery-",
                                context.ControlPlaneId,
                                "-",
                                tenantId),
                        Project =
                            "mcp-kubernetes-runtime-pool-crash-recovery",
                        UserId = "system",
                        TenantId = tenantId,
                        TenantGroupId = snapshot.TenantGroupId,
                        CurrentNamespace = "tests",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    },
                Metadata = new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Performs a final best-effort cleanup for any scenario state left after the test method exits.
        /// </summary>
        /// <returns>A task that completes when all remaining tracked pools have been cleaned.</returns>
        protected async Task CleanupAllTrackedPodsAsync()
        {
            var controlPlaneIds =
                states.Keys.ToArray();

            var failures =
                new List<Exception>();

            foreach (var controlPlaneId in controlPlaneIds)
            {
                try
                {
                    await CleanupControlPlanePodsAsync(
                            controlPlaneId)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "At least one Kubernetes Runtime Pool scenario could not clean all of its Pods.",
                    failures);
            }
        }

        private async Task CleanupControlPlanePodsAsync(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var poolId = ResolvePoolId(controlPlaneId);

            states.TryGetValue(
                controlPlaneId,
                out var state);

            var trackedPods =
                state?.GetTrackedPods()
                ?? Array.Empty<TrackedPod>();

            IReadOnlyCollection<TrackedPod> discoveredPods;

            try
            {
                discoveredPods =
                    await DiscoverPoolPodsAsync(poolId)
                        .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL CLEANUP DISCOVERY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");

                discoveredPods =
                    Array.Empty<TrackedPod>();
            }

            var podsToDelete =
                trackedPods
                    .Concat(discoveredPods)
                    .Distinct()
                    .ToArray();

            output.WriteLine(
                $"[GRPC KUBERNETES RUNTIME POOL SCENARIO CLEANUP START] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', PodCount='{podsToDelete.Length}'.");

            foreach (var trackedPod in podsToDelete)
            {
                try
                {
                    var deleteResult =
                        await RunKubectlAsync(
                                CancellationToken.None,
                                "delete",
                                "pod",
                                trackedPod.PodName,
                                "--namespace",
                                trackedPod.Namespace,
                                "--ignore-not-found=true",
                                "--grace-period=0",
                                "--force",
                                "--wait=true",
                                "--timeout=90s")
                            .ConfigureAwait(false);

                    if (deleteResult.ExitCode != 0)
                    {
                        output.WriteLine(
                            $"[GRPC KUBERNETES RUNTIME POOL CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', StandardError='{deleteResult.StandardError}'.");
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[GRPC KUBERNETES RUNTIME POOL CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', Message='{exception.Message}'.");
                }
            }

            var cleanupDeadline =
                DateTimeOffset.UtcNow.AddSeconds(90);

            IReadOnlyCollection<TrackedPod> remainingPods =
                Array.Empty<TrackedPod>();

            while (DateTimeOffset.UtcNow < cleanupDeadline)
            {
                try
                {
                    remainingPods =
                        await DiscoverPoolPodsAsync(poolId)
                            .ConfigureAwait(false);

                    if (remainingPods.Count == 0)
                    {
                        states.TryRemove(
                            controlPlaneId,
                            out _);

                        output.WriteLine(
                            $"[GRPC KUBERNETES RUNTIME POOL SCENARIO CLEANUP COMPLETE] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', RemainingPodCount='0'.");

                        return;
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[GRPC KUBERNETES RUNTIME POOL CLEANUP VERIFY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "Kubernetes Runtime Pool scenario cleanup left Pods behind. ControlPlaneId='",
                    controlPlaneId,
                    "', PoolId='",
                    poolId,
                    "', RemainingPods='",
                    string.Join(
                        ",",
                        remainingPods.Select(pod => pod.PodName)),
                    "'."));
        }

        private static async Task<IReadOnlyCollection<TrackedPod>>
            DiscoverPoolPodsAsync(
                string poolId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var result =
                await RunKubectlAsync(
                        CancellationToken.None,
                        "get",
                        "pods",
                        "--namespace",
                        KubernetesRuntimePoolScenarioConstants.Namespace,
                        "--output=json")
                    .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Runtime Pool Pods could not be listed for cleanup. StandardError=",
                        result.StandardError));
            }

            using var document =
                JsonDocument.Parse(result.StandardOutput);

            var pods =
                new List<TrackedPod>();

            if (!document.RootElement.TryGetProperty(
                    "items",
                    out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return pods;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty(
                        "metadata",
                        out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty(
                        "annotations",
                        out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Object ||
                    !annotations.TryGetProperty(
                        "multiplexed.ai/pool-id",
                        out var poolIdAnnotation) ||
                    poolIdAnnotation.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(
                        poolIdAnnotation.GetString(),
                        poolId) ||
                    !metadata.TryGetProperty(
                        "name",
                        out var podNameElement))
                {
                    continue;
                }

                var podName =
                    podNameElement.GetString();

                if (string.IsNullOrWhiteSpace(podName))
                {
                    continue;
                }

                var @namespace =
                    KubernetesRuntimePoolScenarioConstants.Namespace;

                if (metadata.TryGetProperty(
                        "namespace",
                        out var namespaceElement) &&
                    !string.IsNullOrWhiteSpace(namespaceElement.GetString()))
                {
                    @namespace = namespaceElement.GetString()!;
                }

                pods.Add(
                    new TrackedPod(
                        @namespace,
                        podName));
            }

            return pods;
        }

        private static async Task<KubectlResult> RunKubectlAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process =
                new System.Diagnostics.Process
                {
                    StartInfo = startInfo
                };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "kubectl could not be started.");
            }

            var standardOutputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            await process
                .WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);

            return new KubectlResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }

        private sealed class KubernetesRuntimePoolChildProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly string poolId;
            private readonly ITestOutputHelper output;

            public KubernetesRuntimePoolChildProcessControl(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                ITestOutputHelper output)
            {
                this.registry = registry;
                this.poolId = poolId;
                this.output = output;
            }

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    await GetRequiredRuntimeSnapshotAsync(
                            registry,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                AssertRuntimePoolIdentity(
                    snapshot,
                    poolId);
                Assert.True(snapshot.ProcessId.HasValue);

                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL PROCESS KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ProcessId='{snapshot.ProcessId}'.");

                var result =
                    await RunKubectlAsync(
                            cancellationToken,
                            "exec",
                            snapshot.KubernetesPodName!,
                            "--namespace",
                            snapshot.KubernetesNamespace!,
                            "--container",
                            "runtime-pool",
                            "--",
                            "sh",
                            "-c",
                            string.Concat(
                                "kill -9 ",
                                snapshot.ProcessId.Value.ToString(
                                    CultureInfo.InvariantCulture)))
                        .ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The in-Pod runtime process could not be killed. StandardError=",
                            result.StandardError));
                }

                return true;
            }
        }

        private sealed class KubernetesRuntimePoolPodFailureProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly AiRuntimeInstanceSnapshot target;
            private readonly IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator coordinator;
            private readonly AiRuntimeHostStartRequest hostStartTemplate;
            private readonly ITestOutputHelper output;
            private Task<AiKubernetesRuntimePoolPodFailureRecoveryResult>?
                recoveryTask;
            private int executed;

            public KubernetesRuntimePoolPodFailureProcessControl(
                AiRuntimeInstanceSnapshot target,
                IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator coordinator,
                AiRuntimeHostStartRequest hostStartTemplate,
                ITestOutputHelper output)
            {
                this.target = target;
                this.coordinator = coordinator;
                this.hostStartTemplate = hostStartTemplate;
                this.output = output;
            }

            public Task<AiKubernetesRuntimePoolPodFailureRecoveryResult>
                RecoveryTask =>
                    recoveryTask
                    ?? throw new InvalidOperationException(
                        "Pod recovery has not been started.");

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref executed, 1) != 0)
                {
                    return false;
                }

                if (!StringComparer.Ordinal.Equals(
                        runtimeInstanceId,
                        target.RuntimeInstanceId))
                {
                    throw new InvalidOperationException(
                        "The Pod failure control received a different RuntimeInstanceId.");
                }

                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL POD KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{target.HostId}', PodName='{target.KubernetesPodName}'.");

                var deleteResult =
                    await RunKubectlAsync(
                            cancellationToken,
                            "delete",
                            "pod",
                            target.KubernetesPodName!,
                            "--namespace",
                            target.KubernetesNamespace!,
                            "--grace-period=0",
                            "--force",
                            "--wait=true",
                            "--timeout=90s")
                        .ConfigureAwait(false);

                if (deleteResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The Kubernetes Runtime Pool Pod could not be deleted. StandardError=",
                            deleteResult.StandardError));
                }

                recoveryTask =
                    coordinator.RecoverAsync(
                        new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                        {
                            FailureId =
                                string.Concat(
                                    "mcp-kubernetes-pod-failure-",
                                    target.HostId),
                            PoolId = target.PoolId!,
                            PodUid = target.HostId!,
                            ClaimedBy =
                                "mcp-grpc-kubernetes-runtime-pool-scenario",
                            FailureMessage =
                                "Forced Kubernetes Runtime Pool Pod deletion in the MCP recovery proof.",
                            HostStartTemplate = hostStartTemplate
                        },
                        CancellationToken.None);

                return true;
            }
        }

        private sealed class RuntimePoolAllInOneFailureState
        {
            private readonly TaskCompletionSource<bool> runtimeFailureCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ConcurrentDictionary<TrackedPod, byte> trackedPods =
                new();

            public Task RuntimeFailureCompletion =>
                runtimeFailureCompletion.Task;

            public string RuntimeFailureHostId { get; private set; } =
                string.Empty;

            public void SetRuntimeFailureHostId(
                string hostId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
                RuntimeFailureHostId = hostId;
            }

            public void CompleteRuntimeFailure()
            {
                runtimeFailureCompletion.TrySetResult(true);
            }

            public void FailRuntimeFailure(
                Exception exception)
            {
                runtimeFailureCompletion.TrySetException(exception);
            }

            public async Task TrackCurrentPoolPodsAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId)
            {
                var snapshots =
                    await registry
                        .ListAsync(includeStopped: true)
                        .ConfigureAwait(false);

                foreach (var snapshot in snapshots.Where(
                             snapshot =>
                                 StringComparer.Ordinal.Equals(
                                     snapshot.PoolId,
                                     poolId) &&
                                 !string.IsNullOrWhiteSpace(
                                     snapshot.KubernetesNamespace) &&
                                 !string.IsNullOrWhiteSpace(
                                     snapshot.KubernetesPodName)))
                {
                    TrackPod(
                        snapshot.KubernetesNamespace!,
                        snapshot.KubernetesPodName!);
                }
            }

            public void TrackPod(
                string @namespace,
                string podName)
            {
                trackedPods.TryAdd(
                    new TrackedPod(
                        @namespace,
                        podName),
                    0);
            }

            public IReadOnlyCollection<TrackedPod> GetTrackedPods()
            {
                return trackedPods.Keys.ToArray();
            }
        }

        private sealed class UnsupportedRuntimePoolProcessControl :
            IAiRuntimeHostProcessControl
        {
            public static UnsupportedRuntimePoolProcessControl Instance { get; } =
                new();

            public Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Runtime Pool physical failures must be executed through the explicit failure-phase hook.");
            }
        }

        private sealed record TrackedPod(
            string Namespace,
            string PodName);

        private sealed record KubectlResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);
    }

    /// <summary>
    /// Executes the existing three-tenant all-in-one MCP crash-recovery proof against a real Kubernetes Runtime Pool.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolCrashRecovery")]
    public sealed class GrpcKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        /// <summary>
        /// Initializes the real gRPC Kubernetes Runtime Pool crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies in one scenario that a child-process kill preserves its siblings, a later Pod deletion
        /// suppresses the exact Pod membership, healthy Pods remain available, and all durable work recovers once.
        /// </summary>
        /// <returns>A task that completes when the all-in-one proof has finished.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_Should_Recover_Runtime_And_Pod_Failures_Without_Impacting_Safe_Tenant()
        {
            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executes a production-like mixed-admission Runtime Pool simulation before reusing
    /// the existing global crash-recovery scenario.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolExistingCapacityProduction")]
    public sealed class GrpcKubernetesRuntimePoolExistingCapacityProductionScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        private const int ProbeStepCount = 5;
        private const int ProbeDelayMs = 50;
        private readonly ConcurrentDictionary<string, byte> sharedRuntimeInstanceIds =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> inventoryRuntimeInstanceIdsByTenantId =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> productionPreludeScaleOutSharedRunIds =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes the production-like existing-capacity and crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolExistingCapacityProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessScaleOutTimeoutOverride =>
            TimeSpan.FromMinutes(4);

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessProgressTimeoutOverride =>
            TimeSpan.FromMinutes(5);

        /// <inheritdoc />
        protected override bool UsesProductionTrafficPrelude => true;

        /// <inheritdoc />
        protected override bool WaitsForFirstInventoryScaleOutFulfillment => false;

        /// <inheritdoc />
        protected override IReadOnlyCollection<string> AdditionalControlPlaneLedgerSharedRunIds =>
            productionPreludeScaleOutSharedRunIds.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        /// <inheritdoc />
        protected override AiRunPlacementDirective? CreateFirstInventoryRunPlacementDirective(
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);

            if (!inventoryRuntimeInstanceIdsByTenantId.TryGetValue(
                    tenant.TenantId,
                    out var runtimeInstanceId) ||
                string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                throw new InvalidOperationException(
                    $"The production warm-capacity proof did not record a compatible first-inventory runtime. TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}'.");
            }

            return new AiRunPlacementDirective
            {
                Target = new AiRunPlacementTarget
                {
                    RuntimeInstanceId = runtimeInstanceId
                },
                Requirement = AiRunPlacementRequirement.Required,
                Fallback = AiRunPlacementFallback.Reject
            };
        }

        /// <summary>
        /// Proves that Dedicated, Hybrid, and Shared tenants warm their policy-compatible
        /// capacity and that a second traffic wave reuses those existing runtime identities
        /// without creating another Pod before the global crash-recovery proof begins.
        /// </summary>
        /// <returns>A task that completes when the complete production simulation converges.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_Should_Reuse_Existing_Admission_Visible_Capacity_Before_Global_Crash_Recovery()
        {
            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        protected override ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryScenario(
            bool includeSafeTenant = false)
        {
            var scenario =
                base.CreateRealRuntimeCrashRecoveryScenario(
                    includeSafeTenant: true);

            /*
             * Tenant A receives the runtime-process failure. Hybrid is safe here because
             * the existing pool manager replaces only the exact child process and preserves
             * the host configuration. Tenant B receives the whole-Pod failure and remains
             * Dedicated, preserving the already-proven Pod replacement template.
             */
            var tenantA =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-a")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Hybrid,
                    ExpectDedicatedRuntimePrefix = false
                };

            var tenantB =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-b")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Dedicated,
                    ExpectDedicatedRuntimePrefix = false
                };

            var safeTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-safe")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Shared,
                    ExpectDedicatedRuntimePrefix = false
                };

            return scenario with
            {
                Name =
                    "grpc-kubernetes-runtime-pool-existing-capacity-production",
                ControlPlaneIdPrefix =
                    "grpc-kubernetes-runtime-pool-existing-capacity-production",
                Tenants = new[]
                {
                    tenantA,
                    tenantB,
                    safeTenant
                }
            };
        }

        /// <inheritdoc />
        protected override bool IsSafeTenantRuntimeCapacityEligibleForImpactedRecovery(
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return sharedRuntimeInstanceIds.ContainsKey(
                runtimeInstanceId);
        }

        /// <inheritdoc />
        protected override async Task AssertRuntimeBelongsToTenantAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(tenant);

            var snapshot =
                await GetRequiredRuntimeSnapshotAsync(
                        registry,
                        runtimeInstanceId)
                    .ConfigureAwait(false);

            if (tenant.RuntimeMode ==
                ProductionTenantRuntimeMode.Dedicated)
            {
                Assert.Equal(
                    tenant.TenantId,
                    snapshot.TenantId);

                return;
            }

            var usesKnownSharedCapacity =
                sharedRuntimeInstanceIds.ContainsKey(
                    runtimeInstanceId);

            if (tenant.RuntimeMode ==
                ProductionTenantRuntimeMode.Shared)
            {
                Assert.True(
                    usesKnownSharedCapacity,
                    $"Shared tenant '{tenant.TenantId}' recovered on runtime '{runtimeInstanceId}', which was not part of the admission-proven shared warm capacity.");

                return;
            }

            Assert.Equal(
                ProductionTenantRuntimeMode.Hybrid,
                tenant.RuntimeMode);

            Assert.True(
                StringComparer.Ordinal.Equals(
                    tenant.TenantId,
                    snapshot.TenantId) ||
                usesKnownSharedCapacity,
                $"Hybrid tenant '{tenant.TenantId}' recovered on runtime '{runtimeInstanceId}' owned by TenantId='{snapshot.TenantId ?? string.Empty}', which is neither tenant-owned nor part of the admission-proven shared warm capacity.");
        }

        /// <inheritdoc />
        protected override async Task ExecuteProductionTrafficPreludeAsync(
            ProcessHostProductionTrafficPreludeContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var orderedTenants =
                context.Tenants
                    .OrderBy(
                        tenant =>
                            GetRuntimeModeOrder(
                                tenant.Tenant.RuntimeMode))
                    .ToArray();

            Assert.Equal(3, orderedTenants.Length);
            Assert.Equal(
                new[]
                {
                    ProductionTenantRuntimeMode.Dedicated,
                    ProductionTenantRuntimeMode.Hybrid,
                    ProductionTenantRuntimeMode.Shared
                },
                orderedTenants
                    .Select(tenant => tenant.Tenant.RuntimeMode)
                    .ToArray());

            context.Output.WriteLine(string.Empty);
            context.Output.WriteLine(
                "# PRODUCTION TRAFFIC PRELUDE - MIXED ADMISSION AND EXISTING CAPACITY REUSE");
            context.Output.WriteLine(
                "[PASS TARGET] Warm Dedicated, Hybrid, and Shared capacity in policy order, validate the durable typed admission request, complete one traffic wave, then prove a second unpinned wave dispatches only to the already-existing RuntimeInstanceId and HostId sets without creating another Pod.");

            var scaleOutRequestStore =
                context.Services.GetRequiredService<
                    IAiRuntimeScaleOutRequestStore>();

            productionPreludeScaleOutSharedRunIds.Clear();

            var firstWave =
                new List<ProductionTrafficDispatchProof>(
                    orderedTenants.Length);

            foreach (var tenant in orderedTenants)
            {
                firstWave.Add(
                    await SubmitProbeAsync(
                            context,
                            scaleOutRequestStore,
                            tenant,
                            waveNumber: 1,
                            expectScaleOutRequest: true)
                        .ConfigureAwait(false));
            }

            await AssertProbeWaveCompletedAsync(
                    context,
                    firstWave)
                .ConfigureAwait(false);

            Assert.Equal(
                orderedTenants.Length,
                productionPreludeScaleOutSharedRunIds.Count);

            var poolId =
                ResolvePoolId(
                    context.ControlPlaneId);

            var warmHostIds =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var expectedWarmRuntimeCount =
                RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount *
                RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod;

            var warmSnapshots =
                await WaitForReadyPoolSnapshotsAsync(
                        context.Registry,
                        poolId,
                        expectedWarmRuntimeCount,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var warmRuntimeInstanceIds =
                warmSnapshots
                    .Select(snapshot => snapshot.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            var firstWaveHostsByMode =
                firstWave.ToDictionary(
                    dispatch => dispatch.TenantContext.Tenant.RuntimeMode,
                    dispatch => dispatch.Snapshot.HostId!,
                    EqualityComparer<ProductionTenantRuntimeMode>.Default);

            inventoryRuntimeInstanceIdsByTenantId.Clear();

            foreach (var dispatch in firstWave)
            {
                Assert.True(
                    inventoryRuntimeInstanceIdsByTenantId.TryAdd(
                        dispatch.TenantContext.Tenant.TenantId,
                        dispatch.Snapshot.RuntimeInstanceId),
                    $"The production warm-capacity proof recorded more than one first-wave runtime for TenantId='{dispatch.TenantContext.Tenant.TenantId}'.");
            }

            Assert.Equal(
                orderedTenants.Length,
                inventoryRuntimeInstanceIdsByTenantId.Count);

            var crashInventoryWarmRuntimeOverlapCount =
                inventoryRuntimeInstanceIdsByTenantId.Count -
                inventoryRuntimeInstanceIdsByTenantId.Values
                    .Distinct(StringComparer.Ordinal)
                    .Count();

            Assert.Equal(
                0,
                crashInventoryWarmRuntimeOverlapCount);

            Assert.Equal(
                RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                firstWaveHostsByMode.Values
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            foreach (var hostId in firstWaveHostsByMode.Values)
            {
                Assert.Equal(
                    RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                    warmSnapshots.Count(
                        snapshot =>
                            StringComparer.Ordinal.Equals(
                                snapshot.HostId,
                                hostId)));
            }

            var sharedHostId =
                firstWaveHostsByMode[ProductionTenantRuntimeMode.Shared];

            foreach (var sharedSnapshot in warmSnapshots.Where(
                         snapshot =>
                             StringComparer.Ordinal.Equals(
                                 snapshot.HostId,
                                 sharedHostId)))
            {
                sharedRuntimeInstanceIds.TryAdd(
                    sharedSnapshot.RuntimeInstanceId,
                    0);
            }

            Assert.Equal(
                RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                sharedRuntimeInstanceIds.Count);

            var secondWave =
                new List<ProductionTrafficDispatchProof>(
                    orderedTenants.Length);

            foreach (var tenant in orderedTenants)
            {
                var dispatch =
                    await SubmitProbeAsync(
                            context,
                            scaleOutRequestStore,
                            tenant,
                            waveNumber: 2,
                            expectScaleOutRequest: false)
                        .ConfigureAwait(false);

                Assert.Contains(
                    dispatch.Run.AssignedRuntimeInstanceId!,
                    warmRuntimeInstanceIds);

                var expectedHostIds =
                    tenant.Tenant.RuntimeMode switch
                    {
                        ProductionTenantRuntimeMode.Dedicated =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Dedicated]
                            },
                        ProductionTenantRuntimeMode.Hybrid =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Hybrid],
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Shared]
                            },
                        ProductionTenantRuntimeMode.Shared =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Shared]
                            },
                        _ =>
                            throw new InvalidOperationException(
                                $"Unsupported production tenant runtime mode '{tenant.Tenant.RuntimeMode}'.")
                    };

                Assert.Contains(
                    dispatch.Snapshot.HostId!,
                    expectedHostIds);

                secondWave.Add(dispatch);
            }

            await AssertProbeWaveCompletedAsync(
                    context,
                    secondWave)
                .ConfigureAwait(false);

            var hostsAfterReuse =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.True(
                warmHostIds.SetEquals(hostsAfterReuse),
                $"The existing-capacity wave changed the physical Pod set. Before='{string.Join(",", warmHostIds.OrderBy(value => value, StringComparer.Ordinal))}', After='{string.Join(",", hostsAfterReuse.OrderBy(value => value, StringComparer.Ordinal))}'.");

            context.Output.WriteLine(string.Empty);
            context.Output.WriteLine(
                "[GRPC KUBERNETES RUNTIME POOL EXISTING CAPACITY PRODUCTION SUMMARY]");
            context.Output.WriteLine("TrafficWaveCount='2'");
            context.Output.WriteLine($"TenantCount='{orderedTenants.Length}'");
            context.Output.WriteLine("DedicatedTenantCount='1'");
            context.Output.WriteLine("HybridTenantCount='1'");
            context.Output.WriteLine("SharedTenantCount='1'");
            context.Output.WriteLine($"WarmPodCount='{warmHostIds.Count}'");
            context.Output.WriteLine($"WarmRuntimeCount='{warmSnapshots.Count}'");
            context.Output.WriteLine($"ExistingRuntimeDispatchCount='{secondWave.Count}'");
            context.Output.WriteLine($"CrashInventoryWarmRuntimeCount='{inventoryRuntimeInstanceIdsByTenantId.Count}'");
            context.Output.WriteLine($"CrashInventoryWarmRuntimeOverlapCount='{crashInventoryWarmRuntimeOverlapCount}'");
            context.Output.WriteLine($"PreludeScaleOutSharedRunCount='{productionPreludeScaleOutSharedRunIds.Count}'");
            context.Output.WriteLine("ScaleOutRequestCountDuringReuse='0'");
            context.Output.WriteLine("NewRuntimeDispatchCount='0'");
            context.Output.WriteLine("NewPodCountDuringReuse='0'");
            context.Output.WriteLine("AdmissionViolationCount='0'");
            context.Output.WriteLine(
                "[GRPC KUBERNETES RUNTIME POOL EXISTING CAPACITY PRODUCTION SUMMARY END]");
        }

        private async Task<ProductionTrafficDispatchProof> SubmitProbeAsync(
            ProcessHostProductionTrafficPreludeContext context,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            ProcessHostProductionTrafficTenantContext tenantContext,
            int waveNumber,
            bool expectScaleOutRequest)
        {
            var probeTenant =
                tenantContext.Tenant with
                {
                    Run = tenantContext.Tenant.Run with
                    {
                        RunCount = 1,
                        StepCount = ProbeStepCount,
                        DelayMs = ProbeDelayMs,
                        FlakyStepInterval = 0,
                        EnableRetention = true
                    }
                };

            var pipelineName =
                $"{tenantContext.PipelinePrefix}-production-wave-{waveNumber:D2}-{Guid.NewGuid():N}";

            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        tenantContext.Mcp,
                        probeTenant,
                        context.ControlPlaneId,
                        pipelineName,
                        context.RequestedBy,
                        context.Source)
                    .ConfigureAwait(false);

            var dispatchedRun =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        tenantContext.Mcp,
                        pipelineName,
                        sharedRunId,
                        context.ScaleOutTimeout +
                        context.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    dispatchedRun.AssignedRuntimeInstanceId),
                $"Production probe was accepted without a runtime binding. TenantId='{probeTenant.TenantId}', RuntimeMode='{probeTenant.RuntimeMode}', SharedRunId='{sharedRunId}'.");

            var snapshot =
                await GetRequiredRuntimeSnapshotAsync(
                        context.Registry,
                        dispatchedRun.AssignedRuntimeInstanceId!)
                    .ConfigureAwait(false);

            AiRuntimeScaleOutRequestRecord? scaleOutRequest = null;

            if (expectScaleOutRequest)
            {
                scaleOutRequest =
                    await WaitForScaleOutRequestAsync(
                            scaleOutRequestStore,
                            context.ControlPlaneId,
                            probeTenant,
                            pipelineName,
                            sharedRunId,
                            context.ScaleOutTimeout)
                        .ConfigureAwait(false);

                AssertTypedAdmissionRequest(
                    scaleOutRequest,
                    probeTenant);

                Assert.True(
                    productionPreludeScaleOutSharedRunIds.TryAdd(
                        sharedRunId,
                        0),
                    $"The production prelude recorded the same scale-out shared run more than once. SharedRunId='{sharedRunId}', ScaleOutRequestId='{scaleOutRequest.RequestId}'.");

                Assert.False(
                    string.IsNullOrWhiteSpace(
                        scaleOutRequest.FulfilledRuntimeInstanceId));

                var fulfilledSnapshot =
                    await GetRequiredRuntimeSnapshotAsync(
                            context.Registry,
                            scaleOutRequest.FulfilledRuntimeInstanceId!)
                        .ConfigureAwait(false);

                Assert.Equal(
                    fulfilledSnapshot.HostId,
                    snapshot.HostId);
            }
            else
            {
                await AssertNoScaleOutRequestAsync(
                        scaleOutRequestStore,
                        context.ControlPlaneId,
                        probeTenant,
                        pipelineName,
                        sharedRunId,
                        TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }

            context.Output.WriteLine(
                $"[PRODUCTION TRAFFIC DISPATCH] Wave='{waveNumber}', TenantId='{probeTenant.TenantId}', RuntimeMode='{probeTenant.RuntimeMode}', SharedRunId='{sharedRunId}', RuntimeInstanceId='{snapshot.RuntimeInstanceId}', PoolId='{snapshot.PoolId}', HostId='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ScaleOutRequestId='{scaleOutRequest?.RequestId ?? string.Empty}'.");

            return new ProductionTrafficDispatchProof(
                tenantContext,
                dispatchedRun,
                snapshot);
        }

        private static async Task<AiRuntimeScaleOutRequestRecord>
            WaitForScaleOutRequestAsync(
                IAiRuntimeScaleOutRequestStore store,
                string controlPlaneId,
                ProductionTenantScenarioDefinition tenant,
                string pipelineName,
                string sharedRunId,
                TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> requests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                requests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                SharedRunId = sharedRunId,
                                MaxResults = 10
                            })
                        .ConfigureAwait(false);

                var fulfilled =
                    requests
                        .Where(
                            request =>
                                request.Status ==
                                    AiRuntimeScaleOutRequestStatus.Fulfilled)
                        .OrderByDescending(request => request.FulfilledAtUtc)
                        .FirstOrDefault();

                if (fulfilled is not null)
                {
                    return fulfilled;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The production warm-up run did not expose a fulfilled typed scale-out request. ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}', PipelineName='{pipelineName}', SharedRunId='{sharedRunId}', ObservedRequestCount='{requests.Count}'.");
        }

        private static async Task AssertNoScaleOutRequestAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string sharedRunId,
            TimeSpan observationWindow)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(observationWindow);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var requests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                SharedRunId = sharedRunId,
                                MaxResults = 10
                            })
                        .ConfigureAwait(false);

                Assert.True(
                    requests.Count == 0,
                    $"Existing capacity reuse created an unexpected scale-out request. ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}', PipelineName='{pipelineName}', SharedRunId='{sharedRunId}', Requests='{string.Join(",", requests.Select(request => $"{request.RequestId}:{request.Status}"))}'.");

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }
        }

        private static void AssertTypedAdmissionRequest(
            AiRuntimeScaleOutRequestRecord request,
            ProductionTenantScenarioDefinition tenant)
        {
            Assert.Equal(
                tenant.TenantId,
                request.TenantId);

            Assert.Equal(
                tenant.TenantGroupId,
                request.TenantGroupId);

            Assert.Equal(
                tenant.TenantId,
                request.ExecutionContextSnapshot.TenantId);

            Assert.Equal(
                tenant.TenantGroupId,
                request.ExecutionContextSnapshot.TenantGroupId);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolveIsolationMode(
                    tenant.RuntimeMode),
                request.IsolationMode);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(
                    tenant.RuntimeMode),
                request.PreferDedicatedCapacity);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(
                    tenant.RuntimeMode),
                request.AllowSharedFallback);

            Assert.True(
                request.MaxRuntimeInstances.HasValue,
                "The typed scale-out request did not preserve MaxRuntimeInstances.");

            Assert.Equal(
                tenant.MaxRuntimeInstances,
                request.MaxRuntimeInstances.Value);

            Assert.Equal(
                tenant.RuntimeInstanceIdPrefix,
                request.RuntimeInstanceIdPrefix);
        }

        private static async Task AssertProbeWaveCompletedAsync(
            ProcessHostProductionTrafficPreludeContext context,
            IReadOnlyCollection<ProductionTrafficDispatchProof> dispatches)
        {
            foreach (var dispatch in dispatches)
            {
                var statuses =
                    await McpTestWaitHelpers
                        .WaitForTerminalRuntimeRunStatusesAsync(
                            dispatch.TenantContext.Mcp,
                            new[]
                            {
                                dispatch.Run
                            },
                            context.CompletionTimeout)
                        .ConfigureAwait(false);

                var status =
                    Assert.Single(statuses);

                Assert.True(
                    status.Success,
                    status.FailureReason ??
                    status.Message);

                Assert.True(
                    string.Equals(
                        status.RunState?.Status,
                        "completed",
                        StringComparison.OrdinalIgnoreCase),
                    $"Production probe did not complete successfully. SharedRunId='{dispatch.Run.SharedRunId}', RuntimeInstanceId='{dispatch.Run.AssignedRuntimeInstanceId}', LocalRunId='{dispatch.Run.LocalRunId}', Status='{status.RunState?.Status}', FailureReason='{status.RunState?.FailureReason ?? status.FailureReason}'.");
            }
        }

        private static async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
            WaitForReadyPoolSnapshotsAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                int expectedRuntimeCount,
                int expectedHostCount,
                TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeInstanceSnapshot> readySnapshots =
                Array.Empty<AiRuntimeInstanceSnapshot>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await registry
                        .ListAsync(includeStopped: false)
                        .ConfigureAwait(false);

                readySnapshots = snapshots
                    .Where(
                        snapshot =>
                            StringComparer.Ordinal.Equals(
                                snapshot.PoolId,
                                poolId) &&
                            snapshot.Status ==
                                AiRuntimeInstanceStatus.Ready &&
                            !string.IsNullOrWhiteSpace(snapshot.HostId))
                    .OrderBy(snapshot => snapshot.HostId, StringComparer.Ordinal)
                    .ThenBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray();

                var hostCount =
                    readySnapshots
                        .Select(snapshot => snapshot.HostId!)
                        .Distinct(StringComparer.Ordinal)
                        .Count();

                if (readySnapshots.Count == expectedRuntimeCount &&
                    hostCount == expectedHostCount)
                {
                    return readySnapshots;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The mixed-admission warm pool did not expose the exact ready topology. PoolId='{poolId}', ExpectedRuntimeCount='{expectedRuntimeCount}', ActualRuntimeCount='{readySnapshots.Count}', ExpectedHostCount='{expectedHostCount}', ActualHostCount='{readySnapshots.Select(snapshot => snapshot.HostId).Where(hostId => !string.IsNullOrWhiteSpace(hostId)).Distinct(StringComparer.Ordinal).Count()}'.");
        }

        private static int GetRuntimeModeOrder(
            ProductionTenantRuntimeMode runtimeMode)
        {
            return runtimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => 0,
                ProductionTenantRuntimeMode.Hybrid => 1,
                ProductionTenantRuntimeMode.Shared => 2,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(runtimeMode),
                    runtimeMode,
                    "Unsupported production tenant runtime mode.")
            };
        }

        private sealed record ProductionTrafficDispatchProof(
            ProcessHostProductionTrafficTenantContext TenantContext,
            Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store.AiSharedRunRecord Run,
            AiRuntimeInstanceSnapshot Snapshot);
    }

    /// <summary>
    /// Executes five isolated real-Pod failure scenarios concurrently and cleans each scenario's Pods on completion.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolPodFailureP5")]
    public sealed class GrpcKubernetesRuntimePoolPodFailureP5ScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        private const int Parallelism = 5;

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessScaleOutTimeoutOverride =>
            TimeSpan.FromMinutes(4);

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessProgressTimeoutOverride =>
            TimeSpan.FromMinutes(5);

        /// <summary>
        /// Initializes the gRPC Kubernetes Runtime Pool Pod-failure P5 proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolPodFailureP5ScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolPodFailureP5ScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies five independent real Pod deletions, complete durable recovery, safe-tenant continuity,
        /// and immediate per-scenario Pod cleanup.
        /// </summary>
        /// <returns>A task that completes when all five isolated scenarios have converged and cleaned their Pods.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_P5_Should_Fully_Recover_After_Five_Independent_Pod_Deletions()
        {
            try
            {
                await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                        Parallelism)
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        protected override ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryScenario(
            bool includeSafeTenant = false)
        {
            var scenario =
                base.CreateRealRuntimeCrashRecoveryScenario(
                    includeSafeTenant: true);

            var impactedTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-a"));

            var safeTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-safe"));

            return scenario with
            {
                Name =
                    "grpc-kubernetes-runtime-pool-pod-failure-p5",
                ControlPlaneIdPrefix =
                    "grpc-kubernetes-runtime-pool-pod-failure-p5",
                Tenants = includeSafeTenant
                    ? new[]
                    {
                        impactedTenant,
                        safeTenant
                    }
                    : new[]
                    {
                        impactedTenant
                    }
            };
        }
    }
}
