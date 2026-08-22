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
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Stores;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

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
            record.Events.Select(x => x.EventType).Should().Contain(AiEngineEvents.Recovery.SharedRunRequeuedForResume);
            record.Events.Select(x => x.EventType).Should().Contain(AiEngineEvents.Recovery.FailedLocalRunMarkedRequeuedForRecovery);
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
                recorder,
                new RecoveryExecutionControlService(),
                new RecoveryDagExecutionStore());
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


        private sealed class RecoveryExecutionControlService : IAiExecutionControlService
        {
            private AiExecutionControlState? state;

            /// <summary>
            /// Gets the number of recovery pause requests.
            /// </summary>
            public int PauseForRecoveryCallCount { get; private set; }

            /// <summary>
            /// Gets the number of effective paused transitions.
            /// </summary>
            public int MarkPausedCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOwnerId);
                cancellationToken.ThrowIfCancellationRequested();

                this.PauseForRecoveryCallCount++;
                this.state = CreateState(
                    executionId,
                    AiExecutionControlStatus.Pausing,
                    reason,
                    recoveryOwnerId);

                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkPausedAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                this.MarkPausedCallCount++;
                this.state = CreateState(
                    executionId,
                    AiExecutionControlStatus.Paused,
                    this.state?.Reason,
                    requestedBy ?? this.state?.RequestedBy);

                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> CancelExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkCancelledAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkWaitingForInputAsync(
                string executionId,
                string waitingKey,
                string? waitingStepName = null,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> SubmitHumanInputAsync(
                string executionId,
                string waitingKey,
                IReadOnlyDictionary<string, object?> input,
                string? submittedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkRunningAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            private static AiExecutionControlState CreateState(
                string executionId,
                AiExecutionControlStatus status,
                string? reason,
                string? requestedBy)
            {
                return new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = status,
                    Reason = reason,
                    RequestedBy = requestedBy,
                    UpdatedAtUtc = DateTime.UtcNow,
                    PauseRequestedAtUtc =
                        status == AiExecutionControlStatus.Pausing
                            ? DateTime.UtcNow
                            : null,
                    PausedAtUtc =
                        status == AiExecutionControlStatus.Paused
                            ? DateTime.UtcNow
                            : null
                };
            }
        }

        /// <summary>
        /// Provides only the explicit running-step recovery operation required by this test.
        /// </summary>
        private sealed class RecoveryDagExecutionStore : IAiDagExecutionStore
        {
            /// <summary>
            /// Gets the number of explicit recovery calls.
            /// </summary>
            public int RecoverRunningStepsForRecoveryCallCount { get; private set; }

            /// <summary>
            /// Gets the last durable execution identifier recovered.
            /// </summary>
            public string? LastRecoveredExecutionId { get; private set; }

            /// <inheritdoc />
            public Task<int> RecoverRunningStepsForRecoveryAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                this.RecoverRunningStepsForRecoveryCallCount++;
                this.LastRecoveredExecutionId = executionId;

                return Task.FromResult(1);
            }

            /// <inheritdoc />
            public Task CreateAsync(
                AiExecutionRecord record,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionRecord?> GetRecordAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task SaveRecordAsync(
                AiExecutionRecord record,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task SaveStateAsync(
                string executionId,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task DeleteRecordAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task DeleteStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task DeleteStepsAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task DeleteExecutionBundleAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiClaimedStep?> TryClaimNextReadyStepAsync(
                string executionId,
                string workerId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<bool> TryCompleteStepAsync(
                string executionId,
                string stepName,
                string claimToken,
                AiStepResult result,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<bool> TryParkStepAsync(
                string executionId,
                string stepName,
                string claimToken,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<bool> TryResumeExternalWaitingStepAsync(
                string executionId,
                string stepName,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }

            public Task<bool> TryFailStepAsync(
                string executionId,
                string stepName,
                string claimToken,
                string? error,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<int> RecoverTimedOutStepsAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<bool> TryFinalizeExecutionAsync(
                AiDagExecutionFinalizationRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task RestoreAsync(
                AiExecutionRecord record,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task DeleteStepAsync(
                string executionId,
                string stepName,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiClaimedStep>> TryClaimReadyStepsAsync(
                string executionId,
                string workerId,
                int maxSteps,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiClaimedStep>> GetReadyStepsAsync(
                string executionId,
                int maxSteps,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiClaimedStep?> TryClaimStepAsync(
                string executionId,
                string stepName,
                string workerId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRetentionPatchResult> TryApplyRetentionPatchAsync(
                string executionId,
                IReadOnlyCollection<AiRetentionPatchCandidate> candidates,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

    }
}