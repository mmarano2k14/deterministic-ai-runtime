using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Validates bounded execution of the Step 7D and Step 7E hierarchy mutations.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacityExecutionCoordinatorTests
    {
        /// <summary>
        /// Verifies that an existing-host process decision invokes only the exact
        /// process creation executor.
        /// </summary>
        [Fact]
        public async Task SelectReserveAndExecuteAsync_Should_Execute_Process_Creation()
        {
            var candidate = CreateProcessCandidate();
            var processExecutor = new RecordingProcessCreationExecutor();
            var podExecutor = new ThrowingPodCreationExecutor();
            var coordinator =
                new AiRuntimeHierarchicalCapacityExecutionCoordinator(
                    new FixedReservationCoordinator(
                        CreateReservationResult(
                            AiRuntimeCapacitySelectionLevel
                                .ExistingPoolPodProcessCreation,
                            candidate)),
                    processExecutor,
                    podExecutor);

            var result =
                await coordinator.SelectReserveAndExecuteAsync(
                    CreateRequest());

            Assert.True(result.IsProcessCreated);
            Assert.False(result.IsPodCreated);
            Assert.NotNull(result.ProcessCreation);
            Assert.Null(result.PodCreation);
            Assert.Equal(1, processExecutor.CallCount);
            Assert.Same(candidate, processExecutor.LastCandidate);
        }

        /// <summary>
        /// Verifies that a Runtime Pool Pod creation decision invokes only the Pod
        /// creation executor.
        /// </summary>
        [Fact]
        public async Task SelectReserveAndExecuteAsync_Should_Execute_Pod_Creation()
        {
            var candidate = CreatePodCandidate();
            var podExecutor = new RecordingPodCreationExecutor();
            var coordinator =
                new AiRuntimeHierarchicalCapacityExecutionCoordinator(
                    new FixedReservationCoordinator(
                        CreateReservationResult(
                            AiRuntimeCapacitySelectionLevel
                                .RuntimePoolPodCreation,
                            candidate)),
                    new ThrowingProcessCreationExecutor(),
                    podExecutor);

            var result =
                await coordinator.SelectReserveAndExecuteAsync(
                    CreateRequest());

            Assert.True(result.IsPodCreated);
            Assert.False(result.IsProcessCreated);
            Assert.Null(result.ProcessCreation);
            Assert.NotNull(result.PodCreation);
            Assert.Equal(1, podExecutor.CallCount);
            Assert.Same(candidate, podExecutor.LastCandidate);
        }

        /// <summary>
        /// Verifies that an existing runtime reservation remains owned by Step 7C and
        /// invokes no capacity creation executor.
        /// </summary>
        [Fact]
        public async Task SelectReserveAndExecuteAsync_Should_Not_Execute_Runtime_Reservation()
        {
            var coordinator =
                new AiRuntimeHierarchicalCapacityExecutionCoordinator(
                    new FixedReservationCoordinator(
                        CreateReservationResult(
                            AiRuntimeCapacitySelectionLevel
                                .ExistingPoolRuntimeSlot,
                            new AiRuntimeCapacitySelectionCandidate
                            {
                                Level =
                                    AiRuntimeCapacitySelectionLevel
                                        .ExistingPoolRuntimeSlot,
                                PoolId = "pool-step-7e",
                                HostId = "host-step-7e",
                                RuntimeInstanceId = "runtime-step-7e-1",
                                ProviderName = "http",
                                IsCompatible = true,
                                IsAvailable = true,
                                PublishedAvailableRunSlots = 1,
                                AvailableRunSlots = 1
                            })),
                    new ThrowingProcessCreationExecutor(),
                    new ThrowingPodCreationExecutor());

            var result =
                await coordinator.SelectReserveAndExecuteAsync(
                    CreateRequest());

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel.ExistingPoolRuntimeSlot,
                result.Decision.Level);
            Assert.Null(result.ProcessCreation);
            Assert.Null(result.PodCreation);
        }

        /// <summary>
        /// Creates one exact existing-host process candidate.
        /// </summary>
        private static AiRuntimeCapacitySelectionCandidate
            CreateProcessCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .ExistingPoolPodProcessCreation,
                PoolId = "pool-step-7e",
                HostId = "host-step-7e",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true,
                AvailableProcessSlots = 1
            };
        }

        /// <summary>
        /// Creates one exact new-Pod candidate.
        /// </summary>
        private static AiRuntimeCapacitySelectionCandidate
            CreatePodCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .RuntimePoolPodCreation,
                PoolId = "pool-step-7e",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true
            };
        }

        /// <summary>
        /// Creates a fixed Step 7C result.
        /// </summary>
        private static AiRuntimeHierarchicalCapacityReservationResult
            CreateReservationResult(
                AiRuntimeCapacitySelectionLevel level,
                AiRuntimeCapacitySelectionCandidate candidate)
        {
            return new AiRuntimeHierarchicalCapacityReservationResult
            {
                Decision =
                    new AiRuntimeCapacitySelectionDecision
                    {
                        Level = level,
                        Candidate = candidate,
                        EvaluatedCandidateCount = 1,
                        Reason = "step-7e-fixed-decision"
                    },
                SelectionAttemptCount = 1
            };
        }

        /// <summary>
        /// Creates one provider-level request.
        /// </summary>
        private static AiRuntimeScaleOutProviderRequest CreateRequest()
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ControlPlaneId = "step-7e-control-plane",
                SharedRunId = Guid.NewGuid().ToString("N"),
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey:
                            string.Concat(
                                "step-7e:",
                                Guid.NewGuid().ToString("N")),
                        project: "step-7e",
                        userId: "unit-test",
                        tenantId: "tenant-step-7e",
                        tenantGroupId: "tenant-group-step-7e",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7e",
                TenantGroupId = "tenant-group-step-7e",
                ProviderHint = "http",
                RequestedTargetInstanceCount = 1
            };
        }

        /// <summary>
        /// Returns one fixed Step 7C result.
        /// </summary>
        private sealed class FixedReservationCoordinator :
            IAiRuntimeHierarchicalCapacityReservationCoordinator
        {
            private readonly AiRuntimeHierarchicalCapacityReservationResult
                result;

            public FixedReservationCoordinator(
                AiRuntimeHierarchicalCapacityReservationResult result)
            {
                this.result = result;
            }

            public Task<AiRuntimeHierarchicalCapacityReservationResult>
                SelectAndReserveAsync(
                    AiRuntimeScaleOutProviderRequest request,
                    int runCount = 1,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.result);
            }
        }

        private sealed class RecordingProcessCreationExecutor :
            IAiRuntimePoolProcessCreationExecutor
        {
            public int CallCount { get; private set; }

            public AiRuntimeCapacitySelectionCandidate? LastCandidate
            {
                get;
                private set;
            }

            public Task<AiRuntimePoolProcessCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.CallCount++;
                this.LastCandidate = candidate;

                return Task.FromResult(
                    new AiRuntimePoolProcessCreationResult
                    {
                        RequestId = request.RequestId,
                        PoolId = candidate.PoolId!,
                        HostId = candidate.HostId!,
                        Status =
                            AiRuntimePoolProcessCreationStatus.Created,
                        ProcessCountBefore = 1,
                        ProcessCountAfter = 2,
                        MaximumProcessCount = 3,
                        CreatedRuntimeInstanceIds =
                            new[] { "runtime-step-7e-2" }
                    });
            }
        }

        private sealed class RecordingPodCreationExecutor :
            IAiRuntimePoolPodCreationExecutor
        {
            public int CallCount { get; private set; }

            public AiRuntimeCapacitySelectionCandidate? LastCandidate
            {
                get;
                private set;
            }

            public Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.CallCount++;
                this.LastCandidate = candidate;

                return Task.FromResult(
                    new AiRuntimePoolPodCreationResult
                    {
                        RequestId = request.RequestId,
                        PoolId = candidate.PoolId!,
                        HostRequestId = "host-request-step-7e",
                        PrimaryRuntimeInstanceId =
                            "runtime-step-7e-primary",
                        PodUid = "pod-uid-step-7e",
                        Status = AiRuntimePoolPodCreationStatus.Created,
                        RuntimeInstanceIds =
                            new[] { "runtime-step-7e-primary" }
                    });
            }
        }

        private sealed class ThrowingProcessCreationExecutor :
            IAiRuntimePoolProcessCreationExecutor
        {
            public Task<AiRuntimePoolProcessCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Process creation must not be invoked for this hierarchy level.");
            }
        }

        private sealed class ThrowingPodCreationExecutor :
            IAiRuntimePoolPodCreationExecutor
        {
            public Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Pod creation must not be invoked for this hierarchy level.");
            }
        }
    }
}
