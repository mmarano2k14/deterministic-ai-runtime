using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeExecutionRecoveryReconciler"/>.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryReconcilerTests
    {
        /// <summary>
        /// Verifies that recovery reconciliation returns an empty result when disabled.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Return_Empty_Result_When_Disabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkUnhealthyAsync("runtime-1");

            await index.RegisterQueuedAsync(CreateIndexEntry("runtime-1", "run-1", "execution-1"));

            var reconciler = CreateReconciler(
                registry,
                index,
                new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = false
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(0, result.ScannedRuntimeInstanceCount);
            Assert.Equal(0, result.IgnoredRuntimeInstanceCount);
            Assert.Equal(0, result.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, result.RecoveredRunCount);
            Assert.Empty(result.Decisions);
        }

        /// <summary>
        /// Verifies that recovery reconciliation discovers unfinished runs assigned to unhealthy runtime instances.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Report_Unfinished_Runs_For_Unhealthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkUnhealthyAsync("runtime-1");

            await index.RegisterQueuedAsync(CreateIndexEntry("runtime-1", "run-1", "execution-1"));
            await index.MarkStartedAsync("run-1", "execution-1");

            var reconciler = CreateReconciler(
                registry,
                index,
                new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true,
                    DryRun = true
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(1, result.ScannedRuntimeInstanceCount);
            Assert.Equal(0, result.IgnoredRuntimeInstanceCount);
            Assert.Equal(1, result.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, result.RecoveredRunCount);

            Assert.Equal(2, result.Decisions.Count);

            var ownershipDecision = Assert.Single(
                result.Decisions,
                decision => string.Equals(
                    decision.Action,
                    "ownership-resolution",
                    StringComparison.Ordinal));

            Assert.Equal("runtime-1", ownershipDecision.RuntimeInstanceId);
            Assert.Equal("run-1", ownershipDecision.LocalRunId);
            Assert.Equal("execution-1", ownershipDecision.ExecutionId);
            Assert.Null(ownershipDecision.SharedRunId);
            Assert.Equal("tenant-1", ownershipDecision.TenantId);
            Assert.Equal("tenant-group-1", ownershipDecision.TenantGroupId);
            Assert.StartsWith(
                "resolved=False;canRecover=False;reason=shared-run-ownership-not-found",
                ownershipDecision.Reason,
                StringComparison.Ordinal);
            Assert.False(ownershipDecision.Changed);

            var transitionDecision = Assert.Single(
                result.Decisions,
                decision => string.Equals(
                    decision.Action,
                    "none",
                    StringComparison.Ordinal));

            Assert.Equal("runtime-1", transitionDecision.RuntimeInstanceId);
            Assert.Equal("run-1", transitionDecision.LocalRunId);
            Assert.Equal("execution-1", transitionDecision.ExecutionId);
            Assert.Null(transitionDecision.SharedRunId);
            Assert.Equal("tenant-1", transitionDecision.TenantId);
            Assert.Equal("tenant-group-1", transitionDecision.TenantGroupId);
            Assert.StartsWith(
                "transitionReason=ownership-not-resolved",
                transitionDecision.Reason,
                StringComparison.Ordinal);
            Assert.False(transitionDecision.Changed);
        }

        /// <summary>
        /// Verifies that recovery reconciliation ignores healthy runtime instances.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Healthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await index.RegisterQueuedAsync(CreateIndexEntry("runtime-1", "run-1", "execution-1"));

            var reconciler = CreateReconciler(
                registry,
                index,
                new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(0, result.ScannedRuntimeInstanceCount);
            Assert.Equal(1, result.IgnoredRuntimeInstanceCount);
            Assert.Equal(0, result.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, result.RecoveredRunCount);

            var decision = Assert.Single(result.Decisions);

            Assert.Equal("runtime-1", decision.RuntimeInstanceId);
            Assert.Equal("ignore-runtime-instance", decision.Action);
            Assert.Equal("runtime-status-not-included", decision.Reason);
            Assert.False(decision.Changed);
        }

        /// <summary>
        /// Verifies that recovery reconciliation reports no unfinished runs for an unhealthy runtime with no assigned unfinished entries.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Report_No_Unfinished_Runs_For_Unhealthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkUnhealthyAsync("runtime-1");

            var reconciler = CreateReconciler(
                registry,
                index,
                new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(1, result.ScannedRuntimeInstanceCount);
            Assert.Equal(0, result.IgnoredRuntimeInstanceCount);
            Assert.Equal(0, result.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, result.RecoveredRunCount);

            var decision = Assert.Single(result.Decisions);

            Assert.Equal("runtime-1", decision.RuntimeInstanceId);
            Assert.Equal("none", decision.Action);
            Assert.Equal("no-recoverable-runtime-runs", decision.Reason);
            Assert.False(decision.Changed);
        }

        /// <summary>
        /// Verifies that recovery reconciliation does not mutate index entries during dry-run discovery.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mutate_Index_Entries_When_DryRun()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await registry.RegisterAsync(CreateRegistration("runtime-1"));
            await registry.MarkUnhealthyAsync("runtime-1");

            await index.RegisterQueuedAsync(CreateIndexEntry("runtime-1", "run-1", "execution-1"));
            await index.MarkStartedAsync("run-1", "execution-1");

            var reconciler = CreateReconciler(
                registry,
                index,
                new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    DryRun = true
                });

            var result = await reconciler.ReconcileAsync();
            var entry = await index.GetAsync("run-1");

            Assert.Equal(1, result.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, result.RecoveredRunCount);

            Assert.NotNull(entry);
            Assert.Equal("run-1", entry!.RunId);
            Assert.Equal("execution-1", entry.ExecutionId);
            Assert.Equal("runtime-1", entry.RuntimeInstanceId);
            Assert.Equal("running", entry.Status);
            Assert.Null(entry.CompletedAtUtc);
        }

        /// <summary>
        /// Creates a runtime execution recovery reconciler.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="index">The runtime run execution index.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        /// <returns>The runtime execution recovery reconciler.</returns>
        private static AiRuntimeExecutionRecoveryReconciler CreateReconciler(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRunExecutionIndex index,
            AiRuntimeExecutionRecoveryReconciliationOptions options)
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();

            IAiSharedRunOwnershipResolver ownershipResolver =
                new AiSharedRunOwnershipResolver(
                    sharedQueue,
                    sharedRunStore);

            IAiRuntimeExecutionRecoveryTransitionService transitionService =
                new AiRuntimeExecutionRecoveryTransitionService(
                    sharedQueue,
                    index);

            return new AiRuntimeExecutionRecoveryReconciler(
                registry,
                index,
                ownershipResolver,
                transitionService,
                Options.Create(options));
        }

        /// <summary>
        /// Creates a runtime instance registration.
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

        /// <summary>
        /// Creates a runtime run execution index entry.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <returns>The runtime run execution index entry.</returns>
        private static AiRuntimeRunExecutionIndexEntry CreateIndexEntry(
            string runtimeInstanceId,
            string runId,
            string executionId)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runId,
                ExecutionId = executionId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "recovery-tests",
                UserId = "test-user",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.Now
            };
        }
    }
}