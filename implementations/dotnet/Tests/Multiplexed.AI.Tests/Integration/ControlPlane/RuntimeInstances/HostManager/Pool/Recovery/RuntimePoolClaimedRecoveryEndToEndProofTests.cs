using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery
{
    /// <summary>
    /// Proves the complete exact claimed-recovery orchestration after one real process-host runtime
    /// failure.
    /// </summary>
    [Collection(RuntimeProcessPoolEndToEndCollection.Name)]
    [Trait("Category", "RuntimePoolClaimedRecoveryEndToEnd")]
    public sealed class RuntimePoolClaimedRecoveryEndToEndProofTests :
        IClassFixture<RuntimeProcessPoolEndToEndTestFixture>,
        IClassFixture<RuntimePoolClaimedRecoveryEndToEndTestFixture>
    {
        private readonly RuntimeProcessPoolEndToEndTestFixture
            processFixture;

        private readonly RuntimePoolClaimedRecoveryEndToEndTestFixture
            recoveryFixture;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RuntimePoolClaimedRecoveryEndToEndProofTests"/> class.
        /// </summary>
        public RuntimePoolClaimedRecoveryEndToEndProofTests(
            RuntimeProcessPoolEndToEndTestFixture processFixture,
            RuntimePoolClaimedRecoveryEndToEndTestFixture recoveryFixture)
        {
            this.processFixture =
                processFixture
                ?? throw new ArgumentNullException(
                    nameof(processFixture));

            this.recoveryFixture =
                recoveryFixture
                ?? throw new ArgumentNullException(
                    nameof(recoveryFixture));
        }

        /// <summary>
        /// Kills real A1 and proves exact failure, suppression, inventory, claim, transition
        /// ordering, sibling isolation, replacement safety, and explicit claim release.
        /// </summary>
        [Fact]
        public async Task Real_A1_Failure_Should_Execute_One_Exact_Claimed_Recovery()
        {
            var identity =
                this.processFixture.CreateIdentity();

            var builder =
                Host.CreateApplicationBuilder();

            this.processFixture.ConfigureControlPlane(
                builder.Configuration,
                identity);

            builder.Services.AddSingleton(
                this.processFixture.RedisConnection);

            this.processFixture.RegisterControlPlaneIdentity(
                builder.Services,
                identity);

            builder.Services.AddAiControlPlane(
                configuration: builder.Configuration);

            this.recoveryFixture.RegisterServices(
                builder.Services);

            builder.Services.AddSingleton<
                TrackingSystemRuntimeProcessPoolLauncher>();

            builder.Services.AddSingleton<
                IAiRuntimeProcessPoolChildProcessLauncher>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        TrackingSystemRuntimeProcessPoolLauncher>());

            builder.Services.AddAiRuntimeProcessPool(
                this.processFixture.CreatePoolOptions(identity),
                this.processFixture.CreateRuntimeInstanceOptions(
                    identity));

            using var host =
                builder.Build();

            using var timeout =
                new CancellationTokenSource(
                    this.processFixture.TestTimeout);

            await host.StartAsync(
                timeout.Token);

            try
            {
                var manager =
                    host.Services.GetRequiredService<
                        IAiRuntimeProcessPoolManager>();

                var membershipReader =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolMembershipReader>();

                var routes =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolRouteRegistry>();

                var failures =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolFailureReader>();

                var safety =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolCapacitySafetyReader>();

                var claimCoordinator =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolRecoveryClaimCoordinator>();

                var claimStore =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolRecoveryClaimStore>();

                var executor =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolClaimedRecoveryExecutor>();

                var state =
                    host.Services.GetRequiredService<
                        RuntimePoolClaimedRecoveryEndToEndState>();

                var launcher =
                    host.Services.GetRequiredService<
                        TrackingSystemRuntimeProcessPoolLauncher>();

                var initial =
                    await WaitForHealthyPoolAsync(
                        manager,
                        membershipReader,
                        expectedRuntimeIds: null,
                        timeout.Token);

                var runtimeA1 =
                    initial.Children.Single(
                        child => child.Ordinal == 1);

                var runtimeA2 =
                    initial.Children.Single(
                        child => child.Ordinal == 2);

                var runtimeA3 =
                    initial.Children.Single(
                        child => child.Ordinal == 3);

                var initialRoutes =
                    await routes
                        .ListByHostIdAsync(
                            initial.HostId,
                            timeout.Token)
                        .ConfigureAwait(false);

                var routeA1 =
                    initialRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA1.RuntimeInstanceId));

                var routeA2 =
                    initialRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA2.RuntimeInstanceId));

                var routeA3 =
                    initialRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA3.RuntimeInstanceId));

                this.recoveryFixture.SeedAssignedWork(
                    state,
                    runtimeA1.RuntimeInstanceId,
                    runtimeA2.RuntimeInstanceId,
                    runtimeA3.RuntimeInstanceId);

                launcher.KillUnexpectedly(
                    runtimeA1.RuntimeInstanceId);

                var replacement =
                    await WaitForReplacementAsync(
                        manager,
                        membershipReader,
                        runtimeA1.RuntimeInstanceId,
                        new[]
                        {
                            runtimeA2.RuntimeInstanceId,
                            runtimeA3.RuntimeInstanceId
                        },
                        timeout.Token);

                var runtimeA4 =
                    replacement.Children.Single(
                        child => child.Ordinal == 4);

                var failureA1 =
                    await WaitForFailureAuthorityAsync(
                        failures,
                        safety,
                        replacement.HostId,
                        runtimeA1.RuntimeInstanceId,
                        timeout.Token);

                Assert.Equal(
                    routeA1.RouteId,
                    failureA1.RouteId);

                var replacementRoutes =
                    await routes
                        .ListByHostIdAsync(
                            replacement.HostId,
                            timeout.Token)
                        .ConfigureAwait(false);

                Assert.Equal(
                    routeA2.RouteId,
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA2.RuntimeInstanceId))
                        .RouteId);

                Assert.Equal(
                    routeA3.RouteId,
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA3.RuntimeInstanceId))
                        .RouteId);

                var routeA4 =
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA4.RuntimeInstanceId));

                Assert.NotEqual(
                    routeA1.RouteId,
                    routeA4.RouteId);

                Assert.Null(
                    await safety.GetSuppressionAsync(
                        replacement.PoolId,
                        replacement.HostId,
                        runtimeA2.RuntimeInstanceId,
                        timeout.Token));

                Assert.Null(
                    await safety.GetSuppressionAsync(
                        replacement.PoolId,
                        replacement.HostId,
                        runtimeA3.RuntimeInstanceId,
                        timeout.Token));

                Assert.Null(
                    await safety.GetSuppressionAsync(
                        replacement.PoolId,
                        replacement.HostId,
                        runtimeA4.RuntimeInstanceId,
                        timeout.Token));

                var claimResults =
                    await Task.WhenAll(
                        claimCoordinator.TryAcquireAsync(
                            failureA1.FailureId,
                            "runtime-pool-final-primary",
                            timeout.Token),
                        claimCoordinator.TryAcquireAsync(
                            failureA1.FailureId,
                            "runtime-pool-final-secondary",
                            timeout.Token));

                var acquired =
                    Assert.Single(
                        claimResults.Where(
                            result =>
                                result.Status ==
                                AiRuntimePoolRecoveryClaimAcquisitionStatus
                                    .Acquired));

                var denied =
                    Assert.Single(
                        claimResults.Where(
                            result =>
                                result.Status ==
                                AiRuntimePoolRecoveryClaimAcquisitionStatus
                                    .AlreadyClaimed));

                Assert.NotNull(acquired.Lease);
                Assert.Null(denied.Lease);

                Assert.Equal(
                    acquired.Claim.ClaimId,
                    denied.Claim.ClaimId);

                Assert.Equal(
                    new[]
                    {
                        AiRuntimePoolAssignedWorkKind.InFlight,
                        AiRuntimePoolAssignedWorkKind.LocalQueued,
                        AiRuntimePoolAssignedWorkKind.LocalQueued
                    },
                    acquired.Inventory.Candidates
                        .Select(candidate => candidate.Kind)
                        .ToArray());

                Assert.Equal(
                    new[]
                    {
                        "local-a1-flight",
                        "local-a1-queued-01",
                        "local-a1-queued-02"
                    },
                    acquired.Inventory.Candidates
                        .Select(candidate => candidate.LocalRunId)
                        .ToArray());

                Assert.All(
                    acquired.Inventory.Candidates,
                    candidate =>
                    {
                        Assert.Equal(
                            runtimeA1.RuntimeInstanceId,
                            candidate.RuntimeInstanceId);

                        Assert.Equal(
                            "tenant-a1",
                            candidate.TenantId);
                    });

                Assert.DoesNotContain(
                    acquired.Inventory.Candidates,
                    candidate =>
                        candidate.LocalRunId.Contains(
                            "control",
                            StringComparison.Ordinal));

                var execution =
                    await executor.ExecuteAsync(
                        acquired,
                        timeout.Token);

                Assert.Equal(3, execution.CandidateCount);
                Assert.Equal(3, execution.AcceptedCount);
                Assert.Equal(3, execution.ChangedCount);
                Assert.Equal(0, execution.RejectedCount);

                Assert.Equal(
                    new[]
                    {
                        runtimeA1.RuntimeInstanceId,
                        runtimeA1.RuntimeInstanceId,
                        runtimeA1.RuntimeInstanceId
                    },
                    state.TransitionRequests
                        .Select(
                            request =>
                                request.Ownership.RuntimeInstanceId)
                        .ToArray());

                Assert.Equal(
                    new[]
                    {
                        "local-a1-flight",
                        "local-a1-queued-01",
                        "local-a1-queued-02"
                    },
                    state.TransitionRequests
                        .Select(
                            request =>
                                request.Ownership.LocalRunId)
                        .ToArray());

                Assert.Equal(
                    "execution-a1",
                    state.TransitionRequests[0]
                        .Ownership.ExecutionId);

                Assert.Null(
                    state.TransitionRequests[1]
                        .Ownership.ExecutionId);

                Assert.Null(
                    state.TransitionRequests[2]
                        .Ownership.ExecutionId);

                Assert.Equal(
                    new[]
                    {
                        "runtime-pool-claimed-in-flight-recovery",
                        "runtime-pool-claimed-local-queued-recovery",
                        "runtime-pool-claimed-local-queued-recovery"
                    },
                    state.TransitionRequests
                        .Select(request => request.Reason)
                        .ToArray());

                Assert.DoesNotContain(
                    state.OwnershipRequests,
                    request =>
                        StringComparer.Ordinal.Equals(
                            request.RuntimeInstanceId,
                            runtimeA2.RuntimeInstanceId) ||
                        StringComparer.Ordinal.Equals(
                            request.RuntimeInstanceId,
                            runtimeA3.RuntimeInstanceId) ||
                        StringComparer.Ordinal.Equals(
                            request.RuntimeInstanceId,
                            runtimeA4.RuntimeInstanceId));

                Assert.False(
                    acquired.Lease!.IsReleased);

                Assert.True(
                    await claimStore.IsActiveLeaseAsync(
                        acquired.Claim.FailureId,
                        acquired.Claim.ClaimId,
                        acquired.Lease.LeaseId,
                        timeout.Token));

                await acquired.Lease.DisposeAsync();

                Assert.False(
                    await claimStore.IsActiveLeaseAsync(
                        acquired.Claim.FailureId,
                        acquired.Claim.ClaimId,
                        acquired.Lease.LeaseId,
                        timeout.Token));
            }
            finally
            {
                using var cleanup =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(30));

                await host.StopAsync(
                    cleanup.Token);
            }
        }

        /// <summary>
        /// Waits until the pool and every expected active member are ready.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForHealthyPoolAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolMembershipReader membershipReader,
                IReadOnlyCollection<string>? expectedRuntimeIds,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot =
                    await manager
                        .GetSnapshotAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                var runtimeIds =
                    expectedRuntimeIds ??
                    snapshot.Children
                        .Select(
                            child =>
                                child.RuntimeInstanceId)
                        .ToArray();

                if (snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    !snapshot.IsBelowMinimumCapacity &&
                    await AreMembersReadyAsync(
                            membershipReader,
                            snapshot.HostId,
                            runtimeIds,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return snapshot;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until A4 replaces failed A1 while A2 and A3 remain ready.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForReplacementAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolMembershipReader membershipReader,
                string failedRuntimeInstanceId,
                IReadOnlyCollection<string> preservedRuntimeIds,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot =
                    await manager
                        .GetSnapshotAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                var activeRuntimeIds =
                    snapshot.Children
                        .Select(
                            child =>
                                child.RuntimeInstanceId)
                        .ToArray();

                var replacementObserved =
                    snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    !activeRuntimeIds.Contains(
                        failedRuntimeInstanceId,
                        StringComparer.Ordinal) &&
                    preservedRuntimeIds.All(
                        runtimeInstanceId =>
                            activeRuntimeIds.Contains(
                                runtimeInstanceId,
                                StringComparer.Ordinal));

                if (replacementObserved &&
                    await AreMembersReadyAsync(
                            membershipReader,
                            snapshot.HostId,
                            activeRuntimeIds,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return snapshot;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for the exact A1 failure and suppression authority.
        /// </summary>
        private static async Task<AiRuntimePoolFailureObservation>
            WaitForFailureAuthorityAsync(
                IAiRuntimePoolFailureReader failureReader,
                IAiRuntimePoolCapacitySafetyReader safetyReader,
                string hostId,
                string failedRuntimeInstanceId,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var failures =
                    await failureReader
                        .ListByRuntimeInstanceIdAsync(
                            failedRuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var failure =
                    failures.SingleOrDefault();

                if (failure is not null)
                {
                    var suppression =
                        await safetyReader
                            .GetSuppressionAsync(
                                failure.PoolId,
                                failure.HostId,
                                failure.RuntimeInstanceId,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (suppression is not null &&
                        StringComparer.Ordinal.Equals(
                            failure.HostId,
                            hostId) &&
                        StringComparer.Ordinal.Equals(
                            suppression.FailureId,
                            failure.FailureId) &&
                        StringComparer.Ordinal.Equals(
                            suppression.RouteId,
                            failure.RouteId))
                    {
                        return failure;
                    }
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verifies every expected runtime remains a ready first-class member.
        /// </summary>
        private static async Task<bool> AreMembersReadyAsync(
            IAiRuntimePoolMembershipReader membershipReader,
            string hostId,
            IReadOnlyCollection<string> expectedRuntimeIds,
            CancellationToken cancellationToken)
        {
            var members =
                await membershipReader
                    .ListByHostIdAsync(
                        hostId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var activeMembers =
                members
                    .Where(
                        member =>
                            expectedRuntimeIds.Contains(
                                member.RuntimeInstanceId,
                                StringComparer.Ordinal))
                    .ToArray();

            return activeMembers.Length ==
                    expectedRuntimeIds.Count &&
                activeMembers.All(
                    member =>
                        StringComparer.Ordinal.Equals(
                            member.HostId,
                            hostId) &&
                        !string.IsNullOrWhiteSpace(
                            member.PoolId) &&
                        member.CanAcceptRun &&
                        member.Status is
                            AiRuntimeInstanceStatus.Ready or
                            AiRuntimeInstanceStatus.Busy);
        }
    }
}
