using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for
    /// <see cref="AiRuntimeHierarchicalCapacitySelector" />.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacitySelectorTests
    {
        /// <summary>
        /// Verifies that the selector preserves the exact hierarchical capacity order
        /// regardless of candidate enumeration order.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Preserve_Exact_Hierarchy_Order()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var candidates =
                new[]
                {
                    CreateExternalNodeCandidate(),
                    CreateRuntimePoolPodCandidate(),
                    CreateExistingPodProcessCandidate(),
                    CreateExistingPoolRuntimeSlotCandidate(),
                    CreateWarmRuntimeCandidate()
                };

            var result =
                await selector
                    .SelectAsync(
                        CreateRequest(),
                        candidates)
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel.CompatibleWarmRuntime,
                result.Level);

            Assert.Same(
                candidates[4],
                result.Candidate);

            Assert.False(
                result.IsBackpressure);

            Assert.Equal(
                candidates.Length,
                result.EvaluatedCandidateCount);
        }

        /// <summary>
        /// Verifies that draining, suppressed, incompatible, and unavailable candidates
        /// are skipped before the next safe hierarchy level is selected.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Exclude_Unsafe_Candidates()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var warm =
                CreateWarmRuntimeCandidate();

            warm.IsDraining = true;

            var existingSlot =
                CreateExistingPoolRuntimeSlotCandidate();

            existingSlot.IsSuppressed = true;

            var processCreation =
                CreateExistingPodProcessCandidate();

            processCreation.IsCompatible = false;

            var podCreation =
                CreateRuntimePoolPodCandidate();

            podCreation.IsAvailable = false;

            var externalNode =
                CreateExternalNodeCandidate();

            var result =
                await selector
                    .SelectAsync(
                        CreateRequest(),
                        new[]
                        {
                            warm,
                            existingSlot,
                            processCreation,
                            podCreation,
                            externalNode
                        })
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel
                    .ExternalNodeCapacityRequest,
                result.Level);

            Assert.Same(
                externalNode,
                result.Candidate);
        }

        /// <summary>
        /// Verifies that malformed candidate identity is rejected instead of being
        /// inferred from metadata.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Reject_Malformed_FirstClass_Identity()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var malformedWarm =
                CreateWarmRuntimeCandidate();

            malformedWarm.RuntimeInstanceId = null;
            malformedWarm.Metadata =
                new Dictionary<string, string>
                {
                    ["runtimeInstanceId"] = "metadata-runtime"
                };

            var malformedProcess =
                CreateExistingPodProcessCandidate();

            malformedProcess.RuntimeInstanceId = "stale-runtime";

            var malformedPod =
                CreateRuntimePoolPodCandidate();

            malformedPod.HostId = "stale-pod-uid";

            var malformedNode =
                CreateExternalNodeCandidate();

            malformedNode.RuntimeInstanceId = "stale-runtime";

            var result =
                await selector
                    .SelectAsync(
                        CreateRequest(),
                        new[]
                        {
                            malformedWarm,
                            malformedProcess,
                            malformedPod,
                            malformedNode
                        })
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel.Backpressure,
                result.Level);

            Assert.True(
                result.IsBackpressure);

            Assert.Null(
                result.Candidate);

            Assert.Equal(
                "hierarchical-capacity-exhausted",
                result.Reason);
        }

        /// <summary>
        /// Verifies that equivalent safe candidates at the same level are selected by
        /// deterministic first-class identity rather than enumeration order.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Break_Ties_By_FirstClass_Identity()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var runtimeB =
                CreateWarmRuntimeCandidate();

            runtimeB.RuntimeInstanceId = "runtime-b";

            var runtimeA =
                CreateWarmRuntimeCandidate();

            runtimeA.RuntimeInstanceId = "runtime-a";

            var result =
                await selector
                    .SelectAsync(
                        CreateRequest(),
                        new[]
                        {
                            runtimeB,
                            runtimeA
                        })
                    .ConfigureAwait(false);

            Assert.Same(
                runtimeA,
                result.Candidate);

            Assert.Equal(
                "runtime-a",
                result.Candidate!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that an empty inventory produces explicit backpressure.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Apply_Backpressure_When_Inventory_Is_Empty()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var result =
                await selector
                    .SelectAsync(
                        CreateRequest(),
                        Array.Empty<AiRuntimeCapacitySelectionCandidate>())
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel.Backpressure,
                result.Level);

            Assert.True(
                result.IsBackpressure);

            Assert.Null(
                result.Candidate);

            Assert.Equal(
                0,
                result.EvaluatedCandidateCount);
        }

        /// <summary>
        /// Verifies that cancellation is observed before candidate selection.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Observe_Cancellation()
        {
            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            using var cancellation =
                new CancellationTokenSource();

            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () =>
                    await selector
                        .SelectAsync(
                            CreateRequest(),
                            new[]
                            {
                                CreateWarmRuntimeCandidate()
                            },
                            cancellation.Token)
                        .ConfigureAwait(false));
        }

        /// <summary>
        /// Verifies that concurrent selectors presented with different enumeration
        /// orders always choose the same least-expensive safe hierarchy level.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Converge_On_Exact_Order_Under_Concurrency()
        {
            const int contenderCount = 64;

            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        index =>
                            Task.Run(
                                async () =>
                                {
                                    var candidates =
                                        new[]
                                        {
                                            CreateWarmRuntimeCandidate(),
                                            CreateExistingPoolRuntimeSlotCandidate(),
                                            CreateExistingPodProcessCandidate(),
                                            CreateRuntimePoolPodCandidate(),
                                            CreateExternalNodeCandidate()
                                        };

                                    var rotated =
                                        candidates
                                            .Skip(
                                                index %
                                                candidates.Length)
                                            .Concat(
                                                candidates.Take(
                                                    index %
                                                    candidates.Length))
                                            .ToArray();

                                    await startGate.Task;

                                    return await selector.SelectAsync(
                                        CreateRequest(),
                                        rotated);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            Assert.All(
                results,
                result =>
                {
                    Assert.Equal(
                        AiRuntimeCapacitySelectionLevel
                            .CompatibleWarmRuntime,
                        result.Level);
                    Assert.Equal(
                        "runtime-warm",
                        result.Candidate!.RuntimeInstanceId);
                    Assert.False(result.IsBackpressure);
                });
        }

        /// <summary>
        /// Verifies that concurrent requests apply explicit backpressure when every
        /// hierarchy level is unsafe, unavailable, or structurally invalid.
        /// </summary>
        [Fact]
        public async Task SelectAsync_Should_Backpressure_When_All_Levels_Are_Exhausted_Concurrently()
        {
            const int contenderCount = 64;

            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var warm = CreateWarmRuntimeCandidate();
            warm.IsSuppressed = true;

            var existingSlot =
                CreateExistingPoolRuntimeSlotCandidate();
            existingSlot.IsDraining = true;

            var processCreation =
                CreateExistingPodProcessCandidate();
            processCreation.AvailableProcessSlots = 0;

            var podCreation =
                CreateRuntimePoolPodCandidate();
            podCreation.IsAvailable = false;

            var externalNode =
                CreateExternalNodeCandidate();
            externalNode.IsCompatible = false;

            var candidates =
                new[]
                {
                    warm,
                    existingSlot,
                    processCreation,
                    podCreation,
                    externalNode
                };

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        _ =>
                            Task.Run(
                                async () =>
                                {
                                    await startGate.Task;
                                    return await selector.SelectAsync(
                                        CreateRequest(),
                                        candidates);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            Assert.All(
                results,
                result =>
                {
                    Assert.True(result.IsBackpressure);
                    Assert.Equal(
                        AiRuntimeCapacitySelectionLevel.Backpressure,
                        result.Level);
                    Assert.Null(result.Candidate);
                    Assert.Equal(
                        "hierarchical-capacity-exhausted",
                        result.Reason);
                });
        }

        /// <summary>
        /// Creates the existing provider-level scale-out request reused by hierarchical
        /// capacity selection.
        /// </summary>
        /// <returns>The scale-out provider request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest()
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = "step-7a-request",
                ControlPlaneId = "step-7a-control-plane",
                SharedRunId = "step-7a-shared-run",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey: "step-7a:tenant:context",
                        project: "step-7a",
                        userId: "unit-test",
                        tenantId: "tenant-step-7a",
                        tenantGroupId: "tenant-group-step-7a",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7a",
                TenantGroupId = "tenant-group-step-7a",
                ProviderHint = "http",
                RequestedTargetInstanceCount = 1
            };
        }

        /// <summary>
        /// Creates one compatible warm-runtime candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate
            CreateWarmRuntimeCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel.CompatibleWarmRuntime,
                PoolId = "pool-step-7a",
                HostId = "pod-uid-step-7a-a",
                RuntimeInstanceId = "runtime-warm",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true,
                AvailableRunSlots = 1
            };
        }

        /// <summary>
        /// Creates one existing pooled-runtime slot candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate
            CreateExistingPoolRuntimeSlotCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .ExistingPoolRuntimeSlot,
                PoolId = "pool-step-7a",
                HostId = "pod-uid-step-7a-a",
                RuntimeInstanceId = "runtime-busy-with-slot",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true,
                AvailableRunSlots = 1
            };
        }

        /// <summary>
        /// Creates one existing Runtime Pool Pod process-creation candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate
            CreateExistingPodProcessCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .ExistingPoolPodProcessCreation,
                PoolId = "pool-step-7a",
                HostId = "pod-uid-step-7a-a",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true,
                AvailableProcessSlots = 1
            };
        }

        /// <summary>
        /// Creates one Runtime Pool Pod creation candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate
            CreateRuntimePoolPodCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .RuntimePoolPodCreation,
                PoolId = "pool-step-7a",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true
            };
        }

        /// <summary>
        /// Creates one external node-capacity candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate
            CreateExternalNodeCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .ExternalNodeCapacityRequest,
                PoolId = "pool-step-7a",
                ProviderName = "kubernetes",
                IsCompatible = true,
                IsAvailable = true
            };
        }
    }
}
