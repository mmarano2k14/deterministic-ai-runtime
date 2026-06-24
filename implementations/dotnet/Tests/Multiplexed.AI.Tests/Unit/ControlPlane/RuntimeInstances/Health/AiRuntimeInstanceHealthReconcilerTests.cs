using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceHealthReconciler"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconcilerTests
    {
        /// <summary>
        /// Verifies that a stale ready runtime instance is marked unhealthy.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Ready_Runtime_Unhealthy_When_Heartbeat_Is_Stale()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal("runtime-1", result.Decisions[0].RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale", result.Decisions[0].Reason);
            Assert.True(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that a stale busy runtime instance is marked unhealthy.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Busy_Runtime_Unhealthy_When_Heartbeat_Is_Stale()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-1",
                queuedRunCount: 1,
                runningRunCount: 1,
                activeRunCount: 2,
                availableRunSlots: 0,
                activeWorkerCount: 2,
                availableWorkerCount: 0,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale", result.Decisions[0].Reason);
            Assert.True(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that a fresh ready runtime instance is not marked unhealthy.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mark_Ready_Runtime_When_Heartbeat_Is_Fresh()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.FromMinutes(5)
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-fresh", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that stopped runtime instances are ignored.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Stopped_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.UnregisterAsync("runtime-1");

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, result.Decisions[0].NewStatus);
            Assert.Equal("ignored-runtime-status", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that paused runtime instances are ignored when configured.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Paused_Runtime_When_Configured()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-1",
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 1,
                activeWorkerCount: 0,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: true,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Paused);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IgnorePausedRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Paused, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Paused, result.Decisions[0].NewStatus);
            Assert.Equal("ignored-runtime-status", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Paused, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that draining runtime instances are ignored when configured.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Draining_Runtime_When_Configured()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkDrainingAsync("runtime-1");

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IgnoreDrainingRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, result.Decisions[0].NewStatus);
            Assert.Equal("ignored-runtime-status", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that already unhealthy runtime instances are ignored.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Unhealthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkUnhealthyAsync("runtime-1");

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("ignored-runtime-status", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that dry-run mode reports the unhealthy transition without changing registry state.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mark_Runtime_When_DryRun_Is_Enabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    DryRun = true
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale-dry-run", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that disabling stale unhealthy transitions reports the decision without changing registry state.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mark_Runtime_When_MarkStaleRuntimeUnhealthy_Is_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = false
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale-dry-transition-disabled", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that disabled reconciliation returns an empty result.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Return_Empty_Result_When_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = false,
                    StaleHeartbeatThreshold = TimeSpan.Zero
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(0, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Empty(result.Decisions);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that disabling ready runtime reconciliation skips ready runtime instances.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Evaluate_Ready_Runtime_When_Ready_Inclusion_Is_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IncludeReadyRuntimeInstances = false
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal("runtime-status-not-included", result.Decisions[0].Reason);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].NewStatus);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that disabling busy runtime reconciliation skips busy runtime instances.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Evaluate_Busy_Runtime_When_Busy_Inclusion_Is_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-1",
                queuedRunCount: 1,
                runningRunCount: 1,
                activeRunCount: 2,
                availableRunSlots: 0,
                activeWorkerCount: 2,
                availableWorkerCount: 0,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IncludeBusyRuntimeInstances = false
                });

            var result = await reconciler.ReconcileAsync();
            var snapshot = await registry.GetAsync("runtime-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(1, result.IgnoredCount);
            Assert.Single(result.Decisions);
            Assert.Equal("runtime-status-not-included", result.Decisions[0].Reason);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Decisions[0].NewStatus);
            Assert.False(result.Decisions[0].Changed);
            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, snapshot!.Status);
        }

        /// <summary>
        /// Creates a runtime instance health reconciler.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="options">The reconciliation options.</param>
        /// <returns>The runtime instance health reconciler.</returns>
        private static AiRuntimeInstanceHealthReconciler CreateReconciler(
            IAiRuntimeInstanceRegistry registry,
            AiRuntimeInstanceHealthReconciliationOptions options)
        {
            return new AiRuntimeInstanceHealthReconciler(
                registry,
                Options.Create(options));
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