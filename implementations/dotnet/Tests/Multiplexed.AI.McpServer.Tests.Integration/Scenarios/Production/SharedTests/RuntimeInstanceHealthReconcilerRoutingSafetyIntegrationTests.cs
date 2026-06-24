using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Production-style routing safety tests for runtime instance health reconciliation.
    /// </summary>
    public sealed class RuntimeInstanceHealthReconcilerRoutingSafetyIntegrationTests
    {
        /// <summary>
        /// Verifies that the health reconciler marks a stale ready runtime instance as unhealthy
        /// and prevents it from accepting new routing decisions.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Stale_Ready_Runtime_Unhealthy_And_Stop_New_Routing()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true,
                    IgnoreStoppedRuntimeInstances = true,
                    IgnorePausedRuntimeInstances = true,
                    IgnoreDrainingRuntimeInstances = true,
                    DryRun = false
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");
            var routableSnapshots = await registry.ListAsync();

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);

            Assert.Equal("runtime-tenant-a-1", result.Decisions[0].RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale", result.Decisions[0].Reason);
            Assert.True(result.Decisions[0].Changed);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
            Assert.Equal(5, snapshot.AvailableRunSlots);

            var listed = Assert.Single(routableSnapshots);
            Assert.Equal("runtime-tenant-a-1", listed.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, listed.Status);
            Assert.False(listed.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that the health reconciler marks a stale busy runtime instance as unhealthy
        /// without losing runtime ownership, capacity, or tenant metadata.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Stale_Busy_Runtime_Unhealthy_And_Preserve_Runtime_Metadata()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-tenant-a-1",
                queuedRunCount: 2,
                runningRunCount: 1,
                activeRunCount: 3,
                availableRunSlots: 1,
                activeWorkerCount: 4,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 2,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);

            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.True(result.Decisions[0].Changed);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
            Assert.Equal(2, snapshot.QueuedRunCount);
            Assert.Equal(1, snapshot.RunningRunCount);
            Assert.Equal(3, snapshot.ActiveRunCount);
            Assert.Equal(1, snapshot.AvailableRunSlots);
            Assert.Equal(4, snapshot.ActiveWorkerCount);
            Assert.Equal(1, snapshot.AvailableWorkerCount);
            Assert.Equal(2, snapshot.MaxLocalWorkersPerExecution);
            Assert.Equal("production-routing-safety", snapshot.Metadata["scenario"]);
        }

        /// <summary>
        /// Verifies that the health reconciler does not mark fresh runtime instances as unhealthy.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mark_Fresh_Runtime_Unhealthy()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.FromMinutes(5),
                    MarkStaleRuntimeUnhealthy = true
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");

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
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
        }

        /// <summary>
        /// Verifies that the health reconciler ignores runtime instances that are already non-routable.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Already_NonRoutable_Runtime_Statuses()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-paused", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-draining", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-unhealthy", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-stopped", "tenant-a", "tenant-group-a"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-paused",
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 5,
                activeWorkerCount: 0,
                availableWorkerCount: 5,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: true,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Paused);

            await registry.MarkDrainingAsync("runtime-draining");
            await registry.MarkUnhealthyAsync("runtime-unhealthy");
            await registry.UnregisterAsync("runtime-stopped");

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IgnorePausedRuntimeInstances = true,
                    IgnoreDrainingRuntimeInstances = true,
                    IgnoreStoppedRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(4, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(4, result.IgnoredCount);
            Assert.Equal(4, result.Decisions.Count);
            Assert.All(result.Decisions, decision =>
            {
                Assert.Equal("ignored-runtime-status", decision.Reason);
                Assert.False(decision.Changed);
            });

            var paused = await registry.GetAsync("runtime-paused");
            var draining = await registry.GetAsync("runtime-draining");
            var unhealthy = await registry.GetAsync("runtime-unhealthy");
            var stopped = await registry.GetAsync("runtime-stopped");

            Assert.Equal(AiRuntimeInstanceStatus.Paused, paused!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, draining!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, unhealthy!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, stopped!.Status);

            Assert.False(paused.CanAcceptRun);
            Assert.False(draining.CanAcceptRun);
            Assert.False(unhealthy.CanAcceptRun);
            Assert.False(stopped.CanAcceptRun);
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
        /// Creates a runtime instance registration.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string tenantId,
            string tenantGroupId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                Role = AiRuntimeInstanceRole.Runtime,
                HostName = "production-test-host",
                ProcessId = 12345,
                WorkerCount = 5,
                QueueCapacity = 20,
                MaxConcurrentRuns = 5,
                RuntimeVersion = "production-test",
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "production-routing-safety"
                }
            };
        }
    }
}