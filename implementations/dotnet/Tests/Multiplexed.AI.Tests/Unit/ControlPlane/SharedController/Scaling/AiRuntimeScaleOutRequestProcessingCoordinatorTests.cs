using System.Collections.Concurrent;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="AiRuntimeScaleOutRequestProcessingCoordinator" />.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestProcessingCoordinatorTests
    {
        /// <summary>
        /// Verifies process-wide and per-control-plane workflow concurrency boundaries.
        /// </summary>
        [Fact]
        public async Task ScheduleAsync_Should_Enforce_Global_And_PerControlPlane_Limits()
        {
            var coordinationKey = $"request-processing-limits-{Guid.NewGuid():N}";
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedCount = 0;
            var activeCount = 0;
            var maximumActiveCount = 0;
            var activeByControlPlane = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var maximumByControlPlane = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

            Task Schedule(
                string controlPlaneId,
                string requestId)
            {
                return AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    controlPlaneId,
                    requestId,
                    $"shared-{requestId}",
                    isRecovery: false,
                    maxConcurrentWorkflows: 2,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    async cancellationToken =>
                    {
                        Interlocked.Increment(ref startedCount);

                        var currentActive = Interlocked.Increment(ref activeCount);
                        UpdateMaximum(ref maximumActiveCount, currentActive);

                        var currentForControlPlane =
                            activeByControlPlane.AddOrUpdate(
                                controlPlaneId,
                                1,
                                static (_, current) => current + 1);

                        maximumByControlPlane.AddOrUpdate(
                            controlPlaneId,
                            currentForControlPlane,
                            (_, currentMaximum) => Math.Max(currentMaximum, currentForControlPlane));

                        try
                        {
                            await release.Task
                                .WaitAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            activeByControlPlane.AddOrUpdate(
                                controlPlaneId,
                                0,
                                static (_, current) => current - 1);

                            Interlocked.Decrement(ref activeCount);
                        }
                    });
            }

            var tasks = new[]
            {
                Schedule("cp-a", "request-a-1"),
                Schedule("cp-a", "request-a-2"),
                Schedule("cp-b", "request-b-1"),
                Schedule("cp-c", "request-c-1")
            };

            await WaitUntilAsync(
                    () => Volatile.Read(ref startedCount) == 2,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.Equal(2, Volatile.Read(ref maximumActiveCount));
            Assert.All(
                maximumByControlPlane.Values,
                value => Assert.Equal(1, value));

            release.TrySetResult(true);

            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.Equal(4, Volatile.Read(ref startedCount));
            Assert.Equal(2, Volatile.Read(ref maximumActiveCount));
            Assert.All(
                maximumByControlPlane.Values,
                value => Assert.Equal(1, value));
        }

        /// <summary>
        /// Verifies that recovery work is selected before normal work after an active slot is released.
        /// </summary>
        [Fact]
        public async Task ScheduleAsync_Should_Prioritize_Recovery_Without_Interrupting_Active_Work()
        {
            var coordinationKey = $"request-processing-recovery-{Guid.NewGuid():N}";
            var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var remainingRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatchOrder = new ConcurrentQueue<string>();

            var activeTask =
                Schedule(
                    controlPlaneId: "cp-active",
                    requestId: "request-active",
                    isRecovery: false,
                    release: firstRelease);

            await WaitUntilAsync(
                    () => dispatchOrder.Count == 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            var normalTask =
                Schedule(
                    controlPlaneId: "cp-normal",
                    requestId: "request-normal",
                    isRecovery: false,
                    release: remainingRelease);

            var recoveryTask =
                Schedule(
                    controlPlaneId: "cp-recovery",
                    requestId: "request-recovery",
                    isRecovery: true,
                    release: remainingRelease);

            firstRelease.TrySetResult(true);

            await WaitUntilAsync(
                    () => dispatchOrder.Count >= 2,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.Equal(
                new[]
                {
                    "request-active",
                    "request-recovery"
                },
                dispatchOrder.Take(2));

            remainingRelease.TrySetResult(true);

            await Task.WhenAll(activeTask, normalTask, recoveryTask)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Task Schedule(
                string controlPlaneId,
                string requestId,
                bool isRecovery,
                TaskCompletionSource<bool> release)
            {
                return AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    controlPlaneId,
                    requestId,
                    $"shared-{requestId}",
                    isRecovery,
                    maxConcurrentWorkflows: 1,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    async cancellationToken =>
                    {
                        dispatchOrder.Enqueue(requestId);

                        await release.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    });
            }
        }

        /// <summary>
        /// Verifies request-id single-flight inside the process-wide coordinator.
        /// </summary>
        [Fact]
        public async Task ScheduleAsync_Should_Deduplicate_The_Same_Logical_Request()
        {
            var coordinationKey = $"request-processing-single-flight-{Guid.NewGuid():N}";
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflowCallCount = 0;

            async Task Workflow(
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref workflowCallCount);

                await release.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var first =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-a",
                    "request-1",
                    "shared-1",
                    isRecovery: true,
                    maxConcurrentWorkflows: 2,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    Workflow);

            var duplicate =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-a",
                    "request-1",
                    "shared-1",
                    isRecovery: true,
                    maxConcurrentWorkflows: 2,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    Workflow);

            Assert.Same(first, duplicate);

            await WaitUntilAsync(
                    () => Volatile.Read(ref workflowCallCount) == 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            release.TrySetResult(true);

            await Task.WhenAll(first, duplicate)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.Equal(1, Volatile.Read(ref workflowCallCount));
        }

        /// <summary>
        /// Verifies that a queued workflow performs no work before admission.
        /// </summary>
        [Fact]
        public async Task ScheduleAsync_Should_Not_Invoke_Queued_Workflow_Before_Admission()
        {
            var coordinationKey = $"request-processing-admission-boundary-{Guid.NewGuid():N}";
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var first =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-a",
                    "request-a",
                    "shared-a",
                    isRecovery: false,
                    maxConcurrentWorkflows: 1,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    async cancellationToken =>
                    {
                        firstStarted.TrySetResult(true);

                        await firstRelease.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    });

            await firstStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            var second =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-b",
                    "request-b",
                    "shared-b",
                    isRecovery: false,
                    maxConcurrentWorkflows: 1,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        secondStarted.TrySetResult(true);
                        return Task.CompletedTask;
                    });

            await Task.Delay(100)
                .ConfigureAwait(false);

            Assert.False(secondStarted.Task.IsCompleted);

            firstRelease.TrySetResult(true);

            await secondStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that queued cancellation completes without invoking the workflow.
        /// </summary>
        [Fact]
        public async Task ScheduleAsync_Should_Cancel_Queued_Workflow_Without_Executing_It()
        {
            var coordinationKey = $"request-processing-cancel-{Guid.NewGuid():N}";
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondWorkflowCallCount = 0;

            var first =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-a",
                    "request-a",
                    "shared-a",
                    isRecovery: false,
                    maxConcurrentWorkflows: 1,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    async cancellationToken =>
                    {
                        firstStarted.TrySetResult(true);

                        await firstRelease.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    });

            await firstStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource();

            var second =
                AiRuntimeScaleOutRequestProcessingCoordinator.ScheduleAsync(
                    coordinationKey,
                    "cp-b",
                    "request-b",
                    "shared-b",
                    isRecovery: true,
                    maxConcurrentWorkflows: 1,
                    maxConcurrentWorkflowsPerControlPlane: 1,
                    recoveryDispatchBurstLimit: 3,
                    cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref secondWorkflowCallCount);
                        return Task.CompletedTask;
                    },
                    cancellation.Token);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () =>
                        await second
                            .WaitAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(0, Volatile.Read(ref secondWorkflowCallCount));

            firstRelease.TrySetResult(true);

            await first
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Waits until a predicate becomes true.
        /// </summary>
        private static async Task WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;

            while (!predicate())
            {
                if (DateTimeOffset.UtcNow - startedAtUtc >= timeout)
                {
                    throw new TimeoutException(
                        $"Condition was not satisfied within '{timeout}'.");
                }

                await Task.Delay(10)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Updates an integer maximum atomically.
        /// </summary>
        private static void UpdateMaximum(
            ref int target,
            int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);

                if (candidate <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(
                        ref target,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }
}
