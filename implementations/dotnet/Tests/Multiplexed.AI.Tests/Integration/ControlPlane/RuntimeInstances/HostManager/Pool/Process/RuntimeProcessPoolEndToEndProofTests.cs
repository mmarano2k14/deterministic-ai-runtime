using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Proves exact process-host Runtime Pool failure isolation, capacity suppression, and
    /// replacement with three real RuntimeInstanceOnly child processes.
    /// </summary>
    [Collection(RuntimeProcessPoolEndToEndCollection.Name)]
    [Trait("Category", "RuntimeProcessPoolEndToEnd")]
    public sealed class RuntimeProcessPoolEndToEndProofTests :
        IClassFixture<RuntimeProcessPoolEndToEndTestFixture>
    {
        private readonly RuntimeProcessPoolEndToEndTestFixture fixture;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RuntimeProcessPoolEndToEndProofTests"/> class.
        /// </summary>
        /// <param name="fixture">
        /// The centralized strongly typed process-pool test fixture.
        /// </param>
        public RuntimeProcessPoolEndToEndProofTests(
            RuntimeProcessPoolEndToEndTestFixture fixture)
        {
            this.fixture =
                fixture
                ?? throw new ArgumentNullException(
                    nameof(fixture));
        }

        /// <summary>
        /// Starts three real runtimes, kills A1, proves exact A1 failure and suppression,
        /// preserves A2/A3, observes safe A4 replacement, and cleans up the entire pool.
        /// </summary>
        [Fact]
        public async Task ProcessPool_Should_Replace_Killed_Runtime_Without_Touching_Siblings()
        {
            var identity =
                this.fixture.CreateIdentity();

            var builder =
                Host.CreateApplicationBuilder();

            this.fixture.ConfigureControlPlane(
                builder.Configuration,
                identity);

            builder.Services.AddSingleton(
                this.fixture.RedisConnection);

            this.fixture.RegisterControlPlaneIdentity(
                builder.Services,
                identity);

            builder.Services.AddAiControlPlane(
                configuration: builder.Configuration);

            builder.Services.AddSingleton<
                TrackingSystemRuntimeProcessPoolLauncher>();

            builder.Services.AddSingleton<
                IAiRuntimeProcessPoolChildProcessLauncher>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        TrackingSystemRuntimeProcessPoolLauncher>());

            builder.Services.AddAiRuntimeProcessPool(
                this.fixture.CreatePoolOptions(identity),
                this.fixture.CreateRuntimeInstanceOptions(
                    identity));

            using var host = builder.Build();

            using var timeout =
                new CancellationTokenSource(
                    this.fixture.TestTimeout);

            await host.StartAsync(timeout.Token);

            var manager =
                host.Services.GetRequiredService<
                    IAiRuntimeProcessPoolManager>();

            var membershipReader =
                host.Services.GetRequiredService<
                    IAiRuntimePoolMembershipReader>();

            var routeRegistry =
                host.Services.GetRequiredService<
                    IAiRuntimePoolRouteRegistry>();

            var failureReader =
                host.Services.GetRequiredService<
                    IAiRuntimePoolFailureReader>();

            var safetyReader =
                host.Services.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyReader>();

            var launcher =
                host.Services.GetRequiredService<
                    TrackingSystemRuntimeProcessPoolLauncher>();

            var initial =
                await WaitForHealthyPoolAsync(
                    manager,
                    membershipReader,
                    expectedRuntimeIds: null,
                    timeout.Token);

            Assert.Equal(3, initial.Children.Count);
            Assert.False(initial.IsBelowMinimumCapacity);

            var runtimeA1 =
                initial.Children.Single(
                    child => child.Ordinal == 1);

            var runtimeA2 =
                initial.Children.Single(
                    child => child.Ordinal == 2);

            var runtimeA3 =
                initial.Children.Single(
                    child => child.Ordinal == 3);

            var preservedRuntimeIds =
                new[]
                {
                    runtimeA2.RuntimeInstanceId,
                    runtimeA3.RuntimeInstanceId
                }
                .OrderBy(
                    value => value,
                    StringComparer.Ordinal)
                .ToArray();

            var initialRoutes =
                await routeRegistry
                    .ListByHostIdAsync(
                        initial.HostId,
                        timeout.Token)
                    .ConfigureAwait(false);

            Assert.Equal(
                3,
                initialRoutes.Count);

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

            launcher.KillUnexpectedly(
                runtimeA1.RuntimeInstanceId);

            var replacement =
                await WaitForReplacementAsync(
                    manager,
                    membershipReader,
                    runtimeA1.RuntimeInstanceId,
                    preservedRuntimeIds,
                    timeout.Token);

            Assert.Equal(
                preservedRuntimeIds,
                replacement.Children
                    .Where(child => child.Ordinal is 2 or 3)
                    .Select(child => child.RuntimeInstanceId)
                    .OrderBy(
                        value => value,
                        StringComparer.Ordinal)
                    .ToArray());

            var runtimeA4 =
                Assert.Single(
                    replacement.Children.Where(
                        child => child.Ordinal == 4));

            Assert.NotEqual(
                runtimeA1.RuntimeInstanceId,
                runtimeA4.RuntimeInstanceId);

            Assert.Equal(
                manager.Identity.PoolId,
                runtimeA4.PoolId);

            Assert.Equal(
                manager.Identity.HostId,
                runtimeA4.HostId);

            Assert.True(
                launcher.Children.Count >= 4);

            var replacementRoutes =
                await routeRegistry
                    .ListByHostIdAsync(
                        replacement.HostId,
                        timeout.Token)
                    .ConfigureAwait(false);

            Assert.Equal(
                3,
                replacementRoutes.Count);

            Assert.DoesNotContain(
                replacementRoutes,
                route =>
                    StringComparer.Ordinal.Equals(
                        route.RuntimeInstanceId,
                        runtimeA1.RuntimeInstanceId));

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

            Assert.NotEqual(
                routeA2.RouteId,
                routeA4.RouteId);

            Assert.NotEqual(
                routeA3.RouteId,
                routeA4.RouteId);

            var hostFailures =
                await failureReader
                    .ListByHostIdAsync(
                        replacement.HostId,
                        timeout.Token)
                    .ConfigureAwait(false);

            var failureA1 =
                Assert.Single(hostFailures);

            Assert.Equal(
                AiRuntimePoolFailureScope.RuntimeInstance,
                failureA1.Scope);

            Assert.Equal(
                AiRuntimePoolFailureKind.UnexpectedProcessExit,
                failureA1.Kind);

            Assert.Equal(
                manager.Identity.PoolId,
                failureA1.PoolId);

            Assert.Equal(
                manager.Identity.HostId,
                failureA1.HostId);

            Assert.Equal(
                runtimeA1.RuntimeInstanceId,
                failureA1.RuntimeInstanceId);

            Assert.Equal(
                routeA1.RouteId,
                failureA1.RouteId);

            Assert.Single(
                await failureReader
                    .ListByRuntimeInstanceIdAsync(
                        runtimeA1.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Empty(
                await failureReader
                    .ListByRuntimeInstanceIdAsync(
                        runtimeA2.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Empty(
                await failureReader
                    .ListByRuntimeInstanceIdAsync(
                        runtimeA3.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Empty(
                await failureReader
                    .ListByRuntimeInstanceIdAsync(
                        runtimeA4.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            var hostSuppressions =
                await safetyReader
                    .ListByHostIdAsync(
                        replacement.HostId,
                        timeout.Token)
                    .ConfigureAwait(false);

            var suppressionA1 =
                Assert.Single(hostSuppressions);

            Assert.Equal(
                failureA1.FailureId,
                suppressionA1.FailureId);

            Assert.Equal(
                failureA1.PoolId,
                suppressionA1.PoolId);

            Assert.Equal(
                failureA1.HostId,
                suppressionA1.HostId);

            Assert.Equal(
                failureA1.RuntimeInstanceId,
                suppressionA1.RuntimeInstanceId);

            Assert.Equal(
                failureA1.RouteId,
                suppressionA1.RouteId);

            Assert.NotNull(
                await safetyReader
                    .GetSuppressionAsync(
                        manager.Identity.PoolId,
                        manager.Identity.HostId,
                        runtimeA1.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Null(
                await safetyReader
                    .GetSuppressionAsync(
                        manager.Identity.PoolId,
                        manager.Identity.HostId,
                        runtimeA2.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Null(
                await safetyReader
                    .GetSuppressionAsync(
                        manager.Identity.PoolId,
                        manager.Identity.HostId,
                        runtimeA3.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            Assert.Null(
                await safetyReader
                    .GetSuppressionAsync(
                        manager.Identity.PoolId,
                        manager.Identity.HostId,
                        runtimeA4.RuntimeInstanceId,
                        timeout.Token)
                    .ConfigureAwait(false));

            await host.StopAsync(timeout.Token);

            var stopped =
                await manager.GetSnapshotAsync(
                    timeout.Token);

            Assert.Equal(
                AiRuntimeProcessPoolManagerStatus.Stopped,
                stopped.Status);

            Assert.Empty(stopped.Children);
        }

        /// <summary>
        /// Waits until the pool and all active registry members are ready.
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
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);

                var runtimeIds =
                    expectedRuntimeIds ??
                    snapshot.Children
                        .Select(child => child.RuntimeInstanceId)
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
        /// Waits until one fresh runtime replaces the failed runtime while both siblings remain
        /// unchanged and ready.
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
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);

                var activeRuntimeIds =
                    snapshot.Children
                        .Select(child => child.RuntimeInstanceId)
                        .ToArray();

                var replacementObserved =
                    snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    !activeRuntimeIds.Contains(
                        failedRuntimeInstanceId,
                        StringComparer.Ordinal) &&
                    preservedRuntimeIds.All(
                        runtimeId =>
                            activeRuntimeIds.Contains(
                                runtimeId,
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
        /// Verifies that every expected active runtime remains a first-class ready member of the
        /// exact host incarnation.
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
