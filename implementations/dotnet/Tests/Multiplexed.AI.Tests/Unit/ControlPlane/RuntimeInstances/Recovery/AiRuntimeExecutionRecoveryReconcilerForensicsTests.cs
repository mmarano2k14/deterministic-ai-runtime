using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Tests runtime execution recovery reconciler forensics recording.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryReconcilerForensicsTests
    {
        /// <summary>
        /// Verifies that the reconciler records a recovery candidate detected event before applying the transition.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Record_Forensics_When_Recovery_Candidate_Is_Detected()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var ownershipResolver = new FakeSharedRunOwnershipResolver();
            var transitionService = new FakeRuntimeExecutionRecoveryTransitionService();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            registry.RuntimeInstances.Add(CreateRuntimeInstance("runtime-1", AiRuntimeInstanceStatus.Unhealthy));
            executionIndex.UnfinishedRuns.Add(CreateIndexEntry("local-run-1", "execution-1", "runtime-1"));

            ownershipResolver.Result = new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                CanRecover = true,
                Reason = "dispatched-to-unavailable-runtime"
            };

            transitionService.Result = new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = true,
                Changed = true,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                Action = "requeue-shared-run",
                Reason = "runtime-execution-recovery-requeue"
            };

            var reconciler = CreateReconciler(
                registry,
                executionIndex,
                ownershipResolver,
                transitionService,
                recorder);

            var result = await reconciler.ReconcileAsync();

            result.ScannedRuntimeInstanceCount.Should().Be(1);
            result.IgnoredRuntimeInstanceCount.Should().Be(0);
            result.DiscoveredUnfinishedRunCount.Should().Be(1);
            result.RecoveredRunCount.Should().Be(1);
            result.Decisions.Should().ContainSingle();

            ownershipResolver.ResolveCalls.Should().Be(1);
            transitionService.ApplyCalls.Should().Be(1);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().ContainSingle();

            var record = records.Single();

            record.Identity.ForensicsId.Should().Be("runtime-recovery:execution-1:shared-run-1:local-run-1");
            record.Identity.ExecutionId.Should().Be("execution-1");
            record.Identity.SharedRunId.Should().Be("shared-run-1");

            record.Events.Should().ContainSingle();

            var evt = record.Events.Single();

            evt.EventType.Should().Be(AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCandidateDetected);
            evt.Outcome.Should().Be("recoverable");
            evt.Reason.Should().Be("dispatched-to-unavailable-runtime");
            evt.ExecutionId.Should().Be("execution-1");
            evt.SharedRunId.Should().Be("shared-run-1");
            evt.LocalRunId.Should().Be("local-run-1");
            evt.RuntimeInstanceId.Should().Be("runtime-1");
            evt.Metadata["tenant.id"].Should().Be("tenant-a");
            evt.Metadata["tenant.group.id"].Should().Be("tenant-group-a");
            evt.Metadata["candidate.canRecover"].Should().Be(bool.TrueString);
        }

        /// <summary>
        /// Verifies that the reconciler records a not-recoverable candidate event when ownership rejects recovery.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Record_NotRecoverable_Forensics_When_Candidate_Cannot_Recover()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var ownershipResolver = new FakeSharedRunOwnershipResolver();
            var transitionService = new FakeRuntimeExecutionRecoveryTransitionService();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            registry.RuntimeInstances.Add(CreateRuntimeInstance("runtime-1", AiRuntimeInstanceStatus.Unhealthy));
            executionIndex.UnfinishedRuns.Add(CreateIndexEntry("local-run-1", "execution-1", "runtime-1"));

            ownershipResolver.Result = new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                CanRecover = false,
                Reason = "already-terminal"
            };

            transitionService.Result = new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = false,
                Changed = false,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                Action = "none",
                Reason = "ownership-not-recoverable"
            };

            var reconciler = CreateReconciler(
                registry,
                executionIndex,
                ownershipResolver,
                transitionService,
                recorder);

            var result = await reconciler.ReconcileAsync();

            result.DiscoveredUnfinishedRunCount.Should().Be(1);
            result.RecoveredRunCount.Should().Be(0);
            transitionService.ApplyCalls.Should().Be(1);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().ContainSingle();

            var evt = records.Single().Events.Single();

            evt.EventType.Should().Be(AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCandidateDetected);
            evt.Outcome.Should().Be("not-recoverable");
            evt.Reason.Should().Be("already-terminal");
            evt.Metadata["candidate.canRecover"].Should().Be(bool.FalseString);
        }

        /// <summary>
        /// Verifies that the reconciler does not record forensics when ownership does not resolve a shared run.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Record_Forensics_When_SharedRunId_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var ownershipResolver = new FakeSharedRunOwnershipResolver();
            var transitionService = new FakeRuntimeExecutionRecoveryTransitionService();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            registry.RuntimeInstances.Add(CreateRuntimeInstance("runtime-1", AiRuntimeInstanceStatus.Unhealthy));
            executionIndex.UnfinishedRuns.Add(CreateIndexEntry("local-run-1", "execution-1", "runtime-1"));

            ownershipResolver.Result = new AiSharedRunOwnershipResolutionResult
            {
                Resolved = false,
                SharedRunId = null,
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                CanRecover = false,
                Reason = "shared-run-not-found"
            };

            transitionService.Result = new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = false,
                Changed = false,
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                Action = "none",
                Reason = "ownership-not-resolved"
            };

            var reconciler = CreateReconciler(
                registry,
                executionIndex,
                ownershipResolver,
                transitionService,
                recorder);

            var result = await reconciler.ReconcileAsync();

            result.DiscoveredUnfinishedRunCount.Should().Be(1);
            result.RecoveredRunCount.Should().Be(0);
            transitionService.ApplyCalls.Should().Be(1);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the reconciler ignores healthy runtime instances and records no recovery forensics.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Record_Forensics_When_Runtime_Is_Healthy()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var ownershipResolver = new FakeSharedRunOwnershipResolver();
            var transitionService = new FakeRuntimeExecutionRecoveryTransitionService();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            registry.RuntimeInstances.Add(CreateRuntimeInstance("runtime-1", AiRuntimeInstanceStatus.Ready));
            executionIndex.UnfinishedRuns.Add(CreateIndexEntry("local-run-1", "execution-1", "runtime-1"));

            var reconciler = CreateReconciler(
                registry,
                executionIndex,
                ownershipResolver,
                transitionService,
                recorder);

            var result = await reconciler.ReconcileAsync();

            result.ScannedRuntimeInstanceCount.Should().Be(0);
            result.IgnoredRuntimeInstanceCount.Should().Be(1);
            result.DiscoveredUnfinishedRunCount.Should().Be(0);
            result.RecoveredRunCount.Should().Be(0);

            ownershipResolver.ResolveCalls.Should().Be(0);
            transitionService.ApplyCalls.Should().Be(0);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().BeEmpty();
        }

        /// <summary>
        /// Creates a recovery reconciler.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="executionIndex">The runtime run execution index.</param>
        /// <param name="ownershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The transition service.</param>
        /// <param name="recorder">The runtime recovery forensics recorder.</param>
        /// <returns>The recovery reconciler.</returns>
        private static AiRuntimeExecutionRecoveryReconciler CreateReconciler(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRunExecutionIndex executionIndex,
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IAiRuntimeRecoveryForensicsRecorder recorder)
        {
            return new AiRuntimeExecutionRecoveryReconciler(
                registry,
                executionIndex,
                ownershipResolver,
                transitionService,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true,
                    IncludeStoppedRuntimeInstances = true,
                    IncludeDrainingRuntimeInstances = true,
                    RequeueUnfinishedRuns = true,
                    DryRun = false
                }),
                recorder);
        }

        /// <summary>
        /// Creates a best-effort forensics recorder backed by the provided store.
        /// </summary>
        /// <param name="store">The forensics store.</param>
        /// <returns>The forensics recorder.</returns>
        private static IAiRuntimeRecoveryForensicsRecorder CreateRecorder(
            IAiRuntimeRecoveryForensicsStore store)
        {
            return new BestEffortAiRuntimeRecoveryForensicsRecorder(
                store,
                Options.Create(new AiRuntimeRecoveryForensicsOptions
                {
                    Enabled = true,
                    StrictPersistence = false
                }),
                NullLogger<BestEffortAiRuntimeRecoveryForensicsRecorder>.Instance);
        }

        /// <summary>
        /// Creates a runtime instance snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="status">The runtime instance status.</param>
        /// <returns>The runtime instance snapshot.</returns>
        private static AiRuntimeInstanceSnapshot CreateRuntimeInstance(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                Status = status,
                HostName = "host-test",
                ProcessId = null,
                KubernetesNamespace = null,
                KubernetesPodName = null,
                KubernetesNodeName = null,
                WorkerCount = 1,
                QueuedRunCount = 0,
                RunningRunCount = 1,
                ActiveRunCount = 1,
                QueueCapacity = 10,
                MaxConcurrentRuns = 1,
                AvailableRunSlots = 0,
                IsQueuePaused = false,
                CanAcceptRun = false,
                RegisteredAtUtc = now.AddMinutes(-5),
                LastHeartbeatAtUtc = now.AddMinutes(-5),
                SnapshotAtUtc = now,
                RuntimeVersion = "test",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Role = AiRuntimeInstanceRole.Runtime,
                ActiveWorkerCount = 1,
                AvailableWorkerCount = 0,
                MaxLocalWorkersPerExecution = 1,
                HostId = "host-test",
                RuntimeId = runtimeInstanceId,
                ControlPlaneHostId = "control-plane-host-test",
                ControlPlaneId = "control-plane-test"
            };
        }

        /// <summary>
        /// Creates a runtime run execution index entry.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The runtime run execution index entry.</returns>
        private static AiRuntimeRunExecutionIndexEntry CreateIndexEntry(
            string runId,
            string executionId,
            string runtimeInstanceId)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runId,
                ExecutionId = executionId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = "started",
                ExecutionContextSnapshot = CreateSnapshot(),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
                CompletedAtUtc = null,
                FailureReason = null,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-test",
                Project = "project-test",
                UserId = "user-test",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}