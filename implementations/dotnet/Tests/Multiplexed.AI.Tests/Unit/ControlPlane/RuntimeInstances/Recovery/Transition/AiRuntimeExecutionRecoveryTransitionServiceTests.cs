using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeExecutionRecoveryTransitionService"/>.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionServiceTests
    {
        /// <summary>
        /// Verifies that unresolved ownership is rejected before any mutation.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Ownership_Is_Not_Resolved()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: false,
                        canRecover: false),
                    DryRun = true
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("ownership-not-resolved", result.Reason);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Empty(operations);
        }

        /// <summary>
        /// Verifies that non-recoverable ownership is rejected before any mutation.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Ownership_Is_Not_Recoverable()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: false),
                    DryRun = true
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("ownership-not-recoverable", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Empty(operations);
        }

        /// <summary>
        /// Verifies that dry-run validates recovery without mutating execution control,
        /// DAG state, shared queue state, or runtime execution index state.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Accept_Recoverable_Ownership_When_DryRun_Without_Mutation()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations);
            var dagExecutionStore = new FakeDagExecutionStore(operations);
            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: true);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    Reason = "test-dry-run",
                    DryRun = true
                });

            Assert.True(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("dry-run-requeue-shared-run", result.Action);
            Assert.Equal("test-dry-run", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Empty(operations);
            Assert.Equal(0, executionControl.PauseForRecoveryCalls);
            Assert.Equal(0, executionControl.MarkPausedCalls);
            Assert.Equal(0, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);
            Assert.Equal(0, sharedQueue.RequeueDispatchedCalls);
            Assert.Equal(0, runExecutionIndex.MarkRequeuedForRecoveryCalls);
        }

        /// <summary>
        /// Verifies the complete ordered in-flight recovery transition.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Recover_InFlight_Execution_In_Required_Order()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations);
            var dagExecutionStore = new FakeDagExecutionStore(operations)
            {
                RecoveredRunningStepCount = 1
            };

            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: true);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    Reason = "test-recovery-requeue",
                    DryRun = false
                });

            const string expectedRecoveryOwnerId =
                "runtime-recovery:execution-1:shared-run-1:run-1";

            Assert.True(result.Accepted);
            Assert.True(result.Changed);
            Assert.Equal("requeue-shared-run", result.Action);
            Assert.Equal("test-recovery-requeue", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);

            Assert.Equal(
                [
                    "control.pause-recovery",
                    "dag.recover-running",
                    "control.mark-paused",
                    "queue.requeue-dispatched",
                    "index.mark-requeued-for-recovery"
                ],
                operations);

            Assert.Equal(1, executionControl.PauseForRecoveryCalls);
            Assert.Equal(1, executionControl.MarkPausedCalls);
            Assert.Equal("execution-1", executionControl.LastExecutionId);
            Assert.Equal(expectedRecoveryOwnerId, executionControl.LastRecoveryOwnerId);
            Assert.Equal(expectedRecoveryOwnerId, executionControl.LastMarkPausedRequestedBy);
            Assert.Equal("test-recovery-requeue", executionControl.LastReason);

            Assert.Equal(1, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);
            Assert.Equal("execution-1", dagExecutionStore.LastRecoveredExecutionId);

            Assert.Equal(1, sharedQueue.RequeueDispatchedCalls);
            Assert.Equal("shared-run-1", sharedQueue.LastRequeueSharedRunId);
            Assert.Equal("claim-token-1", sharedQueue.LastRequeueClaimToken);
            Assert.Equal("test-recovery-requeue", sharedQueue.LastRequeueReason);
            Assert.NotNull(sharedQueue.LastRequeueMetadata);
            Assert.Equal(
                expectedRecoveryOwnerId,
                sharedQueue.LastRequeueMetadata!["recovery.forensicsId"]);
            Assert.Equal(
                "resume-existing-execution",
                sharedQueue.LastRequeueMetadata["recovery.mode"]);
            Assert.Equal(
                "execution-1",
                sharedQueue.LastRequeueMetadata["recovery.failedExecutionId"]);
            Assert.Equal(
                "-100",
                sharedQueue.LastRequeueMetadata["queue.priority"]);

            Assert.Equal(1, runExecutionIndex.MarkRequeuedForRecoveryCalls);
            Assert.Equal("run-1", runExecutionIndex.LastRunId);
            Assert.Equal("execution-1", runExecutionIndex.LastExecutionId);
            Assert.Equal("test-recovery-requeue", runExecutionIndex.LastReason);
        }

        /// <summary>
        /// Verifies that local queued work bypasses execution control and DAG claim recovery.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Bypass_Execution_Control_For_Local_Queued_Recovery()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations);
            var dagExecutionStore = new FakeDagExecutionStore(operations);

            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: true);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true,
                        executionId: null),
                    Reason = "test-local-queued-recovery",
                    DryRun = false
                });

            Assert.True(result.Accepted);
            Assert.True(result.Changed);
            Assert.Equal("requeue-shared-run", result.Action);
            Assert.Null(result.ExecutionId);

            Assert.Equal(
                [
                    "queue.requeue-dispatched",
                    "index.mark-requeued-for-recovery"
                ],
                operations);

            Assert.Equal(0, executionControl.PauseForRecoveryCalls);
            Assert.Equal(0, executionControl.MarkPausedCalls);
            Assert.Equal(0, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);

            Assert.NotNull(sharedQueue.LastRequeueMetadata);
            Assert.Equal(
                "requeue-local-queued-run",
                sharedQueue.LastRequeueMetadata!["recovery.mode"]);
            Assert.Equal(
                string.Empty,
                sharedQueue.LastRequeueMetadata["recovery.failedExecutionId"]);
            Assert.Equal(
                "runtime-recovery:local-queued:shared-run-1:run-1",
                sharedQueue.LastRequeueMetadata["recovery.forensicsId"]);
            Assert.False(
                sharedQueue.LastRequeueMetadata.ContainsKey("queue.priority"));
        }

        /// <summary>
        /// Verifies that a pause not owned by the deterministic recovery owner rejects recovery.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Recovery_Pause_Is_Not_Owned()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations)
            {
                PauseRequestedByOverride = "runtime-recovery:another-owner"
            };
            var dagExecutionStore = new FakeDagExecutionStore(operations);

            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: true);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    Reason = "test-recovery-requeue",
                    DryRun = false
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("execution-control-recovery-pause-rejected", result.Reason);
            Assert.Equal(
                [
                    "control.pause-recovery"
                ],
                operations);
            Assert.Equal(0, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);
            Assert.Equal(0, sharedQueue.RequeueDispatchedCalls);
            Assert.Equal(0, runExecutionIndex.MarkRequeuedForRecoveryCalls);
        }

        /// <summary>
        /// Verifies that recovery stops when MarkPaused does not preserve recovery ownership.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_MarkPaused_Does_Not_Preserve_Recovery_Owner()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations)
            {
                MarkPausedRequestedByOverride = "operator-1"
            };
            var dagExecutionStore = new FakeDagExecutionStore(operations);

            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: true);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    Reason = "test-recovery-requeue",
                    DryRun = false
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("execution-control-recovery-mark-paused-rejected", result.Reason);

            Assert.Equal(
                [
                    "control.pause-recovery",
                    "dag.recover-running",
                    "control.mark-paused"
                ],
                operations);

            Assert.Equal(1, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);
            Assert.Equal(0, sharedQueue.RequeueDispatchedCalls);
            Assert.Equal(0, runExecutionIndex.MarkRequeuedForRecoveryCalls);
        }

        /// <summary>
        /// Verifies that enabled DAG resume rejects mutation when execution control is unavailable.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Execution_Control_Service_Is_Unavailable()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex,
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        EnableDagExecutionResume = true
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder());

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    DryRun = false
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("execution-control-service-unavailable", result.Reason);
            Assert.Empty(operations);
        }

        /// <summary>
        /// Verifies that enabled DAG resume rejects mutation when the DAG store is unavailable.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Dag_Execution_Store_Is_Unavailable()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations);
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex,
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        EnableDagExecutionResume = true
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                executionControl);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    DryRun = false
                });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("dag-execution-store-unavailable", result.Reason);
            Assert.Empty(operations);
            Assert.Equal(0, executionControl.PauseForRecoveryCalls);
        }

        /// <summary>
        /// Verifies that DAG recovery metadata and execution control mutations are omitted
        /// when DAG execution resume is disabled.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Not_Apply_Dag_Recovery_When_Dag_Resume_Is_Disabled()
        {
            var operations = new List<string>();
            var sharedQueue = new FakeSharedQueue(operations);
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex(operations);
            var executionControl = new FakeExecutionControlService(operations);
            var dagExecutionStore = new FakeDagExecutionStore(operations);

            var service = CreateService(
                sharedQueue,
                runExecutionIndex,
                executionControl,
                dagExecutionStore,
                enableDagExecutionResume: false);

            var result = await service.ApplyAsync(
                new AiRuntimeExecutionRecoveryTransitionRequest
                {
                    Ownership = CreateOwnership(
                        resolved: true,
                        canRecover: true),
                    Reason = "test-recovery-requeue",
                    DryRun = false
                });

            Assert.True(result.Accepted);
            Assert.True(result.Changed);
            Assert.Equal(
                [
                    "queue.requeue-dispatched",
                    "index.mark-requeued-for-recovery"
                ],
                operations);
            Assert.Equal(0, executionControl.PauseForRecoveryCalls);
            Assert.Equal(0, executionControl.MarkPausedCalls);
            Assert.Equal(0, dagExecutionStore.RecoverRunningStepsForRecoveryCalls);
            Assert.Null(sharedQueue.LastRequeueMetadata);
        }

        private static AiRuntimeExecutionRecoveryTransitionService CreateService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiExecutionControlService executionControlService,
            IAiDagExecutionStore dagExecutionStore,
            bool enableDagExecutionResume)
        {
            return new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runtimeRunExecutionIndex,
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        EnableDagExecutionResume = enableDagExecutionResume
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                executionControlService,
                dagExecutionStore);
        }

        /// <summary>
        /// Creates an ownership resolution result.
        /// </summary>
        private static AiSharedRunOwnershipResolutionResult CreateOwnership(
            bool resolved,
            bool canRecover,
            string? claimToken = "claim-token-1",
            string? executionId = "execution-1")
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = resolved,
                SharedRunId = resolved
                    ? "shared-run-1"
                    : null,
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "run-1",
                ExecutionId = executionId,
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                QueueStatus = resolved
                    ? AiSharedQueueItemStatus.Dispatched
                    : null,
                SharedRunStatus = resolved
                    ? AiSharedRunStatus.Dispatched
                    : null,
                ClaimToken = resolved
                    ? claimToken
                    : null,
                CanRecover = canRecover,
                Reason = resolved
                    ? "shared-run-ownership-resolved"
                    : "shared-run-ownership-not-found"
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "transition-tests",
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

        /// <summary>
        /// Fake shared queue used to verify transition order and mutation payloads.
        /// </summary>
        private sealed class FakeSharedQueue : IAiSharedQueue
        {
            private readonly IList<string> operations;

            public FakeSharedQueue(
                IList<string> operations)
            {
                ArgumentNullException.ThrowIfNull(operations);

                this.operations = operations;
            }

            public bool RejectRequeueDispatched { get; set; }

            public int RequeueDispatchedCalls { get; private set; }

            public string? LastRequeueSharedRunId { get; private set; }

            public string? LastRequeueClaimToken { get; private set; }

            public string? LastRequeueReason { get; private set; }

            public IReadOnlyDictionary<string, string>? LastRequeueMetadata { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedQueueItem> EnqueueAsync(
                AiSharedQueueItem item,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(item);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(item);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> GetAsync(
                string sharedRunId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
                bool includeTerminal = false,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>([]);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> ClaimNextAsync(
                AiSharedQueueClaimRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(null);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> ClaimAsync(
                string sharedRunId,
                AiSharedQueueClaimRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(null);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> MarkDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(
                    CreateQueueItem(
                        sharedRunId,
                        AiSharedQueueItemStatus.Dispatched,
                        claimToken));
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(
                    CreateQueueItem(
                        sharedRunId,
                        AiSharedQueueItemStatus.Pending));
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> CancelAsync(
                string sharedRunId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(
                    CreateQueueItem(
                        sharedRunId,
                        AiSharedQueueItemStatus.Cancelled));
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                return RequeueDispatchedAsync(
                    sharedRunId,
                    claimToken,
                    reason,
                    metadata: null,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason,
                IReadOnlyDictionary<string, string>? metadata,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                operations.Add("queue.requeue-dispatched");

                RequeueDispatchedCalls++;
                LastRequeueSharedRunId = sharedRunId;
                LastRequeueClaimToken = claimToken;
                LastRequeueReason = reason;
                LastRequeueMetadata = metadata;

                if (RejectRequeueDispatched)
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                return Task.FromResult<AiSharedQueueItem?>(
                    CreateQueueItem(
                        sharedRunId,
                        AiSharedQueueItemStatus.Pending,
                        metadata: metadata));
            }

            private static AiSharedQueueItem CreateQueueItem(
                string sharedRunId,
                AiSharedQueueItemStatus status,
                string? claimToken = null,
                IReadOnlyDictionary<string, string>? metadata = null)
            {
                var now = DateTimeOffset.UtcNow;
                var ownsClaim =
                    status is
                        AiSharedQueueItemStatus.Claimed or
                        AiSharedQueueItemStatus.Dispatched;

                return new AiSharedQueueItem
                {
                    SharedRunId = sharedRunId,
                    ControlPlaneId = "control-plane-1",
                    Status = status,
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                    PipelineKey = "transition-test",
                    Priority = 0,
                    ClaimedByRuntimeInstanceId = ownsClaim
                        ? "runtime-1"
                        : null,
                    ClaimedByWorkerId = ownsClaim
                        ? "worker-1"
                        : null,
                    ClaimToken = ownsClaim
                        ? claimToken
                        : null,
                    EnqueuedAtUtc = now,
                    UpdatedAtUtc = now,
                    ClaimedAtUtc = ownsClaim
                        ? now
                        : null,
                    ClaimExpiresAtUtc = ownsClaim
                        ? now.AddMinutes(5)
                        : null,
                    Metadata = metadata
                        ?? new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        /// <summary>
        /// Fake runtime run execution index used to verify transition order.
        /// </summary>
        private sealed class FakeRuntimeRunExecutionIndex : RuntimeRunExecutionIndexTestFixture
        {
            private readonly IList<string> operations;

            public FakeRuntimeRunExecutionIndex(
                IList<string> operations)
            {
                ArgumentNullException.ThrowIfNull(operations);

                this.operations = operations;
            }

            public bool MarkRequeuedForRecoveryResult { get; set; } = true;

            public int MarkRequeuedForRecoveryCalls { get; private set; }

            public string? LastRunId { get; private set; }

            public string? LastExecutionId { get; private set; }

            public string? LastReason { get; private set; }

            /// <inheritdoc />
            public override Task RegisterQueuedAsync(
                AiRuntimeRunExecutionIndexEntry entry,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task<bool> TryRegisterQueuedAsync(
                AiRuntimeRunExecutionIndexEntry entry,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public override Task MarkStartedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkCompletedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkFailedAsync(
                string runId,
                string? executionId,
                string failureReason,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkCancelledAsync(
                string runId,
                string? executionId,
                string? reason,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task<bool> MarkRequeuedForRecoveryAsync(
                string runId,
                string executionId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                operations.Add("index.mark-requeued-for-recovery");

                MarkRequeuedForRecoveryCalls++;
                LastRunId = runId;
                LastExecutionId = executionId;
                LastReason = reason;

                return Task.FromResult(
                    MarkRequeuedForRecoveryResult);
            }

            /// <inheritdoc />
            public override Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimeRunExecutionIndexEntry?>(null);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>([]);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>([]);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>([]);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>([]);
            }
        }

        /// <summary>
        /// Fake execution control service used to verify recovery ownership.
        /// </summary>
        private sealed class FakeExecutionControlService : IAiExecutionControlService
        {
            private readonly IList<string> operations;

            public FakeExecutionControlService(
                IList<string> operations)
            {
                ArgumentNullException.ThrowIfNull(operations);

                this.operations = operations;
            }

            public AiExecutionControlStatus PauseStatus { get; set; } =
                AiExecutionControlStatus.Pausing;

            public AiExecutionControlStatus MarkPausedStatus { get; set; } =
                AiExecutionControlStatus.Paused;

            public string? PauseRequestedByOverride { get; set; }

            public string? MarkPausedRequestedByOverride { get; set; }

            public int PauseForRecoveryCalls { get; private set; }

            public int MarkPausedCalls { get; private set; }

            public string? LastExecutionId { get; private set; }

            public string? LastReason { get; private set; }

            public string? LastRecoveryOwnerId { get; private set; }

            public string? LastMarkPausedRequestedBy { get; private set; }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Pausing,
                        AiExecutionControlAction.Pause,
                        requestedBy,
                        reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Resuming,
                        AiExecutionControlAction.Resume,
                        requestedBy,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                operations.Add("control.pause-recovery");

                PauseForRecoveryCalls++;
                LastExecutionId = executionId;
                LastReason = reason;
                LastRecoveryOwnerId = recoveryOwnerId;

                return Task.FromResult(
                    CreateState(
                        executionId,
                        PauseStatus,
                        AiExecutionControlAction.Pause,
                        PauseRequestedByOverride
                            ?? recoveryOwnerId,
                        reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Resuming,
                        AiExecutionControlAction.Resume,
                        recoveryOwnerId,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> CancelExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Cancelling,
                        AiExecutionControlAction.Cancel,
                        requestedBy,
                        reason));
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
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.WaitingForInput,
                        AiExecutionControlAction.WaitForInput,
                        requestedBy,
                        reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> SubmitHumanInputAsync(
                string executionId,
                string waitingKey,
                IReadOnlyDictionary<string, object?> input,
                string? submittedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Resuming,
                        AiExecutionControlAction.SubmitInput,
                        submittedBy,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiExecutionControlDecision
                    {
                        CanContinue = true,
                        Status = AiExecutionControlStatus.Running
                    });
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkPausedAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                operations.Add("control.mark-paused");

                MarkPausedCalls++;
                LastExecutionId = executionId;
                LastMarkPausedRequestedBy = requestedBy;

                return Task.FromResult(
                    CreateState(
                        executionId,
                        MarkPausedStatus,
                        AiExecutionControlAction.None,
                        MarkPausedRequestedByOverride
                            ?? requestedBy,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkRunningAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Running,
                        AiExecutionControlAction.None,
                        requestedBy,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkCancelledAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Cancelled,
                        AiExecutionControlAction.None,
                        requestedBy,
                        reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiExecutionControlState?>(
                    CreateState(
                        executionId,
                        AiExecutionControlStatus.Running,
                        AiExecutionControlAction.None,
                        requestedBy: null,
                        reason: null));
            }

            private static AiExecutionControlState CreateState(
                string executionId,
                AiExecutionControlStatus status,
                AiExecutionControlAction pendingAction,
                string? requestedBy,
                string? reason)
            {
                return new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = status,
                    PendingAction = pendingAction,
                    RequestedBy = requestedBy,
                    Reason = reason,
                    Version = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Fake DAG execution store used to verify explicit running-step recovery.
        /// </summary>
        private sealed class FakeDagExecutionStore : IAiDagExecutionStore
        {
            private readonly IList<string> operations;

            public FakeDagExecutionStore(
                IList<string> operations)
            {
                ArgumentNullException.ThrowIfNull(operations);

                this.operations = operations;
            }

            public int RecoveredRunningStepCount { get; set; }

            public int RecoverRunningStepsForRecoveryCalls { get; private set; }

            public string? LastRecoveredExecutionId { get; private set; }

            /// <inheritdoc />
            public Task<int> RecoverRunningStepsForRecoveryAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                operations.Add("dag.recover-running");

                RecoverRunningStepsForRecoveryCalls++;
                LastRecoveredExecutionId = executionId;

                return Task.FromResult(
                    RecoveredRunningStepCount);
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
