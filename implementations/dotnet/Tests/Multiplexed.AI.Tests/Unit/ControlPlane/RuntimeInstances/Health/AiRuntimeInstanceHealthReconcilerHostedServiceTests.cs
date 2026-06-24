using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceHealthReconcilerHostedService"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconcilerHostedServiceTests
    {
        /// <summary>
        /// Verifies that the hosted service does nothing when disabled.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Not_Reconcile_When_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = new AiRuntimeInstanceHealthReconciler(
                registry,
                Options.Create(new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                }));

            var service = new AiRuntimeInstanceHealthReconcilerHostedService(
                reconciler,
                Options.Create(new AiRuntimeInstanceHealthReconcilerHostedServiceOptions
                {
                    Enabled = false,
                    Interval = TimeSpan.FromMilliseconds(10),
                    ErrorDelay = TimeSpan.FromMilliseconds(10)
                }));

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            var snapshot = await registry.GetAsync("runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that the hosted service runs reconciliation when enabled.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reconcile_When_Enabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = new AiRuntimeInstanceHealthReconciler(
                registry,
                Options.Create(new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                }));

            var service = new AiRuntimeInstanceHealthReconcilerHostedService(
                reconciler,
                Options.Create(new AiRuntimeInstanceHealthReconcilerHostedServiceOptions
                {
                    Enabled = true,
                    Interval = TimeSpan.FromMilliseconds(10),
                    ErrorDelay = TimeSpan.FromMilliseconds(10)
                }));

            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await service.StartAsync(cancellationTokenSource.Token);

            while (!cancellationTokenSource.IsCancellationRequested)
            {
                var snapshot = await registry.GetAsync("runtime-1");

                if (snapshot?.Status == AiRuntimeInstanceStatus.Unhealthy)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationTokenSource.Token);
            }

            await service.StopAsync(CancellationToken.None);

            var finalSnapshot = await registry.GetAsync("runtime-1");

            Assert.NotNull(finalSnapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, finalSnapshot!.Status);
            Assert.False(finalSnapshot.CanAcceptRun);
        }

        /// <summary>
        /// Creates a runtime instance registration for tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                Role = AiRuntimeInstanceRole.Runtime,
                WorkerCount = 2,
                QueueCapacity = 10,
                MaxConcurrentRuns = 2,
                RuntimeVersion = "test",
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            };
        }
    }
}