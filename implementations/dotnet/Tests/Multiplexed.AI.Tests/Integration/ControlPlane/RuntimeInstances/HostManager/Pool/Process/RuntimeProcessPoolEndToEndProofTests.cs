using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Proves the complete Step 2 process-host Runtime Pool Manager contract with three real
    /// RuntimeInstanceOnly child processes.
    /// </summary>
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
        /// Starts three real runtimes, kills A1, preserves A2/A3, observes A4, and cleans up the
        /// entire pool.
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

            var preservedRuntimeIds =
                initial.Children
                    .Where(child => child.Ordinal is 2 or 3)
                    .Select(child => child.RuntimeInstanceId)
                    .OrderBy(
                        value => value,
                        StringComparer.Ordinal)
                    .ToArray();

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
