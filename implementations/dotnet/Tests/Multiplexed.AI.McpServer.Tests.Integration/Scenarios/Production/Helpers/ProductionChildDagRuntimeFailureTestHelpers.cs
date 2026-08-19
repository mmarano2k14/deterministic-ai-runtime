using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.Stores;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable physical runtime-boundary failure proofs used by nested Child DAG production scenarios.
    /// </summary>
    internal static class ProductionChildDagRuntimeFailureTestHelpers
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Injects the configured physical runtime-boundary failure while a targeted child is checkpoint-blocked and
        /// captures the durable recovery/isolation proof before releasing the checkpoint.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="failure">The configured nested child failure.</param>
        /// <param name="crashCheckpointGate">The durable checkpoint embedded in the targeted child DAG.</param>
        /// <param name="parentPreChildCheckpointGate">Optional root-parent checkpoint used to make the original parent runtime admission-ineligible before C1 dispatch.</param>
        /// <param name="mcp">The MCP test client used for existing runtime queue control-plane operations.</param>
        /// <param name="relationStore">The authoritative parent-child relation store.</param>
        /// <param name="dagStore">The authoritative DAG execution store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="processControl">The physical runtime process-control authority.</param>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="tenantId">The owning tenant identifier.</param>
        /// <param name="submittedParentExecutionId">The originally submitted parent execution identifier.</param>
        /// <param name="submittedParentPipelineName">The originally submitted parent pipeline name.</param>
        /// <param name="submittedParentRuntimeInstanceId">The physical runtime initially assigned to the root parent.</param>
        /// <param name="submittedParentLocalRunId">The local runtime run initially assigned to the root parent.</param>
        /// <param name="childDepth">The total configured nested child depth.</param>
        /// <param name="timeout">The maximum duration allowed for the complete kill/recovery proof.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The physical runtime-boundary failure proof.</returns>
        public static async Task<ProductionChildDagRuntimeFailureResult> InjectRuntimeFailureAndObserveRecoveryAsync(
            ITestOutputHelper output,
            ProductionChildDagFailureInjectionDefinition failure,
            ProductionCrashCheckpointGate crashCheckpointGate,
            ProductionCrashCheckpointGate? parentPreChildCheckpointGate,
            McpTestClient mcp,
            IAiChildExecutionRelationStore relationStore,
            IAiDagExecutionStore dagStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiRuntimeHostProcessControl processControl,
            IAiRuntimeInstanceRegistry registry,
            string tenantId,
            string submittedParentExecutionId,
            string submittedParentPipelineName,
            string? submittedParentRuntimeInstanceId,
            string? submittedParentLocalRunId,
            int childDepth,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(failure);
            ArgumentNullException.ThrowIfNull(crashCheckpointGate);
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(relationStore);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(processControl);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(submittedParentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(submittedParentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);

            if (failure.TargetDepth <= 0 || failure.TargetDepth > childDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failure),
                    failure.TargetDepth,
                    $"Target child depth must be between 1 and '{childDepth}'.");
            }

            if (failure.Target == ProductionChildDagFailureTarget.ParentRuntimeAfterPark)
            {
                if (failure.TargetDepth != 1)
                {
                    throw new InvalidOperationException(
                        "The focused parked-parent runtime failure proof requires TargetDepth=1 so the targeted relation parent is the originally submitted root execution.");
                }

                if (parentPreChildCheckpointGate is null)
                {
                    throw new InvalidOperationException(
                        "The parked-parent runtime failure proof requires the root pre-child checkpoint used to make the original parent runtime admission-ineligible before C1 dispatch.");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(submittedParentRuntimeInstanceId);
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The Child DAG runtime failure timeout must be greater than zero.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var checkpointReleased = false;
            var parentPreChildCheckpointReleased = false;
            var parentRuntimeQueuePaused = false;
            var parentRuntimeKilled = false;

            try
            {
                if (failure.Target == ProductionChildDagFailureTarget.ParentRuntimeAfterPark)
                {
                    await parentPreChildCheckpointGate!
                        .WaitUntilReachedAsync(GetRemaining(deadline, "parent pre-child checkpoint reach"))
                        .ConfigureAwait(false);

                    var pauseResult = await mcp
                        .PauseRuntimeQueueAsync(
                            new AiRuntimeQueueControlPlaneRequest
                            {
                                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                                RuntimeInstanceId = submittedParentRuntimeInstanceId,
                                RequestedBy = "child-dag-parent-failure-proof",
                                Source = "integration-test",
                                Reason = "reserve-distinct-child-failure-boundary"
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    Assert.True(
                        pauseResult.Success,
                        pauseResult.FailureReason ?? pauseResult.Message);

                    await WaitForRuntimeQueuePausedAsync(
                            registry,
                            submittedParentRuntimeInstanceId!,
                            GetRemaining(deadline, "parent runtime paused registry observation"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    parentRuntimeQueuePaused = true;

                    output.WriteLine(
                        $"[CHILD DAG PARENT RUNTIME ADMISSION PAUSED] TenantId='{tenantId}', ParentExecutionId='{submittedParentExecutionId}', " +
                        $"ParentRuntimeInstanceId='{submittedParentRuntimeInstanceId}', RootCheckpointStepIndex='{parentPreChildCheckpointGate.Definition.StepIndex}'.");

                    await parentPreChildCheckpointGate
                        .ReleaseAsync()
                        .ConfigureAwait(false);
                    parentPreChildCheckpointReleased = true;
                }

                await crashCheckpointGate
                    .WaitUntilReachedAsync(GetRemaining(deadline, "child crash checkpoint reach"))
                    .ConfigureAwait(false);

                var relation = await ProductionChildDagScenarioHelpers
                    .WaitForWaitingRelationAtDepthAsync(
                        relationStore,
                        tenantId,
                        submittedParentExecutionId,
                        submittedParentPipelineName,
                        childDepth,
                        failure.TargetDepth,
                        GetRemaining(deadline, "waiting child relation"),
                        cancellationToken)
                    .ConfigureAwait(false);

                Assert.False(string.IsNullOrWhiteSpace(relation.ChildExecutionId));

                var parentStep = await WaitForParentExternalWaitAsync(
                        dagStore,
                        relation.ParentExecutionId,
                        GetRemaining(deadline, "parent WaitingForExternal"),
                        cancellationToken)
                    .ConfigureAwait(false);

                Assert.Equal(AiStepExecutionStatus.WaitingForExternal, parentStep.Status);
                Assert.Null(parentStep.ClaimedBy);
                Assert.Null(parentStep.ClaimToken);
                Assert.Null(parentStep.ClaimedAtUtc);
                Assert.Null(parentStep.LeaseExpiresAtUtc);

                var parentRecoverableEntries = await runExecutionIndex
                    .ListRecoverableAsync(cancellationToken)
                    .ConfigureAwait(false);

                var parentRuntimeCapacityReleased = !parentRecoverableEntries.Any(entry =>
                    string.Equals(
                        entry.ExecutionId,
                        relation.ParentExecutionId,
                        StringComparison.Ordinal));

                Assert.True(
                    parentRuntimeCapacityReleased,
                    $"Parent execution '{relation.ParentExecutionId}' remained recoverable/runtime-owned after durable external wait.");

                var childExecutionId = relation.ChildExecutionId!;
                var originalChildRun = await WaitForActiveChildRunAsync(
                        runExecutionIndex,
                        childExecutionId,
                        excludedRuntimeInstanceId: null,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

                var originalChildRuntimeInstanceId = originalChildRun.RuntimeInstanceId!;
                var originalChildRuntimeSnapshot =
                    await registry
                        .GetAsync(originalChildRuntimeInstanceId)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Active child runtime '{originalChildRuntimeInstanceId}' was not found in the shared registry before failure injection.");

                if (failure.Target == ProductionChildDagFailureTarget.ChildRuntime)
                {
                    output.WriteLine(
                        $"[CHILD DAG REAL RUNTIME KILL READY] TenantId='{tenantId}', TargetDepth='{failure.TargetDepth}', " +
                        $"ParentExecutionId='{relation.ParentExecutionId}', ChildExecutionId='{childExecutionId}', " +
                        $"RuntimeInstanceId='{originalChildRuntimeInstanceId}', HostId='{originalChildRuntimeSnapshot.HostId}', LocalRunId='{originalChildRun.RunId}', " +
                        $"CheckpointStepIndex='{failure.CrashCheckpointStepIndex}'.");

                    var killed = await processControl
                        .KillAsync(originalChildRuntimeInstanceId, cancellationToken)
                        .ConfigureAwait(false);

                    Assert.True(
                        killed,
                        $"Physical runtime '{originalChildRuntimeInstanceId}' owning child execution '{childExecutionId}' was not killed.");

                    await ProductionRecoveryWaitHelpers
                        .WaitForRuntimeInstanceUnsafeAsync(
                            registry,
                            originalChildRuntimeInstanceId,
                            GetRemaining(deadline, "failed runtime unsafe observation"))
                        .ConfigureAwait(false);

                    /*
                     * Keep the durable checkpoint unreleased while recovery converges. The replacement runtime will
                     * resume the same ChildExecutionId, reach the same already-reached checkpoint, and block again.
                     * This creates a deterministic observation window for proving physical runtime replacement.
                     */
                    var recoveredChildRun = await WaitForActiveChildRunAsync(
                            runExecutionIndex,
                            childExecutionId,
                            originalChildRuntimeInstanceId,
                            deadline,
                            cancellationToken)
                        .ConfigureAwait(false);

                    Assert.NotEqual(
                        originalChildRuntimeInstanceId,
                        recoveredChildRun.RuntimeInstanceId);
                    Assert.NotEqual(
                        originalChildRun.RunId,
                        recoveredChildRun.RunId);

                    var recoveredRuntimeSnapshot =
                        await registry
                            .GetAsync(recoveredChildRun.RuntimeInstanceId!)
                            .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"Recovered child runtime '{recoveredChildRun.RuntimeInstanceId}' was not found in the shared registry.");

                    output.WriteLine(
                        $"[CHILD DAG REAL RUNTIME RECOVERED] TenantId='{tenantId}', TargetDepth='{failure.TargetDepth}', " +
                        $"ParentExecutionId='{relation.ParentExecutionId}', ChildExecutionId='{childExecutionId}', " +
                        $"OriginalRuntimeInstanceId='{originalChildRuntimeInstanceId}', RecoveredRuntimeInstanceId='{recoveredChildRun.RuntimeInstanceId}', OriginalHostId='{originalChildRuntimeSnapshot.HostId}', RecoveredHostId='{recoveredRuntimeSnapshot.HostId}', " +
                        $"OriginalLocalRunId='{originalChildRun.RunId}', RecoveredLocalRunId='{recoveredChildRun.RunId}'.");

                    await crashCheckpointGate.ReleaseAsync().ConfigureAwait(false);
                    checkpointReleased = true;

                    return new ProductionChildDagRuntimeFailureResult
                    {
                        FailureTarget = failure.Target,
                        TargetDepth = failure.TargetDepth,
                        ParentExecutionId = relation.ParentExecutionId,
                        ChildExecutionId = childExecutionId,
                        OriginalRuntimeInstanceId = originalChildRuntimeInstanceId,
                        RecoveredRuntimeInstanceId = recoveredChildRun.RuntimeInstanceId!,
                        OriginalHostId = originalChildRuntimeSnapshot.HostId,
                        RecoveredHostId = recoveredRuntimeSnapshot.HostId,
                        OriginalLocalRunId = originalChildRun.RunId,
                        RecoveredLocalRunId = recoveredChildRun.RunId,
                        ParentWaitingForExternalObserved = true,
                        ParentRuntimeCapacityReleased = parentRuntimeCapacityReleased,
                        KillSucceeded = killed
                    };
                }

                if (failure.Target != ProductionChildDagFailureTarget.ParentRuntimeAfterPark)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(failure),
                        failure.Target,
                        "Unsupported Child DAG physical failure target.");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(submittedParentRuntimeInstanceId);
                ArgumentException.ThrowIfNullOrWhiteSpace(submittedParentLocalRunId);

                Assert.Equal(submittedParentExecutionId, relation.ParentExecutionId);
                Assert.NotEqual(
                    submittedParentRuntimeInstanceId,
                    originalChildRuntimeInstanceId);

                var originalParentRuntimeSnapshot =
                    await registry
                        .GetAsync(submittedParentRuntimeInstanceId)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Original parked-parent runtime '{submittedParentRuntimeInstanceId}' was not found in the shared registry before failure injection.");

                Assert.False(string.IsNullOrWhiteSpace(originalParentRuntimeSnapshot.HostId));
                Assert.False(string.IsNullOrWhiteSpace(originalChildRuntimeSnapshot.HostId));
                Assert.NotEqual(
                    originalParentRuntimeSnapshot.HostId,
                    originalChildRuntimeSnapshot.HostId);

                output.WriteLine(
                    $"[CHILD DAG PARENT RUNTIME KILL READY] TenantId='{tenantId}', TargetDepth='{failure.TargetDepth}', " +
                    $"ParentExecutionId='{relation.ParentExecutionId}', ChildExecutionId='{childExecutionId}', " +
                    $"ParentRuntimeInstanceId='{submittedParentRuntimeInstanceId}', ParentHostId='{originalParentRuntimeSnapshot.HostId}', ParentLocalRunId='{submittedParentLocalRunId}', " +
                    $"ChildRuntimeInstanceId='{originalChildRuntimeInstanceId}', ChildHostId='{originalChildRuntimeSnapshot.HostId}', ChildLocalRunId='{originalChildRun.RunId}', " +
                    $"CheckpointStepIndex='{failure.CrashCheckpointStepIndex}'.");

                var parentKilled = await processControl
                    .KillAsync(submittedParentRuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);
                parentRuntimeKilled = parentKilled;

                Assert.True(
                    parentKilled,
                    $"Physical runtime '{submittedParentRuntimeInstanceId}' that originally executed parent '{submittedParentExecutionId}' was not killed.");

                await ProductionRecoveryWaitHelpers
                    .WaitForRuntimeInstanceUnsafeAsync(
                        registry,
                        submittedParentRuntimeInstanceId,
                        GetRemaining(deadline, "parked parent runtime unsafe observation"))
                    .ConfigureAwait(false);

                /*
                 * Keep C1 at the same durable checkpoint while the parked parent boundary disappears. The child must
                 * remain on the exact same runtime/local-run incarnation; this proves the failure was isolated to the
                 * old parent capacity and did not recover/restart the child as a side effect.
                 */
                var childAfterParentKill = await WaitForActiveChildRunAsync(
                        runExecutionIndex,
                        childExecutionId,
                        excludedRuntimeInstanceId: null,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

                Assert.Equal(
                    originalChildRuntimeInstanceId,
                    childAfterParentKill.RuntimeInstanceId);
                Assert.Equal(
                    originalChildRun.RunId,
                    childAfterParentKill.RunId);

                var childAfterParentKillSnapshot =
                    await registry
                        .GetAsync(originalChildRuntimeInstanceId)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Child runtime '{originalChildRuntimeInstanceId}' disappeared after destruction of the parked parent boundary.");

                Assert.Equal(
                    originalChildRuntimeSnapshot.HostId,
                    childAfterParentKillSnapshot.HostId);

                output.WriteLine(
                    $"[CHILD DAG PARENT RUNTIME KILLED - CHILD STILL ACTIVE] TenantId='{tenantId}', " +
                    $"ParentExecutionId='{relation.ParentExecutionId}', DestroyedParentRuntimeInstanceId='{submittedParentRuntimeInstanceId}', DestroyedParentHostId='{originalParentRuntimeSnapshot.HostId}', " +
                    $"ChildExecutionId='{childExecutionId}', ChildRuntimeInstanceId='{originalChildRuntimeInstanceId}', ChildHostId='{originalChildRuntimeSnapshot.HostId}', ChildLocalRunId='{originalChildRun.RunId}'.");

                await crashCheckpointGate.ReleaseAsync().ConfigureAwait(false);
                checkpointReleased = true;

                return new ProductionChildDagRuntimeFailureResult
                {
                    FailureTarget = failure.Target,
                    TargetDepth = failure.TargetDepth,
                    ParentExecutionId = relation.ParentExecutionId,
                    ChildExecutionId = childExecutionId,
                    OriginalRuntimeInstanceId = submittedParentRuntimeInstanceId,
                    OriginalHostId = originalParentRuntimeSnapshot.HostId,
                    OriginalLocalRunId = submittedParentLocalRunId,
                    ObservedChildRuntimeInstanceId = originalChildRuntimeInstanceId,
                    ObservedChildHostId = originalChildRuntimeSnapshot.HostId,
                    ObservedChildLocalRunId = originalChildRun.RunId,
                    ParentWaitingForExternalObserved = true,
                    ParentRuntimeCapacityReleased = parentRuntimeCapacityReleased,
                    KillSucceeded = parentKilled
                };
            }
            finally
            {
                if (!checkpointReleased)
                {
                    try
                    {
                        await crashCheckpointGate
                            .ReleaseAsync()
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine(
                            $"[CHILD DAG REAL RUNTIME FAILURE CLEANUP] Crash checkpoint release failed. " +
                            $"ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                    }
                }

                if (parentPreChildCheckpointGate is not null &&
                    !parentPreChildCheckpointReleased)
                {
                    try
                    {
                        await parentPreChildCheckpointGate
                            .ReleaseAsync()
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine(
                            $"[CHILD DAG REAL RUNTIME FAILURE CLEANUP] Parent pre-child checkpoint release failed. " +
                            $"ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                    }
                }

                if (parentRuntimeQueuePaused &&
                    !parentRuntimeKilled &&
                    !string.IsNullOrWhiteSpace(submittedParentRuntimeInstanceId))
                {
                    try
                    {
                        await mcp
                            .ResumeRuntimeQueueAsync(
                                new AiRuntimeQueueControlPlaneRequest
                                {
                                    Operation = AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                                    RuntimeInstanceId = submittedParentRuntimeInstanceId,
                                    RequestedBy = "child-dag-parent-failure-proof",
                                    Source = "integration-test",
                                    Reason = "cleanup-parent-placement-reservation"
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine(
                            $"[CHILD DAG REAL RUNTIME FAILURE CLEANUP] Parent runtime queue resume failed. " +
                            $"RuntimeInstanceId='{submittedParentRuntimeInstanceId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                    }
                }
            }
        }

        private static async Task WaitForRuntimeQueuePausedAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            AiRuntimeInstanceSnapshot? lastSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastSnapshot = await registry
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

                if (lastSnapshot?.IsQueuePaused == true)
                {
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Runtime '{runtimeInstanceId}' did not become admission-ineligible through queue pause within '{timeout}'. " +
                $"LastStatus='{lastSnapshot?.Status}', LastCanAcceptRun='{lastSnapshot?.CanAcceptRun}', LastIsQueuePaused='{lastSnapshot?.IsQueuePaused}'.");
        }

        private static async Task<AiStepState> WaitForParentExternalWaitAsync(
            IAiDagExecutionStore dagStore,
            string executionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            AiStepState? lastStep = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = await dagStore
                    .GetStateAsync(executionId, cancellationToken)
                    .ConfigureAwait(false);

                if (state?.Steps.TryGetValue(
                        McpTestPipelineFactory.ChildDagStepName,
                        out var step) == true)
                {
                    lastStep = step;

                    if (step.Status == AiStepExecutionStatus.WaitingForExternal)
                    {
                        return step;
                    }
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Parent execution '{executionId}' did not park call-site '{McpTestPipelineFactory.ChildDagStepName}' " +
                $"in WaitingForExternal within '{timeout}'. LastStatus='{lastStep?.Status}'.");
        }

        private static async Task<AiRuntimeRunExecutionIndexEntry> WaitForActiveChildRunAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string childExecutionId,
            string? excludedRuntimeInstanceId,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            AiRuntimeRunExecutionIndexEntry? lastMatchingEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entries = await runExecutionIndex
                    .ListRecoverableAsync(cancellationToken)
                    .ConfigureAwait(false);

                var active = entries
                    .Where(entry =>
                        string.Equals(entry.ExecutionId, childExecutionId, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(entry.RuntimeInstanceId) &&
                        (string.Equals(entry.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(entry.Status, "running", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                lastMatchingEntry = active.LastOrDefault();

                var selected = active.FirstOrDefault(entry =>
                    string.IsNullOrWhiteSpace(excludedRuntimeInstanceId) ||
                    !string.Equals(
                        entry.RuntimeInstanceId,
                        excludedRuntimeInstanceId,
                        StringComparison.OrdinalIgnoreCase));

                if (selected is not null)
                {
                    return selected;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Child execution '{childExecutionId}' did not become active on the expected runtime capacity. " +
                $"ExcludedRuntimeInstanceId='{excludedRuntimeInstanceId ?? string.Empty}', " +
                $"LastRuntimeInstanceId='{lastMatchingEntry?.RuntimeInstanceId ?? string.Empty}', " +
                $"LastRunId='{lastMatchingEntry?.RunId ?? string.Empty}', LastStatus='{lastMatchingEntry?.Status ?? string.Empty}'.");
        }

        private static TimeSpan GetRemaining(
            DateTimeOffset deadline,
            string phase)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }

            throw new TimeoutException(
                $"Child DAG physical runtime failure proof timed out before phase '{phase}'.");
        }
    }
}
