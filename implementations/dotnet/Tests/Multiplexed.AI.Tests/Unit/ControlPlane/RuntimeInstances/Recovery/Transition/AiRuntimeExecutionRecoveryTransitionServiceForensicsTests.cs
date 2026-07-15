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
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Tests runtime execution recovery transition forensics recording.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionServiceForensicsTests
    {
        /// <summary>
        /// Verifies that a successful recovery transition records runtime recovery forensics.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Record_Forensics_When_Recovery_Transition_Succeeds()
        {
            var sharedQueue = new FakeSharedQueue();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            var service = CreateService(sharedQueue, executionIndex, recorder);

            var ownership = CreateOwnership();

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = ownership,
                Reason = "runtime-unhealthy",
                DryRun = false
            });

            result.Accepted.Should().BeTrue();
            result.Changed.Should().BeTrue();
            result.Action.Should().Be("requeue-shared-run");
            result.SharedRunId.Should().Be("shared-run-1");
            result.LocalRunId.Should().Be("local-run-1");
            result.ExecutionId.Should().Be("execution-1");

            sharedQueue.RequeueDispatchedCalls.Should().Be(1);
            sharedQueue.LastRequeueSharedRunId.Should().Be("shared-run-1");
            sharedQueue.LastRequeueClaimToken.Should().Be("claim-token-1");
            sharedQueue.LastRequeueReason.Should().Be("runtime-unhealthy");
            sharedQueue.LastRequeueMetadata.Should().NotBeNull();
            sharedQueue.LastRequeueMetadata!["recovery.mode"].Should().Be("resume-existing-execution");
            sharedQueue.LastRequeueMetadata["recovery.failedExecutionId"].Should().Be("execution-1");
            sharedQueue.LastRequeueMetadata["recovery.failedRuntimeInstanceId"].Should().Be("runtime-1");
            sharedQueue.LastRequeueMetadata["recovery.failedLocalRunId"].Should().Be("local-run-1");

            executionIndex.MarkRequeuedForRecoveryCalls.Should().Be(1);
            executionIndex.LastRequeuedRunId.Should().Be("local-run-1");
            executionIndex.LastRequeuedExecutionId.Should().Be("execution-1");
            executionIndex.LastRequeuedReason.Should().Be("runtime-unhealthy");

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().ContainSingle();

            var record = records.Single();

            record.Identity.ForensicsId.Should().Be("runtime-recovery:execution-1:shared-run-1:local-run-1");
            record.Identity.ExecutionId.Should().Be("execution-1");
            record.Identity.SharedRunId.Should().Be("shared-run-1");

            record.Failure.Should().NotBeNull();
            record.Failure!.RuntimeFailureIncidentId.Should().Be("runtime-failure:runtime-1");
            record.Failure.FailedRuntimeInstanceId.Should().Be("runtime-1");
            record.Failure.FailedLocalRunId.Should().Be("local-run-1");
            record.Failure.FailureSignal.Should().Be("runtime-execution-recovery");
            record.Failure.SuppressCapacityReason.Should().Be("runtime-unhealthy");

            record.Recovery.Should().NotBeNull();
            record.Recovery!.RecoveryMode.Should().Be("resume-existing-execution");
            record.Recovery.RecoveryKind.Should().Be("in-flight-execution-resume");
            record.Recovery.Outcome.Should().Be("requeued");
            record.Recovery.Reason.Should().Be("runtime-unhealthy");

            record.Artifacts.Restored.Should().Contain(AiRuntimeRecoveryArtifactName.DurableExecutionId);
            record.Artifacts.Restored.Should().Contain(AiRuntimeRecoveryArtifactName.SharedRunMetadata);
            record.Artifacts.Restored.Should().Contain(AiRuntimeRecoveryArtifactName.RecoveryMetadata);
            record.Artifacts.Recreated.Should().Contain(AiRuntimeRecoveryArtifactName.DispatchAssignment);
            record.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.FailedRuntimeLocalQueueMemory);
            record.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.OldClaimToken);
            record.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.OldLease);
            record.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.OldLocalRunAsActiveWork);

            record.Events.Should().HaveCount(2);
            record.Events.Select(x => x.EventType).Should().Contain(AiRuntimeRecoveryForensicsEventType.SharedRunRequeuedForResume);
            record.Events.Select(x => x.EventType).Should().Contain(AiRuntimeRecoveryForensicsEventType.FailedLocalRunMarkedRequeuedForRecovery);
        }

        /// <summary>
        /// Verifies that dry-run recovery transitions do not record recovery forensics.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Not_Record_Forensics_When_Request_Is_DryRun()
        {
            var sharedQueue = new FakeSharedQueue();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            var service = CreateService(sharedQueue, executionIndex, recorder);

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(),
                Reason = "runtime-unhealthy",
                DryRun = true
            });

            result.Accepted.Should().BeTrue();
            result.Changed.Should().BeFalse();
            result.Action.Should().Be("dry-run-requeue-shared-run");

            sharedQueue.RequeueDispatchedCalls.Should().Be(0);
            executionIndex.MarkRequeuedForRecoveryCalls.Should().Be(0);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that rejected shared queue requeue transitions do not record recovery forensics.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Not_Record_Forensics_When_SharedQueue_Requeue_Is_Rejected()
        {
            var sharedQueue = new FakeSharedQueue
            {
                RejectRequeueDispatched = true
            };

            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            var service = CreateService(sharedQueue, executionIndex, recorder);

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(),
                Reason = "runtime-unhealthy",
                DryRun = false
            });

            result.Accepted.Should().BeFalse();
            result.Changed.Should().BeFalse();
            result.Action.Should().Be("none");
            result.Reason.Should().Be("shared-queue-requeue-dispatched-rejected");

            sharedQueue.RequeueDispatchedCalls.Should().Be(1);
            executionIndex.MarkRequeuedForRecoveryCalls.Should().Be(0);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that invalid ownership does not record recovery forensics.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Not_Record_Forensics_When_Ownership_Is_Not_Recoverable()
        {
            var sharedQueue = new FakeSharedQueue();
            var executionIndex = new FakeRuntimeRunExecutionIndex();
            var forensicsStore = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(forensicsStore);

            var service = CreateService(sharedQueue, executionIndex, recorder);

            var ownership = CreateOwnership(
                canRecover: false,
                reason: "already-terminal");

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = ownership,
                Reason = "runtime-unhealthy",
                DryRun = false
            });

            result.Accepted.Should().BeFalse();
            result.Changed.Should().BeFalse();
            result.Action.Should().Be("none");
            result.Reason.Should().Be("ownership-not-recoverable");

            sharedQueue.RequeueDispatchedCalls.Should().Be(0);
            executionIndex.MarkRequeuedForRecoveryCalls.Should().Be(0);

            var records = await forensicsStore.ListByExecutionIdAsync("execution-1");

            records.Should().BeEmpty();
        }

        /// <summary>
        /// Creates a runtime execution recovery transition service.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="executionIndex">The runtime run execution index.</param>
        /// <param name="recorder">The runtime recovery forensics recorder.</param>
        /// <returns>The runtime execution recovery transition service.</returns>
        private static AiRuntimeExecutionRecoveryTransitionService CreateService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex executionIndex,
            IAiRuntimeRecoveryForensicsRecorder recorder)
        {
            return new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                executionIndex,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    EnableDagExecutionResume = true
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
        /// Creates a shared run ownership result.
        /// </summary>
        /// <param name="resolved">A value indicating whether ownership was resolved.</param>
        /// <param name="canRecover">A value indicating whether the ownership can be recovered.</param>
        /// <param name="reason">The ownership resolution reason.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateOwnership(
            bool resolved = true,
            bool canRecover = true,
            string reason = "dispatched-to-unavailable-runtime")
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = resolved,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                QueueStatus = AiSharedQueueItemStatus.Dispatched,
                ClaimToken = "claim-token-1",
                CanRecover = canRecover,
                Reason = reason
            };
        }

        
    }
}