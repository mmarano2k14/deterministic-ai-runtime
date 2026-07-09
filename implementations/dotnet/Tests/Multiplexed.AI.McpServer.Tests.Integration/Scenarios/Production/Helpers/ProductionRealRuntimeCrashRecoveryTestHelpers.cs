using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.Stores;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable helpers for real process-host runtime crash recovery inventory tests.
    /// </summary>
    public static class ProductionRealRuntimeCrashRecoveryTestHelpers
    {
        /// <summary>
        /// Submits real tenant-scoped runs, waits until they are assigned to runtime capacity,
        /// groups the assigned work by runtime instance, and returns the selected failed-runtime inventory.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="scaleOutRequestStore">The scale-out request store.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="pipelineNamePrefix">The pipeline name prefix.</param>
        /// <param name="requestedBy">The requested-by value.</param>
        /// <param name="source">The source value.</param>
        /// <param name="runCount">The number of real runs to submit for the tenant.</param>
        /// <param name="minimumInFlightExecutionCount">The minimum expected in-flight execution count on the selected failed runtime.</param>
        /// <param name="minimumLocalQueuedRunCount">The minimum expected local queued run count on the selected failed runtime.</param>
        /// <param name="minimumCompletedStepsBeforeKill">The minimum completed DAG step count required before killing the runtime.</param>
        /// <param name="scaleOutTimeout">The scale-out wait timeout.</param>
        /// <param name="dispatchTimeout">The dispatch wait timeout.</param>
        /// <param name="progressTimeout">The DAG progress wait timeout.</param>
        /// <returns>The real assigned work inventory selected for process crash.</returns>
        public static async Task<RealRuntimeCrashAssignedWorkInventoryProof> SubmitAndBuildAssignedWorkInventoryAsync(
            ITestOutputHelper output,
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            IAiSharedRunStore sharedRunStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineNamePrefix,
            string requestedBy,
            string source,
            int runCount,
            int minimumInFlightExecutionCount,
            int minimumLocalQueuedRunCount,
            int minimumCompletedStepsBeforeKill,
            TimeSpan scaleOutTimeout,
            TimeSpan dispatchTimeout,
            TimeSpan progressTimeout)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(scaleOutRequestStore);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineNamePrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);
            ArgumentOutOfRangeException.ThrowIfNegative(minimumInFlightExecutionCount);
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLocalQueuedRunCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedStepsBeforeKill);

            var dispatchedRuns = new List<(AiSharedRunRecord Run, string PipelineName)>();

            for (var index = 1; index <= runCount; index++)
            {
                var pipelineName = $"{pipelineNamePrefix}-run-{index:00}-{Guid.NewGuid():N}";
                AiSharedRunRecord dispatchedRun;

                if (index == 1)
                {
                    dispatchedRun =
                        await ProductionSharedRunTestHelpers
                            .SubmitAndDispatchOneRunAsync(
                                mcp,
                                scaleOutRequestStore,
                                tenant,
                                controlPlaneId,
                                pipelineName,
                                requestedBy,
                                source,
                                scaleOutTimeout,
                                dispatchTimeout)
                            .ConfigureAwait(false);
                }
                else
                {
                    var sharedRunId =
                        await ProductionSharedRunTestHelpers
                            .SubmitOneRunAsync(
                                mcp,
                                tenant,
                                controlPlaneId,
                                pipelineName,
                                requestedBy,
                                source)
                            .ConfigureAwait(false);

                    dispatchedRun =
                        await ProductionSharedRunTestHelpers
                            .WaitForSingleDispatchedRunAsync(
                                mcp,
                                pipelineName,
                                sharedRunId,
                                dispatchTimeout)
                            .ConfigureAwait(false);
                }

                Assert.False(string.IsNullOrWhiteSpace(dispatchedRun.SharedRunId));
                Assert.False(string.IsNullOrWhiteSpace(dispatchedRun.AssignedRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

                dispatchedRuns.Add((dispatchedRun, pipelineName));

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY] Run dispatched. TenantId='{tenant.TenantId}', SharedRunId='{dispatchedRun.SharedRunId}', RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}', PipelineName='{pipelineName}'.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(progressTimeout);
            RealRuntimeCrashAssignedWorkInventoryProof? selectedInventory = null;
            var lastInventorySummary = string.Empty;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var inventories = new List<RealRuntimeCrashAssignedWorkInventoryProof>();

                foreach (var group in dispatchedRuns.GroupBy(item => item.Run.AssignedRuntimeInstanceId!, StringComparer.Ordinal))
                {
                    var works = new List<RealRuntimeCrashWorkProof>();

                    foreach (var item in group)
                    {
                        var refreshedRun =
                            await sharedRunStore
                                .GetAsync(item.Run.SharedRunId)
                                .ConfigureAwait(false) ??
                            item.Run;

                        var localRunId =
                            refreshedRun.LocalRunId ??
                            item.Run.LocalRunId;

                        Assert.False(string.IsNullOrWhiteSpace(localRunId));

                        var indexEntry =
                            await runExecutionIndex
                                .GetAsync(localRunId!)
                                .ConfigureAwait(false);

                        var executionId =
                            refreshedRun.ExecutionId ??
                            indexEntry?.ExecutionId;

                        var kind =
                            string.IsNullOrWhiteSpace(executionId)
                                ? RealRuntimeCrashWorkKind.LocalQueued
                                : RealRuntimeCrashWorkKind.InFlightExecution;

                        works.Add(
                            new RealRuntimeCrashWorkProof
                            {
                                Kind = kind,
                                SharedRun = refreshedRun,
                                SharedRunId = refreshedRun.SharedRunId,
                                LocalRunId = localRunId!,
                                ExecutionId = executionId,
                                PipelineName = item.PipelineName
                            });
                    }

                    inventories.Add(
                        new RealRuntimeCrashAssignedWorkInventoryProof
                        {
                            Tenant = tenant,
                            Mcp = mcp,
                            RuntimeInstanceId = group.Key,
                            Works = works
                        });
                }

                selectedInventory =
                    inventories
                        .OrderByDescending(inventory => inventory.Works.Count)
                        .ThenByDescending(inventory => inventory.InFlightExecutions.Count)
                        .ThenByDescending(inventory => inventory.LocalQueuedRuns.Count)
                        .FirstOrDefault(inventory =>
                            inventory.InFlightExecutions.Count >= minimumInFlightExecutionCount &&
                            inventory.LocalQueuedRuns.Count >= minimumLocalQueuedRunCount);

                lastInventorySummary =
                    string.Join(
                        " | ",
                        inventories.Select(inventory =>
                            $"Runtime='{inventory.RuntimeInstanceId}', Total='{inventory.Works.Count}', InFlight='{inventory.InFlightExecutions.Count}', LocalQueued='{inventory.LocalQueuedRuns.Count}'"));

                if (selectedInventory is not null)
                {
                    foreach (var inFlight in selectedInventory.InFlightExecutions)
                    {
                        await ProductionRecoveryWaitHelpers
                            .WaitForDagCompletedStepCountAsync(
                                dagStore,
                                inFlight.ExecutionId!,
                                minimumCompletedStepsBeforeKill,
                                progressTimeout)
                            .ConfigureAwait(false);
                    }

                    WriteAssignedWorkInventory(output, selectedInventory);
                    return selectedInventory;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Could not build a real assigned work inventory matching the expected runtime shape before crash. " +
                $"TenantId='{tenant.TenantId}', ExpectedInFlight='{minimumInFlightExecutionCount}', ExpectedLocalQueued='{minimumLocalQueuedRunCount}', LastInventorySummary='{lastInventorySummary}'.");

            throw new InvalidOperationException("Unreachable assertion path.");
        }

        /// <summary>
        /// Kills the runtime process that owns a real assigned work inventory, waits for automatic recovery,
        /// and verifies strict resume semantics for all in-flight executions.
        /// </summary>
        public static async Task<RealRuntimeCrashFailedRuntimeRecoveryProof> KillRuntimeAndRecoverAssignedInventoryAsync(
            ITestOutputHelper output,
            IAiRuntimeHostProcessControl processControl,
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiSharedRunStore sharedRunStore,
            IAiDagExecutionStore dagStore,
            RealRuntimeCrashAssignedWorkInventoryProof inventory,
            TimeSpan unsafeTimeout,
            TimeSpan requeueTimeout,
            TimeSpan redispatchTimeout,
            TimeSpan executionResolveTimeout)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(processControl);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(inventory);

            var killed =
                await processControl
                    .KillAsync(inventory.RuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.True(
                killed,
                $"Runtime process was not killed. RuntimeInstanceId='{inventory.RuntimeInstanceId}'.");

            output.WriteLine(
                $"[REAL RUNTIME INVENTORY CRASH] Runtime process killed. TenantId='{inventory.Tenant.TenantId}', RuntimeInstanceId='{inventory.RuntimeInstanceId}', WorkCount='{inventory.Works.Count}', InFlight='{inventory.InFlightExecutions.Count}', LocalQueued='{inventory.LocalQueuedRuns.Count}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForRuntimeInstanceUnsafeAsync(
                    registry,
                    inventory.RuntimeInstanceId,
                    unsafeTimeout)
                .ConfigureAwait(false);

            output.WriteLine(
                $"[REAL RUNTIME INVENTORY CRASH] Runtime instance marked unsafe. TenantId='{inventory.Tenant.TenantId}', RuntimeInstanceId='{inventory.RuntimeInstanceId}'. Waiting for automatic execution recovery reconciliation.");

            foreach (var work in inventory.Works)
            {
                await WaitForWorkRequeuedForRecoveryAsync(
                        runExecutionIndex,
                        inventory,
                        work,
                        requeueTimeout)
                    .ConfigureAwait(false);
            }

            var recoveredWorks =
                new List<RealRuntimeCrashRecoveredWorkProof>();

            foreach (var work in inventory.Works)
            {
                var redispatchedRun =
                    await ProductionRecoveryWaitHelpers
                        .WaitForRecoveredRunRedispatchedAsync(
                            sharedRunStore,
                            work.SharedRunId,
                            inventory.RuntimeInstanceId,
                            work.LocalRunId,
                            redispatchTimeout)
                        .ConfigureAwait(false);

                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
                Assert.NotEqual(inventory.RuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
                Assert.NotEqual(work.LocalRunId, redispatchedRun.LocalRunId);
                AssertRuntimeBelongsToTenant(redispatchedRun.AssignedRuntimeInstanceId!, inventory.Tenant);

                string recoveredExecutionId;

                if (work.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                {
                    var recoveredExecution =
                        await ProductionRecoveryWaitHelpers
                            .WaitForDurableDagExecutionAsync(
                                sharedRunStore,
                                runExecutionIndex,
                                dagStore,
                                redispatchedRun.SharedRunId,
                                executionResolveTimeout)
                            .ConfigureAwait(false);

                    recoveredExecutionId =
                        recoveredExecution.ExecutionId;

                    Assert.False(string.IsNullOrWhiteSpace(work.ExecutionId));
                    Assert.Equal(work.ExecutionId, recoveredExecutionId);
                }
                else
                {
                    var replacementIndex =
                        await WaitForReplacementLocalQueuedRunIndexAsync(
                                runExecutionIndex,
                                redispatchedRun.LocalRunId!,
                                redispatchedRun.AssignedRuntimeInstanceId!,
                                executionResolveTimeout)
                            .ConfigureAwait(false);

                    recoveredExecutionId =
                        replacementIndex.ExecutionId ?? string.Empty;

                    Assert.True(
                        string.Equals(replacementIndex.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(replacementIndex.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(replacementIndex.Status, "completed", StringComparison.OrdinalIgnoreCase),
                        $"Recovered local queued run has an unexpected runtime index status. SharedRunId='{work.SharedRunId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}', Status='{replacementIndex.Status}'.");

                    output.WriteLine(
                        $"[REAL RUNTIME INVENTORY LOCAL QUEUED RECOVERY] Local queued work redispatched. TenantId='{inventory.Tenant.TenantId}', SharedRunId='{work.SharedRunId}', FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', FailedLocalRunId='{work.LocalRunId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}', ReplacementIndexStatus='{replacementIndex.Status}', ReplacementExecutionId='{replacementIndex.ExecutionId}'.");
                }

                recoveredWorks.Add(
                    new RealRuntimeCrashRecoveredWorkProof
                    {
                        Original = work,
                        RedispatchedRun = redispatchedRun,
                        ReplacementRuntimeInstanceId = redispatchedRun.AssignedRuntimeInstanceId!,
                        ReplacementLocalRunId = redispatchedRun.LocalRunId!,
                        RecoveredExecutionId = recoveredExecutionId
                    });

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY RECOVERY] Work recovered. TenantId='{inventory.Tenant.TenantId}', Kind='{work.Kind}', SharedRunId='{work.SharedRunId}', FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', FailedLocalRunId='{work.LocalRunId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}', ExecutionIdBefore='{work.ExecutionId}', ExecutionIdAfter='{recoveredExecutionId}'.");
            }

            var proof =
                new RealRuntimeCrashFailedRuntimeRecoveryProof
                {
                    FailedInventory = inventory,
                    RecoveredWorks = recoveredWorks
                };

            AssertRecoveredInventoryStrictResume(proof);
            WriteRecoveredInventory(output, proof);

            return proof;
        }

        /// <summary>
        /// Verifies that recovered in-flight DAG executions reached the expected completed step count,
        /// while volatile local queued work is only required to have been durably redispatched.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="proof">The failed runtime recovery proof.</param>
        /// <param name="expectedCompletedStepCount">The expected completed step count.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>A task that completes when all recovered in-flight DAG executions have reached the expected progress.</returns>
        public static async Task AssertRecoveredInventoryDagCompletedAsync(
            ITestOutputHelper output,
            IAiDagExecutionStore dagStore,
            RealRuntimeCrashFailedRuntimeRecoveryProof proof,
            int expectedCompletedStepCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(proof);
            ArgumentNullException.ThrowIfNull(proof.FailedInventory);
            ArgumentNullException.ThrowIfNull(proof.RecoveredWorks);

            var completedInFlightExecutionCount =
                0;

            var recoveredLocalQueuedCount =
                0;

            foreach (var recovered in proof.RecoveredWorks)
            {
                Assert.NotNull(recovered.Original);
                Assert.NotNull(recovered.RedispatchedRun);

                if (recovered.Original.Kind == RealRuntimeCrashWorkKind.LocalQueued)
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(recovered.ReplacementRuntimeInstanceId),
                        $"Recovered local queued work must have a replacement runtime instance id. SharedRunId='{recovered.Original.SharedRunId}'.");

                    Assert.False(
                        string.IsNullOrWhiteSpace(recovered.ReplacementLocalRunId),
                        $"Recovered local queued work must have a replacement local run id. SharedRunId='{recovered.Original.SharedRunId}'.");

                    Assert.NotEqual(
                        proof.FailedInventory.RuntimeInstanceId,
                        recovered.ReplacementRuntimeInstanceId);

                    Assert.NotEqual(
                        recovered.Original.LocalRunId,
                        recovered.ReplacementLocalRunId);

                    recoveredLocalQueuedCount++;

                    if (string.IsNullOrWhiteSpace(recovered.RecoveredExecutionId))
                    {
                        output.WriteLine(
                            $"[REAL RUNTIME INVENTORY COMPLETION] Local queued work recovered as replacement queued run. TenantId='{proof.FailedInventory.Tenant.TenantId}', SharedRunId='{recovered.Original.SharedRunId}', FailedLocalRunId='{recovered.Original.LocalRunId}', ReplacementRuntimeInstanceId='{recovered.ReplacementRuntimeInstanceId}', ReplacementLocalRunId='{recovered.ReplacementLocalRunId}'.");
                    }
                    else
                    {
                        await ProductionRecoveryWaitHelpers
                            .WaitForDagCompletedStepCountAsync(
                                dagStore,
                                recovered.RecoveredExecutionId,
                                expectedCompletedStepCount,
                                timeout)
                            .ConfigureAwait(false);

                        output.WriteLine(
                            $"[REAL RUNTIME INVENTORY COMPLETION] Recovered local queued DAG execution completed. TenantId='{proof.FailedInventory.Tenant.TenantId}', SharedRunId='{recovered.Original.SharedRunId}', ExecutionId='{recovered.RecoveredExecutionId}', CompletedSteps='{expectedCompletedStepCount}'.");
                    }

                    continue;
                }

                Assert.Equal(
                    RealRuntimeCrashWorkKind.InFlightExecution,
                    recovered.Original.Kind);

                Assert.False(
                    string.IsNullOrWhiteSpace(recovered.Original.ExecutionId),
                    $"Original in-flight work must have an execution id. SharedRunId='{recovered.Original.SharedRunId}'.");

                Assert.False(
                    string.IsNullOrWhiteSpace(recovered.RecoveredExecutionId),
                    $"Recovered in-flight work must expose the durable execution id. SharedRunId='{recovered.Original.SharedRunId}'.");

                Assert.Equal(
                    recovered.Original.ExecutionId,
                    recovered.RecoveredExecutionId);

                await ProductionRecoveryWaitHelpers
                    .WaitForDagCompletedStepCountAsync(
                        dagStore,
                        recovered.RecoveredExecutionId,
                        expectedCompletedStepCount,
                        timeout)
                    .ConfigureAwait(false);

                completedInFlightExecutionCount++;

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY COMPLETION] Recovered in-flight execution completed. TenantId='{proof.FailedInventory.Tenant.TenantId}', SharedRunId='{recovered.Original.SharedRunId}', ExecutionId='{recovered.RecoveredExecutionId}', CompletedSteps='{expectedCompletedStepCount}'.");
            }

            Assert.Equal(
                proof.FailedInventory.InFlightExecutions.Count,
                completedInFlightExecutionCount);

            Assert.Equal(
                proof.FailedInventory.LocalQueuedRuns.Count,
                recoveredLocalQueuedCount);
        }

        /// <summary>
        /// Verifies that recovered work preserves strict DAG resume semantics for in-flight executions
        /// and safe redispatch semantics for volatile local queued work.
        /// </summary>
        /// <param name="proof">The failed runtime recovery proof.</param>
        private static void AssertRecoveredInventoryStrictResume(
            RealRuntimeCrashFailedRuntimeRecoveryProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);
            ArgumentNullException.ThrowIfNull(proof.FailedInventory);
            ArgumentNullException.ThrowIfNull(proof.RecoveredWorks);

            Assert.Equal(
                proof.FailedInventory.Works.Count,
                proof.RecoveredWorks.Count);

            foreach (var recovered in proof.RecoveredWorks)
            {
                Assert.NotNull(recovered.Original);
                Assert.NotNull(recovered.RedispatchedRun);

                var original =
                    recovered.Original;

                Assert.False(
                    string.IsNullOrWhiteSpace(original.SharedRunId),
                    "Original work must have a shared run id.");

                Assert.False(
                    string.IsNullOrWhiteSpace(original.LocalRunId),
                    "Original work must have a failed local run id.");

                Assert.False(
                    string.IsNullOrWhiteSpace(recovered.ReplacementRuntimeInstanceId),
                    $"Recovered work must have a replacement runtime instance id. SharedRunId='{original.SharedRunId}'.");

                Assert.False(
                    string.IsNullOrWhiteSpace(recovered.ReplacementLocalRunId),
                    $"Recovered work must have a replacement local run id. SharedRunId='{original.SharedRunId}'.");

                Assert.NotEqual(
                    proof.FailedInventory.RuntimeInstanceId,
                    recovered.ReplacementRuntimeInstanceId);

                Assert.NotEqual(
                    original.LocalRunId,
                    recovered.ReplacementLocalRunId);

                Assert.Equal(
                    original.SharedRunId,
                    recovered.RedispatchedRun.SharedRunId);

                Assert.Equal(
                    recovered.ReplacementRuntimeInstanceId,
                    recovered.RedispatchedRun.AssignedRuntimeInstanceId);

                Assert.Equal(
                    recovered.ReplacementLocalRunId,
                    recovered.RedispatchedRun.LocalRunId);

                if (original.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(original.ExecutionId),
                        $"Original in-flight work must have an execution id. SharedRunId='{original.SharedRunId}'.");

                    Assert.False(
                        string.IsNullOrWhiteSpace(recovered.RecoveredExecutionId),
                        $"Recovered in-flight work must expose the durable execution id. SharedRunId='{original.SharedRunId}'.");

                    Assert.Equal(
                        original.ExecutionId,
                        recovered.RecoveredExecutionId);

                    Assert.Equal(
                        original.ExecutionId,
                        recovered.RedispatchedRun.ExecutionId);

                    continue;
                }

                Assert.Equal(
                    RealRuntimeCrashWorkKind.LocalQueued,
                    original.Kind);

                Assert.True(
                    string.IsNullOrWhiteSpace(original.ExecutionId),
                    $"Original local queued work must not already have a DAG execution id. SharedRunId='{original.SharedRunId}', ExecutionId='{original.ExecutionId}'.");

                Assert.True(
                    string.IsNullOrWhiteSpace(recovered.RecoveredExecutionId) ||
                    !string.Equals(
                        original.ExecutionId,
                        recovered.RecoveredExecutionId,
                        StringComparison.Ordinal),
                    $"Recovered local queued work must not be treated as strict DAG resume. SharedRunId='{original.SharedRunId}', OriginalExecutionId='{original.ExecutionId}', RecoveredExecutionId='{recovered.RecoveredExecutionId}'.");

                Assert.True(
                    string.IsNullOrWhiteSpace(recovered.RedispatchedRun.ExecutionId) ||
                    string.Equals(
                        recovered.RedispatchedRun.ExecutionId,
                        recovered.RecoveredExecutionId,
                        StringComparison.Ordinal),
                    $"Recovered local queued shared run has inconsistent execution ids. SharedRunId='{original.SharedRunId}', SharedRunExecutionId='{recovered.RedispatchedRun.ExecutionId}', ProofExecutionId='{recovered.RecoveredExecutionId}'.");
            }

            var recoveredSharedRunIds =
                proof.RecoveredWorks
                    .Select(item => item.Original.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            foreach (var original in proof.FailedInventory.Works)
            {
                Assert.Contains(
                    original.SharedRunId,
                    recoveredSharedRunIds);
            }

            var recoveredInFlightCount =
                proof.RecoveredWorks.Count(item => item.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution);

            var recoveredLocalQueuedCount =
                proof.RecoveredWorks.Count(item => item.Original.Kind == RealRuntimeCrashWorkKind.LocalQueued);

            Assert.Equal(
                proof.FailedInventory.InFlightExecutions.Count,
                recoveredInFlightCount);

            Assert.Equal(
                proof.FailedInventory.LocalQueuedRuns.Count,
                recoveredLocalQueuedCount);
        }

        /// <summary>
        /// Asserts that multiple failed-runtime recoveries did not leak work across tenant boundaries.
        /// </summary>
        public static void AssertNoCrossTenantInventoryRecoveryLeak(
            IReadOnlyCollection<RealRuntimeCrashFailedRuntimeRecoveryProof> proofs)
        {
            ArgumentNullException.ThrowIfNull(proofs);

            foreach (var proof in proofs)
            {
                foreach (var recoveredWork in proof.RecoveredWorks)
                {
                    AssertRuntimeBelongsToTenant(
                        recoveredWork.ReplacementRuntimeInstanceId,
                        proof.FailedInventory.Tenant);
                }
            }

            var allRecoveredSharedRunIds =
                proofs
                    .SelectMany(proof => proof.RecoveredWorks)
                    .Select(work => work.Original.SharedRunId)
                    .ToArray();

            var duplicateSharedRunGroups =
                allRecoveredSharedRunIds
                    .GroupBy(id => id, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .ToArray();

            Assert.True(
                duplicateSharedRunGroups.Length == 0,
                "Duplicate recovered shared run ids were detected across failed runtime recoveries. " +
                $"Duplicates='{string.Join(",", duplicateSharedRunGroups.Select(group => $"{group.Key}:{group.Count()}"))}'.");

            foreach (var left in proofs)
            {
                foreach (var right in proofs.Where(proof => !ReferenceEquals(proof, left)))
                {
                    var leftSharedRunIds =
                        left.RecoveredWorks
                            .Select(work => work.Original.SharedRunId)
                            .ToHashSet(StringComparer.Ordinal);

                    var rightSharedRunIds =
                        right.RecoveredWorks
                            .Select(work => work.Original.SharedRunId)
                            .ToHashSet(StringComparer.Ordinal);

                    Assert.Empty(leftSharedRunIds.Intersect(rightSharedRunIds, StringComparer.Ordinal));
                }
            }
        }

        /// <summary>
        /// Asserts that a safe tenant was not used by recovery and did not receive recovery contamination.
        /// </summary>
        public static void AssertSafeTenantUntouchedByRecovery(
            RealRuntimeCrashSafeTenantProof safeTenant,
            IReadOnlyCollection<RealRuntimeCrashFailedRuntimeRecoveryProof> failedRecoveries)
        {
            ArgumentNullException.ThrowIfNull(safeTenant);
            ArgumentNullException.ThrowIfNull(failedRecoveries);

            var safeSharedRunIds =
                safeTenant.Works
                    .Select(work => work.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            foreach (var recovery in failedRecoveries)
            {
                Assert.DoesNotContain(
                    recovery.RecoveredWorks,
                    recovered =>
                        string.Equals(recovered.ReplacementRuntimeInstanceId, safeTenant.RuntimeInstanceId, StringComparison.Ordinal) ||
                        safeSharedRunIds.Contains(recovered.Original.SharedRunId) ||
                        safeSharedRunIds.Contains(recovered.RedispatchedRun.SharedRunId));
            }
        }

        /// <summary>
        /// Asserts that a runtime instance id belongs to the expected tenant.
        /// </summary>
        public static void AssertRuntimeBelongsToTenant(
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(tenant);

            Assert.True(
                runtimeInstanceId.Contains(tenant.TenantId, StringComparison.Ordinal) ||
                runtimeInstanceId.Contains(tenant.RuntimeInstanceIdPrefix, StringComparison.Ordinal),
                $"Runtime instance does not appear to belong to the expected tenant. RuntimeInstanceId='{runtimeInstanceId}', TenantId='{tenant.TenantId}', RuntimeInstanceIdPrefix='{tenant.RuntimeInstanceIdPrefix}'.");
        }

        /// <summary>
        /// Waits until a replacement local queued runtime run is visible in the runtime run execution index.
        /// </summary>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="replacementLocalRunId">The replacement local runtime run identifier.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance identifier.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The replacement runtime run execution index entry.</returns>
        private static async Task<AiRuntimeRunExecutionIndexEntry> WaitForReplacementLocalQueuedRunIndexAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string replacementLocalRunId,
            string replacementRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentException.ThrowIfNullOrWhiteSpace(replacementLocalRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(replacementRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRunExecutionIndexEntry? lastEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(replacementLocalRunId)
                        .ConfigureAwait(false);

                if (lastEntry is not null &&
                    string.Equals(lastEntry.RuntimeInstanceId, replacementRuntimeInstanceId, StringComparison.Ordinal))
                {
                    return lastEntry;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Replacement local queued runtime run was not visible in the runtime run execution index within the timeout. ReplacementRuntimeInstanceId='{replacementRuntimeInstanceId}', ReplacementLocalRunId='{replacementLocalRunId}', LastIndexRuntimeInstanceId='{lastEntry?.RuntimeInstanceId}', LastIndexStatus='{lastEntry?.Status}', LastIndexExecutionId='{lastEntry?.ExecutionId}'.");

            throw new InvalidOperationException(
                "Unreachable because Assert.Fail throws.");
        }

        private static async Task WaitForWorkRequeuedForRecoveryAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            RealRuntimeCrashAssignedWorkInventoryProof inventory,
            RealRuntimeCrashWorkProof work,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRunExecutionIndexEntry? lastEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(work.LocalRunId)
                        .ConfigureAwait(false);

                var runtimeMatches =
                    string.Equals(lastEntry?.RuntimeInstanceId, inventory.RuntimeInstanceId, StringComparison.Ordinal);

                var executionMatches =
                    string.IsNullOrWhiteSpace(work.ExecutionId) ||
                    string.Equals(lastEntry?.ExecutionId, work.ExecutionId, StringComparison.Ordinal);

                var statusMatches =
                    work.Kind == RealRuntimeCrashWorkKind.InFlightExecution
                        ? string.Equals(lastEntry?.Status, "requeued-for-recovery", StringComparison.OrdinalIgnoreCase)
                        : string.Equals(lastEntry?.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(lastEntry?.Status, "requeued-for-recovery", StringComparison.OrdinalIgnoreCase);

                if (statusMatches && runtimeMatches && executionMatches)
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Runtime work was not observed in a recoverable pre-redispatch state within the timeout. " +
                $"TenantId='{inventory.Tenant.TenantId}', FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', SharedRunId='{work.SharedRunId}', LocalRunId='{work.LocalRunId}', Kind='{work.Kind}', ExpectedExecutionId='{work.ExecutionId}', LastStatus='{lastEntry?.Status}', LastRuntimeInstanceId='{lastEntry?.RuntimeInstanceId}', LastExecutionId='{lastEntry?.ExecutionId}'.");

            throw new InvalidOperationException("Unreachable assertion path.");
        }

        private static void WriteAssignedWorkInventory(
            ITestOutputHelper output,
            RealRuntimeCrashAssignedWorkInventoryProof inventory)
        {
            output.WriteLine("[REAL RUNTIME ASSIGNED WORK INVENTORY]");
            output.WriteLine($"TenantId='{inventory.Tenant.TenantId}'");
            output.WriteLine($"RuntimeInstanceId='{inventory.RuntimeInstanceId}'");
            output.WriteLine($"TotalWorkCount='{inventory.Works.Count}'");
            output.WriteLine($"InFlightExecutionCount='{inventory.InFlightExecutions.Count}'");
            output.WriteLine($"LocalQueuedRunCount='{inventory.LocalQueuedRuns.Count}'");

            var index = 1;

            foreach (var work in inventory.Works)
            {
                output.WriteLine(
                    $"{index:00}. Kind='{work.Kind}', SharedRunId='{work.SharedRunId}', LocalRunId='{work.LocalRunId}', ExecutionId='{work.ExecutionId}', PipelineName='{work.PipelineName}'.");

                index++;
            }
        }

        private static void WriteRecoveredInventory(
            ITestOutputHelper output,
            RealRuntimeCrashFailedRuntimeRecoveryProof proof)
        {
            output.WriteLine("[REAL RUNTIME RECOVERED WORK INVENTORY]");
            output.WriteLine($"TenantId='{proof.FailedInventory.Tenant.TenantId}'");
            output.WriteLine($"FailedRuntimeInstanceId='{proof.FailedInventory.RuntimeInstanceId}'");
            output.WriteLine($"RecoveredWorkCount='{proof.RecoveredWorks.Count}'");

            var index = 1;

            foreach (var recoveredWork in proof.RecoveredWorks)
            {
                output.WriteLine(
                    $"{index:00}. Kind='{recoveredWork.Original.Kind}', SharedRunId='{recoveredWork.Original.SharedRunId}', FailedLocalRunId='{recoveredWork.Original.LocalRunId}', ReplacementRuntimeInstanceId='{recoveredWork.ReplacementRuntimeInstanceId}', ReplacementLocalRunId='{recoveredWork.ReplacementLocalRunId}', ExecutionIdBefore='{recoveredWork.Original.ExecutionId}', ExecutionIdAfter='{recoveredWork.RecoveredExecutionId}'.");

                index++;
            }
        }

        /// <summary>
        /// Waits for runtime recovery forensics records matching a recovered real-runtime crash inventory,
        /// verifies tenant ownership, work ownership, timeline evidence, and writes the recovered forensics inventory.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="queryService">The runtime recovery forensics query service.</param>
        /// <param name="proof">The failed runtime recovery proof.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The matching runtime recovery forensics records.</returns>
        public static async Task<IReadOnlyList<AiRuntimeRecoveryForensicsReadModel>> AssertRecoveredInventoryForensicsAsync(
            ITestOutputHelper output,
            IAiRuntimeRecoveryForensicsQueryService queryService,
            RealRuntimeCrashFailedRuntimeRecoveryProof proof,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(queryService);
            ArgumentNullException.ThrowIfNull(proof);
            ArgumentNullException.ThrowIfNull(proof.FailedInventory);
            ArgumentNullException.ThrowIfNull(proof.RecoveredWorks);

            var expectedSharedRunIds =
                proof.RecoveredWorks
                    .Select(work => work.Original.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> records =
                Array.Empty<AiRuntimeRecoveryForensicsReadModel>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var result =
                    await queryService
                        .SearchAsync(
                            new AiRuntimeRecoveryForensicsQuery
                            {
                                RuntimeInstanceId = proof.FailedInventory.RuntimeInstanceId,
                                TenantId = proof.FailedInventory.Tenant.TenantId,
                                Limit = 200
                            })
                        .ConfigureAwait(false);

                records =
                    result.Items
                        .Where(record => !string.IsNullOrWhiteSpace(record.SharedRunId))
                        .Where(record => expectedSharedRunIds.Contains(record.SharedRunId!))
                        .ToArray();

                if (records.Count == expectedSharedRunIds.Count &&
                    RecordsContainExpectedRecoveryTimeline(records, proof))
                {
                    break;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Equal(
                expectedSharedRunIds.Count,
                records.Count);

            Assert.True(
                RecordsContainExpectedRecoveryTimeline(records, proof),
                "Recovery forensics records were found, but their timelines did not contain the expected recovery events. " +
                $"TenantId='{proof.FailedInventory.Tenant.TenantId}', FailedRuntimeInstanceId='{proof.FailedInventory.RuntimeInstanceId}', " +
                $"ExpectedSharedRunIds='{string.Join(",", expectedSharedRunIds)}', " +
                $"ActualTimelines='{string.Join(" || ", records.Select(record => $"{record.SharedRunId}:{string.Join("->", record.Timeline.Select(item => item.EventType))}"))}'.");

            foreach (var recovered in proof.RecoveredWorks)
            {
                Assert.Contains(
                    records,
                    record => string.Equals(
                        record.SharedRunId,
                        recovered.Original.SharedRunId,
                        StringComparison.Ordinal));
            }

            foreach (var record in records)
            {
                Assert.Equal(
                    proof.FailedInventory.Tenant.TenantId,
                    record.TenantId);

                Assert.Contains(
                    proof.RecoveredWorks,
                    recovered => string.Equals(
                        recovered.Original.SharedRunId,
                        record.SharedRunId,
                        StringComparison.Ordinal));

                Assert.False(
                    string.IsNullOrWhiteSpace(record.ForensicsId),
                    $"Recovery forensics id must not be empty. TenantId='{proof.FailedInventory.Tenant.TenantId}', SharedRunId='{record.SharedRunId}'.");

                Assert.False(
                    string.IsNullOrWhiteSpace(record.SharedRunId),
                    $"Recovery forensics shared run id must not be empty. TenantId='{proof.FailedInventory.Tenant.TenantId}', ForensicsId='{record.ForensicsId}'.");

                Assert.NotEmpty(
                    record.Timeline);

                AssertContainsTimelineEvent(
                    record,
                    "failed.local.run.marked.requeued.for.recovery");

                AssertContainsTimelineEvent(
                    record,
                    "replacement.runtime.selected");

                AssertContainsTimelineEvent(
                    record,
                    "replacement.local.run.registered");

                AssertContainsTimelineEvent(
                    record,
                    "resume.context.seeded");

                var recovered =
                    proof.RecoveredWorks.Single(work =>
                        string.Equals(
                            work.Original.SharedRunId,
                            record.SharedRunId,
                            StringComparison.Ordinal));

                if (recovered.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                {
                    AssertContainsTimelineEvent(
                        record,
                        "execution.recovery.candidate.detected");

                    AssertContainsTimelineEvent(
                        record,
                        "shared.run.requeued.for.resume");

                    Assert.Equal(
                        recovered.Original.ExecutionId,
                        record.ExecutionId);
                }
                else
                {
                    Assert.Equal(
                        RealRuntimeCrashWorkKind.LocalQueued,
                        recovered.Original.Kind);

                    Assert.True(
                        ContainsTimelineEvent(record, "SharedRunRequeuedForLocalQueuedRecovery") ||
                        ContainsTimelineEvent(record, "shared.run.requeued.for.local.queued.recovery"),
                        $"Local queued recovery forensics record does not contain a local queued requeue event. SharedRunId='{record.SharedRunId}', Timeline='{string.Join(" -> ", record.Timeline.Select(item => item.EventType))}'.");

                    Assert.True(
                        string.IsNullOrWhiteSpace(record.ExecutionId) ||
                        string.Equals(
                            record.ExecutionId,
                            recovered.RecoveredExecutionId,
                            StringComparison.Ordinal),
                        $"Recovered local queued forensics execution id mismatch. SharedRunId='{record.SharedRunId}', ForensicsExecutionId='{record.ExecutionId}', RecoveredExecutionId='{recovered.RecoveredExecutionId}'.");
                }
            }

            WriteRuntimeRecoveryInventoryForensics(
                output,
                proof.FailedInventory.RuntimeInstanceId,
                records);

            return records;
        }

        /// <summary>
        /// Verifies that recovery forensics records do not leak between two recovered tenant inventories.
        /// </summary>
        /// <param name="tenantAProof">The tenant A recovery proof.</param>
        /// <param name="tenantAForensics">The tenant A recovery forensics records.</param>
        /// <param name="tenantBProof">The tenant B recovery proof.</param>
        /// <param name="tenantBForensics">The tenant B recovery forensics records.</param>
        public static void AssertNoCrossTenantRecoveryForensicsLeak(
            RealRuntimeCrashFailedRuntimeRecoveryProof tenantAProof,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> tenantAForensics,
            RealRuntimeCrashFailedRuntimeRecoveryProof tenantBProof,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> tenantBForensics)
        {
            ArgumentNullException.ThrowIfNull(tenantAProof);
            ArgumentNullException.ThrowIfNull(tenantAProof.FailedInventory);
            ArgumentNullException.ThrowIfNull(tenantAProof.RecoveredWorks);
            ArgumentNullException.ThrowIfNull(tenantAForensics);
            ArgumentNullException.ThrowIfNull(tenantBProof);
            ArgumentNullException.ThrowIfNull(tenantBProof.FailedInventory);
            ArgumentNullException.ThrowIfNull(tenantBProof.RecoveredWorks);
            ArgumentNullException.ThrowIfNull(tenantBForensics);

            var tenantASharedRunIds =
                tenantAProof.RecoveredWorks
                    .Select(work => work.Original.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            var tenantBSharedRunIds =
                tenantBProof.RecoveredWorks
                    .Select(work => work.Original.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            foreach (var record in tenantAForensics)
            {
                Assert.Equal(
                    tenantAProof.FailedInventory.Tenant.TenantId,
                    record.TenantId);

                Assert.Contains(
                    record.SharedRunId,
                    tenantASharedRunIds);

                Assert.DoesNotContain(
                    record.SharedRunId,
                    tenantBSharedRunIds);
            }

            foreach (var record in tenantBForensics)
            {
                Assert.Equal(
                    tenantBProof.FailedInventory.Tenant.TenantId,
                    record.TenantId);

                Assert.Contains(
                    record.SharedRunId,
                    tenantBSharedRunIds);

                Assert.DoesNotContain(
                    record.SharedRunId,
                    tenantASharedRunIds);
            }
        }

        /// <summary>
        /// Verifies that recovery forensics records do not contain duplicate recovery evidence.
        /// </summary>
        /// <param name="records">The recovery forensics records.</param>
        public static void AssertNoDuplicateRecoveryForensics(
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> records)
        {
            ArgumentNullException.ThrowIfNull(records);

            var duplicateForensicsIds =
                records
                    .Where(record => !string.IsNullOrWhiteSpace(record.ForensicsId))
                    .GroupBy(
                        record => record.ForensicsId,
                        StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => $"{group.Key}:{group.Count()}")
                    .ToArray();

            Assert.True(
                duplicateForensicsIds.Length == 0,
                $"Duplicate recovery forensics ids were detected. Duplicates='{string.Join(",", duplicateForensicsIds)}'.");

            var duplicateRecoveryKeys =
                records
                    .Where(record => !string.IsNullOrWhiteSpace(record.TenantId) && !string.IsNullOrWhiteSpace(record.SharedRunId))
                    .GroupBy(
                        record => $"{record.TenantId}|{record.SharedRunId}",
                        StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => $"{group.Key}:{group.Count()}")
                    .ToArray();

            Assert.True(
                duplicateRecoveryKeys.Length == 0,
                $"Duplicate recovery forensics records were detected for the same tenant/shared-run. Duplicates='{string.Join(",", duplicateRecoveryKeys)}'.");
        }

        /// <summary>
        /// Verifies whether all forensics records contain the expected recovery timeline events.
        /// </summary>
        /// <param name="records">The forensics records.</param>
        /// <param name="proof">The recovered inventory proof.</param>
        /// <returns>True when all expected timeline events are present.</returns>
        private static bool RecordsContainExpectedRecoveryTimeline(
            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> records,
            RealRuntimeCrashFailedRuntimeRecoveryProof proof)
        {
            foreach (var recovered in proof.RecoveredWorks)
            {
                var record =
                    records.FirstOrDefault(item =>
                        string.Equals(
                            item.SharedRunId,
                            recovered.Original.SharedRunId,
                            StringComparison.Ordinal));

                if (record is null)
                {
                    return false;
                }

                if (!ContainsTimelineEvent(record, "failed.local.run.marked.requeued.for.recovery") ||
                    !ContainsTimelineEvent(record, "replacement.runtime.selected") ||
                    !ContainsTimelineEvent(record, "replacement.local.run.registered") ||
                    !ContainsTimelineEvent(record, "resume.context.seeded"))
                {
                    return false;
                }

                if (recovered.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                {
                    if (!ContainsTimelineEvent(record, "execution.recovery.candidate.detected") ||
                        !ContainsTimelineEvent(record, "shared.run.requeued.for.resume"))
                    {
                        return false;
                    }
                }
                else if (!ContainsTimelineEvent(record, "SharedRunRequeuedForLocalQueuedRecovery") &&
                         !ContainsTimelineEvent(record, "shared.run.requeued.for.local.queued.recovery"))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Asserts that a forensics record contains a timeline event.
        /// </summary>
        /// <param name="record">The forensics record.</param>
        /// <param name="eventType">The expected event type.</param>
        private static void AssertContainsTimelineEvent(
            AiRuntimeRecoveryForensicsReadModel record,
            string eventType)
        {
            Assert.True(
                ContainsTimelineEvent(record, eventType),
                $"Recovery forensics timeline does not contain expected event. SharedRunId='{record.SharedRunId}', ExpectedEventType='{eventType}', Timeline='{string.Join(" -> ", record.Timeline.Select(item => item.EventType))}'.");
        }

        /// <summary>
        /// Returns whether a forensics record contains a timeline event.
        /// </summary>
        /// <param name="record">The forensics record.</param>
        /// <param name="eventType">The expected event type.</param>
        /// <returns>True when the timeline contains the event.</returns>
        private static bool ContainsTimelineEvent(
            AiRuntimeRecoveryForensicsReadModel record,
            string eventType)
        {
            return record.Timeline.Any(item =>
                string.Equals(
                    item.EventType,
                    eventType,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Writes the runtime recovery forensics inventory linked to a failed runtime instance.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="records">The runtime recovery forensics records.</param>
        private static void WriteRuntimeRecoveryInventoryForensics(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> records)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(records);

            output.WriteLine("[RUNTIME RECOVERY INVENTORY FORENSICS]");
            output.WriteLine($"FailedRuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"ForensicsRecordCount='{records.Count}'");

            var index =
                1;

            foreach (var record in records)
            {
                output.WriteLine(
                    $"{index:00}. " +
                    $"ForensicsId='{record.ForensicsId}', " +
                    $"ExecutionId='{record.ExecutionId}', " +
                    $"SharedRunId='{record.SharedRunId}', " +
                    $"TenantId='{record.TenantId}', " +
                    $"Timeline='{string.Join(" -> ", record.Timeline.Select(item => item.EventType))}'.");

                index++;
            }
        }
    }
}