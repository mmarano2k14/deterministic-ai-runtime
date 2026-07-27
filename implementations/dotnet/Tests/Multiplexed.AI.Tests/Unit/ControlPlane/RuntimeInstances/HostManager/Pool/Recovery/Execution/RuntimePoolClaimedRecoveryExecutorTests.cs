using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Validates exact recovery transitions under one active claim lease.
    /// </summary>
    public sealed class RuntimePoolClaimedRecoveryExecutorTests
    {
        /// <summary>
        /// Verifies in-flight and local-queued transitions execute in deterministic order while the
        /// exact claim remains held.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Apply_Exact_Transitions_And_Keep_Claim_Held()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var inventory =
                CreateInventory();

            var claimedWork =
                await AcquireAsync(
                    store,
                    inventory);

            var ownershipResolver =
                new RecordingOwnershipResolver();

            var transitionService =
                new RecordingTransitionService();

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    ownershipResolver,
                    transitionService);

            var result =
                await executor.ExecuteAsync(
                    claimedWork);

            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(2, result.AcceptedCount);
            Assert.Equal(2, result.ChangedCount);
            Assert.Equal(0, result.RejectedCount);

            Assert.Equal(
                new[]
                {
                    "local-a1-flight",
                    "local-a1-queued"
                },
                ownershipResolver.Requests
                    .Select(request => request.LocalRunId)
                    .ToArray());

            Assert.Equal(
                new[]
                {
                    "runtime-pool-claimed-in-flight-recovery",
                    "runtime-pool-claimed-local-queued-recovery"
                },
                transitionService.Requests
                    .Select(request => request.Reason)
                    .ToArray());

            Assert.All(
                transitionService.Requests,
                request =>
                    Assert.False(request.DryRun));

            Assert.Equal(
                "execution-a1",
                transitionService.Requests[0]
                    .Ownership.ExecutionId);

            Assert.Null(
                transitionService.Requests[1]
                    .Ownership.ExecutionId);

            Assert.False(
                claimedWork.Lease!.IsReleased);

            Assert.True(
                await store.IsActiveLeaseAsync(
                    claimedWork.Claim.FailureId,
                    claimedWork.Claim.ClaimId,
                    claimedWork.Lease.LeaseId));

            await claimedWork.Lease.DisposeAsync();

            Assert.False(
                await store.IsActiveLeaseAsync(
                    claimedWork.Claim.FailureId,
                    claimedWork.Claim.ClaimId,
                    claimedWork.Lease.LeaseId));
        }

        /// <summary>
        /// Verifies an AlreadyClaimed observation cannot invoke transitions.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Claim_Not_Acquired()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var inventory =
                CreateInventory();

            var acquired =
                await AcquireAsync(
                    store,
                    inventory);

            var claimedWork =
                acquired with
                {
                    Status =
                        AiRuntimePoolRecoveryClaimAcquisitionStatus
                            .AlreadyClaimed,
                    Lease = null
                };

            var transitionService =
                new RecordingTransitionService();

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    new RecordingOwnershipResolver(),
                    transitionService);

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRecoveryExecutionAuthorityException>(
                    () =>
                        executor.ExecuteAsync(
                            claimedWork));

            Assert.Equal(
                AiRuntimePoolRecoveryExecutionAuthorityFailure
                    .ClaimNotAcquired,
                exception.Reason);

            Assert.Empty(
                transitionService.Requests);

            await acquired.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies a released lease cannot authorize transitions.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Released_Lease()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var claimedWork =
                await AcquireAsync(
                    store,
                    CreateInventory());

            await claimedWork.Lease!.DisposeAsync();

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    new RecordingOwnershipResolver(),
                    new RecordingTransitionService());

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRecoveryExecutionAuthorityException>(
                    () =>
                        executor.ExecuteAsync(
                            claimedWork));

            Assert.Equal(
                AiRuntimePoolRecoveryExecutionAuthorityFailure
                    .LeaseReleased,
                exception.Reason);
        }

        /// <summary>
        /// Verifies ownership resolving to sibling A2 is rejected before mutation.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Sibling_Ownership()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var claimedWork =
                await AcquireAsync(
                    store,
                    CreateInventory());

            var transitionService =
                new RecordingTransitionService();

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    new RecordingOwnershipResolver
                    {
                        RuntimeInstanceIdOverride =
                            "runtime-a2"
                    },
                    transitionService);

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRecoveryExecutionAuthorityException>(
                    () =>
                        executor.ExecuteAsync(
                            claimedWork));

            Assert.Equal(
                AiRuntimePoolRecoveryExecutionAuthorityFailure
                    .OwnershipBoundaryViolation,
                exception.Reason);

            Assert.Empty(
                transitionService.Requests);

            Assert.True(
                await store.IsActiveLeaseAsync(
                    claimedWork.Claim.FailureId,
                    claimedWork.Claim.ClaimId,
                    claimedWork.Lease!.LeaseId));

            await claimedWork.Lease.DisposeAsync();
        }

        /// <summary>
        /// Verifies unsupported recoverable states receive a deterministic no-mutation outcome.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Skip_Other_Recoverable_Without_Transition()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var inventory =
                CreateInventory() with
                {
                    Candidates =
                        new[]
                        {
                            CreateCandidate(
                                localRunId: "local-a1-other",
                                executionId: null,
                                sharedRunId: "shared-run-other",
                                kind:
                                    AiRuntimePoolAssignedWorkKind
                                        .OtherRecoverable,
                                createdSecond: 1)
                        }
                };

            var claimedWork =
                await AcquireAsync(
                    store,
                    inventory);

            var ownershipResolver =
                new RecordingOwnershipResolver();

            var transitionService =
                new RecordingTransitionService();

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    ownershipResolver,
                    transitionService);

            var result =
                await executor.ExecuteAsync(
                    claimedWork);

            var outcome =
                Assert.Single(result.Outcomes);

            Assert.False(outcome.Transition.Accepted);
            Assert.False(outcome.Transition.Changed);

            Assert.Equal(
                "unsupported-recovery-candidate-kind",
                outcome.Transition.Reason);

            Assert.Empty(ownershipResolver.Requests);
            Assert.Empty(transitionService.Requests);

            await claimedWork.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies a transition exception does not release the exact claim.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Keep_Claim_When_Transition_Throws()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var claimedWork =
                await AcquireAsync(
                    store,
                    CreateInventory());

            var executor =
                new AiRuntimePoolClaimedRecoveryExecutor(
                    store,
                    new RecordingOwnershipResolver(),
                    new RecordingTransitionService
                    {
                        ExceptionToThrow =
                            new InvalidOperationException(
                                "transition-failed")
                    });

            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () =>
                    executor.ExecuteAsync(
                        claimedWork));

            Assert.False(
                claimedWork.Lease!.IsReleased);

            Assert.True(
                await store.IsActiveLeaseAsync(
                    claimedWork.Claim.FailureId,
                    claimedWork.Claim.ClaimId,
                    claimedWork.Lease.LeaseId));

            await claimedWork.Lease.DisposeAsync();
        }

        /// <summary>
        /// Acquires one exact claim around the supplied inventory.
        /// </summary>
        private static async Task<
            AiRuntimePoolClaimedAssignedWork>
            AcquireAsync(
                IAiRuntimePoolRecoveryClaimStore store,
                AiRuntimePoolAssignedWorkInventory inventory)
        {
            var acquisition =
                await store.TryAcquireAsync(
                    new AiRuntimePoolRecoveryClaimRequest
                    {
                        FailureId =
                            inventory.FailureId,
                        PoolId = inventory.PoolId,
                        HostId = inventory.HostId,
                        RuntimeInstanceId =
                            inventory.RuntimeInstanceId,
                        RouteId = inventory.RouteId,
                        InventoryFingerprint =
                            AiRuntimePoolRecoveryInventoryFingerprint
                                .Calculate(inventory),
                        CandidateCount =
                            inventory.Candidates.Count,
                        ClaimedBy =
                            "coordinator-01"
                    });

            return new AiRuntimePoolClaimedAssignedWork
            {
                Inventory = inventory,
                Status = acquisition.Status,
                Claim = acquisition.Claim,
                Lease = acquisition.Lease
            };
        }

        /// <summary>
        /// Creates one deterministic exact A1 inventory.
        /// </summary>
        private static AiRuntimePoolAssignedWorkInventory
            CreateInventory()
        {
            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = "failure-a1",
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = "runtime-a1",
                RouteId = "route-a1",
                EnumeratedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        5,
                        TimeSpan.Zero),
                Candidates =
                    new[]
                    {
                        CreateCandidate(
                            localRunId:
                                "local-a1-flight",
                            executionId:
                                "execution-a1",
                            sharedRunId:
                                "shared-run-01",
                            kind:
                                AiRuntimePoolAssignedWorkKind
                                    .InFlight,
                            createdSecond: 1),
                        CreateCandidate(
                            localRunId:
                                "local-a1-queued",
                            executionId: null,
                            sharedRunId:
                                "shared-run-02",
                            kind:
                                AiRuntimePoolAssignedWorkKind
                                    .LocalQueued,
                            createdSecond: 2)
                    }
            };
        }

        /// <summary>
        /// Creates one exact assigned-work candidate.
        /// </summary>
        private static AiRuntimePoolAssignedWorkCandidate
            CreateCandidate(
                string localRunId,
                string? executionId,
                string? sharedRunId,
                AiRuntimePoolAssignedWorkKind kind,
                int createdSecond)
        {
            return new AiRuntimePoolAssignedWorkCandidate
            {
                FailureId = "failure-a1",
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    "runtime-a1",
                RouteId = "route-a1",
                LocalRunId = localRunId,
                ExecutionId = executionId,
                Status =
                    kind ==
                        AiRuntimePoolAssignedWorkKind
                            .LocalQueued
                        ? "queued"
                        : "running",
                TenantId = "tenant-01",
                TenantGroupId =
                    "tenant-group-01",
                SharedRunId = sharedRunId,
                Kind = kind,
                CreatedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        createdSecond,
                        TimeSpan.Zero)
            };
        }

        /// <summary>
        /// Records exact ownership requests.
        /// </summary>
        private sealed class RecordingOwnershipResolver :
            IAiSharedRunOwnershipResolver
        {
            /// <summary>
            /// Gets or sets a runtime identity override used by boundary tests.
            /// </summary>
            public string? RuntimeInstanceIdOverride { get; init; }

            /// <summary>
            /// Gets the recorded requests.
            /// </summary>
            public List<AiSharedRunOwnershipResolutionRequest>
                Requests { get; } = new();

            /// <inheritdoc />
            public Task<AiSharedRunOwnershipResolutionResult>
                ResolveAsync(
                    AiSharedRunOwnershipResolutionRequest request,
                    CancellationToken cancellationToken = default)
            {
                this.Requests.Add(request);

                return Task.FromResult(
                    new AiSharedRunOwnershipResolutionResult
                    {
                        Resolved = true,
                        CanRecover = true,
                        SharedRunId =
                            request.SharedRunId,
                        RuntimeInstanceId =
                            this.RuntimeInstanceIdOverride ??
                            request.RuntimeInstanceId,
                        LocalRunId =
                            request.LocalRunId,
                        ExecutionId =
                            request.ExecutionId,
                        TenantId = request.TenantId,
                        TenantGroupId =
                            request.TenantGroupId,
                        ClaimToken =
                            "shared-queue-claim-token",
                        Reason = "resolved"
                    });
            }
        }

        /// <summary>
        /// Records exact existing recovery transition requests.
        /// </summary>
        private sealed class RecordingTransitionService :
            IAiRuntimeExecutionRecoveryTransitionService
        {
            /// <summary>
            /// Gets or sets an exception thrown during transition.
            /// </summary>
            public Exception? ExceptionToThrow { get; init; }

            /// <summary>
            /// Gets the recorded transition requests.
            /// </summary>
            public List<AiRuntimeExecutionRecoveryTransitionRequest>
                Requests { get; } = new();

            /// <inheritdoc />
            public Task<AiRuntimeExecutionRecoveryTransitionResult>
                ApplyAsync(
                    AiRuntimeExecutionRecoveryTransitionRequest request,
                    CancellationToken cancellationToken = default)
            {
                this.Requests.Add(request);

                if (this.ExceptionToThrow is not null)
                {
                    throw this.ExceptionToThrow;
                }

                return Task.FromResult(
                    new AiRuntimeExecutionRecoveryTransitionResult
                    {
                        Accepted = true,
                        Changed = true,
                        SharedRunId =
                            request.Ownership.SharedRunId,
                        RuntimeInstanceId =
                            request.Ownership.RuntimeInstanceId,
                        LocalRunId =
                            request.Ownership.LocalRunId,
                        ExecutionId =
                            request.Ownership.ExecutionId,
                        Action =
                            "requeue-shared-run",
                        Reason =
                            request.Reason ??
                            "recovery"
                    });
            }
        }
    }
}
