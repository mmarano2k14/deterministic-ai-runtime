using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
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
        /// <param name="sharedQueue">The durable shared queue used only for timeout diagnostics.</param>
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
        /// <param name="observationMode">The production recovery observation mode.</param>
        /// <param name="crashCheckpointGate">The optional test-only durable crash checkpoint gate.</param>
        /// <param name="remainingRunPlacementFactory">
        /// Optional factory that creates the placement directive for runs submitted after the first runtime assignment.
        /// Historical scenarios leave this unset and preserve the existing admission behavior.
        /// </param>
        /// <returns>The real assigned work inventory selected for process crash.</returns>
        public static async Task<RealRuntimeCrashAssignedWorkInventoryProof> SubmitAndBuildAssignedWorkInventoryAsync(
            ITestOutputHelper output,
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
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
            TimeSpan progressTimeout,
            ProductionRecoveryObservationMode observationMode =
                ProductionRecoveryObservationMode.Polling,
            ProductionCrashCheckpointGate? crashCheckpointGate = null,
            Func<string, AiRunPlacementDirective?>? remainingRunPlacementFactory = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(scaleOutRequestStore);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
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

            if (observationMode != ProductionRecoveryObservationMode.Polling &&
                observationMode != ProductionRecoveryObservationMode.HybridSignals)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(observationMode),
                    observationMode,
                    "The production recovery observation mode is not supported.");
            }

            var trackedRuns =
                new List<(
                    string SharedRunId,
                    string PipelineName,
                    AiSharedRunRecord? InitialRun)>();

            var firstPipelineName =
                $"{pipelineNamePrefix}-run-01-{Guid.NewGuid():N}";

            var firstDispatchedRun =
                await ProductionSharedRunTestHelpers
                    .SubmitAndDispatchOneRunAsync(
                        mcp,
                        scaleOutRequestStore,
                        tenant,
                        controlPlaneId,
                        firstPipelineName,
                        requestedBy,
                        source,
                        scaleOutTimeout,
                        dispatchTimeout,
                        crashCheckpointGate?.Definition)
                    .ConfigureAwait(false);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    firstDispatchedRun.SharedRunId));

            Assert.False(
                string.IsNullOrWhiteSpace(
                    firstDispatchedRun.AssignedRuntimeInstanceId));

            Assert.False(
                string.IsNullOrWhiteSpace(
                    firstDispatchedRun.LocalRunId));

            trackedRuns.Add(
                (
                    firstDispatchedRun.SharedRunId,
                    firstPipelineName,
                    firstDispatchedRun));

            output.WriteLine(
                $"[REAL RUNTIME INVENTORY] Run dispatched. TenantId='{tenant.TenantId}', SharedRunId='{firstDispatchedRun.SharedRunId}', RuntimeInstanceId='{firstDispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{firstDispatchedRun.LocalRunId}', PipelineName='{firstPipelineName}'.");

            if (crashCheckpointGate is not null)
            {
                await crashCheckpointGate
                    .WaitUntilReachedAsync(progressTimeout)
                    .ConfigureAwait(false);

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY CRASH GATE CONFIRMED] TenantId='{tenant.TenantId}', SharedRunId='{firstDispatchedRun.SharedRunId}', RuntimeInstanceId='{firstDispatchedRun.AssignedRuntimeInstanceId}', PipelineName='{firstPipelineName}', CheckpointStepIndex='{crashCheckpointGate.Definition.StepIndex}'. Submitting local queued work only after durable gate reach.");
            }

            var remainingRunPlacement =
                remainingRunPlacementFactory?.Invoke(
                    firstDispatchedRun.AssignedRuntimeInstanceId!);

            /*
             * Submit every remaining run before waiting for any individual
             * dispatch. The first execution must stay active long enough for
             * the later runs to occupy the same runtime local queue.
             */
            for (var index = 2; index <= runCount; index++)
            {
                var pipelineName =
                    $"{pipelineNamePrefix}-run-{index:00}-{Guid.NewGuid():N}";

                var sharedRunId =
                    await ProductionSharedRunTestHelpers
                        .SubmitOneRunAsync(
                            mcp,
                            tenant,
                            controlPlaneId,
                            pipelineName,
                            requestedBy,
                            source,
                            placement: remainingRunPlacement)
                        .ConfigureAwait(false);

                trackedRuns.Add(
                    (
                        sharedRunId,
                        pipelineName,
                        null));

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY] Run submitted before combined inventory wait. TenantId='{tenant.TenantId}', SharedRunId='{sharedRunId}', PipelineName='{pipelineName}', PlacementRuntimeInstanceId='{remainingRunPlacement?.Target.RuntimeInstanceId}', PlacementRequirement='{remainingRunPlacement?.Requirement}', PlacementFallback='{remainingRunPlacement?.Fallback}'.");
            }

            /*
             * Resolve dispatch and assigned-work state through one bounded
             * durable loop. This avoids both failure modes observed under P20:
             *
             *  - independent concurrent dispatch pollers overload Redis;
             *  - sequential dispatch waits allow the first DAG to complete
             *    before the crash inventory can be captured.
             */
            var inventoryTimeout =
                dispatchTimeout > progressTimeout
                    ? dispatchTimeout
                    : progressTimeout;

            var deadline =
                DateTimeOffset.UtcNow.Add(inventoryTimeout);

            var loggedDispatchedRunIds =
                new HashSet<string>(StringComparer.Ordinal)
                {
                    firstDispatchedRun.SharedRunId
                };

            RealRuntimeCrashAssignedWorkInventoryProof? selectedInventory =
                null;

            var lastInventorySummary =
                string.Empty;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var worksByRuntime =
                    new Dictionary<
                        string,
                        List<RealRuntimeCrashWorkProof>>(
                            StringComparer.Ordinal);

                var unresolvedRuns =
                    new List<string>();

                foreach (var trackedRun in trackedRuns)
                {
                    var refreshedRun =
                        await sharedRunStore
                            .GetAsync(trackedRun.SharedRunId)
                            .ConfigureAwait(false) ??
                        trackedRun.InitialRun;

                    if (refreshedRun is null ||
                        string.IsNullOrWhiteSpace(
                            refreshedRun.AssignedRuntimeInstanceId) ||
                        string.IsNullOrWhiteSpace(
                            refreshedRun.LocalRunId))
                    {
                        unresolvedRuns.Add(
                            $"{trackedRun.SharedRunId}:not-dispatched");

                        continue;
                    }

                    if (loggedDispatchedRunIds.Add(
                            refreshedRun.SharedRunId))
                    {
                        output.WriteLine(
                            $"[REAL RUNTIME INVENTORY] Run dispatch observed by combined inventory wait. TenantId='{tenant.TenantId}', SharedRunId='{refreshedRun.SharedRunId}', RuntimeInstanceId='{refreshedRun.AssignedRuntimeInstanceId}', LocalRunId='{refreshedRun.LocalRunId}', PipelineName='{trackedRun.PipelineName}'.");
                    }

                    var indexEntry =
                        await runExecutionIndex
                            .GetAsync(refreshedRun.LocalRunId)
                            .ConfigureAwait(false);

                    if (indexEntry is null)
                    {
                        unresolvedRuns.Add(
                            $"{trackedRun.SharedRunId}:index-missing");

                        continue;
                    }

                    var executionId =
                        refreshedRun.ExecutionId ??
                        indexEntry.ExecutionId;

                    RealRuntimeCrashWorkKind? kind =
                        null;

                    if (string.Equals(
                            indexEntry.Status,
                            "running",
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(executionId))
                    {
                        kind =
                            RealRuntimeCrashWorkKind.InFlightExecution;
                    }
                    else if (string.Equals(
                                 indexEntry.Status,
                                 "queued",
                                 StringComparison.OrdinalIgnoreCase) &&
                             string.IsNullOrWhiteSpace(executionId))
                    {
                        kind =
                            RealRuntimeCrashWorkKind.LocalQueued;
                    }

                    if (kind is null)
                    {
                        unresolvedRuns.Add(
                            $"{trackedRun.SharedRunId}:status={indexEntry.Status}");

                        continue;
                    }

                    if (!worksByRuntime.TryGetValue(
                            refreshedRun.AssignedRuntimeInstanceId,
                            out var works))
                    {
                        works =
                            new List<RealRuntimeCrashWorkProof>();

                        worksByRuntime.Add(
                            refreshedRun.AssignedRuntimeInstanceId,
                            works);
                    }

                    works.Add(
                        new RealRuntimeCrashWorkProof
                        {
                            Kind = kind.Value,
                            SharedRun = refreshedRun,
                            SharedRunId = refreshedRun.SharedRunId,
                            LocalRunId = refreshedRun.LocalRunId,
                            ExecutionId = executionId,
                            PipelineName = trackedRun.PipelineName
                        });
                }

                var inventories =
                    worksByRuntime
                        .Select(pair =>
                            new RealRuntimeCrashAssignedWorkInventoryProof
                            {
                                Tenant = tenant,
                                Mcp = mcp,
                                RuntimeInstanceId = pair.Key,
                                Works = pair.Value
                            })
                        .ToArray();

                selectedInventory =
                    inventories
                        .OrderByDescending(inventory =>
                            inventory.Works.Count)
                        .ThenByDescending(inventory =>
                            inventory.InFlightExecutions.Count)
                        .ThenByDescending(inventory =>
                            inventory.LocalQueuedRuns.Count)
                        .FirstOrDefault(inventory =>
                            inventory.Works.Count == runCount &&
                            inventory.InFlightExecutions.Count ==
                            minimumInFlightExecutionCount &&
                            inventory.LocalQueuedRuns.Count >=
                            minimumLocalQueuedRunCount);

                lastInventorySummary =
                    string.Join(
                        " | ",
                        inventories.Select(inventory =>
                            $"Runtime='{inventory.RuntimeInstanceId}', Total='{inventory.Works.Count}', InFlight='{inventory.InFlightExecutions.Count}', LocalQueued='{inventory.LocalQueuedRuns.Count}'"));

                if (unresolvedRuns.Count > 0)
                {
                    lastInventorySummary =
                        $"{lastInventorySummary} | Unresolved='{string.Join(",", unresolvedRuns)}'";
                }

                if (selectedInventory is not null)
                {
                    WriteAssignedWorkInventory(
                        output,
                        selectedInventory);

                    return selectedInventory;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            await WriteAssignedWorkInventoryTimeoutDiagnosticsAsync(
                    output,
                    scaleOutRequestStore,
                    sharedRunStore,
                    sharedQueue,
                    runExecutionIndex,
                    tenant,
                    controlPlaneId,
                    trackedRuns,
                    inventoryTimeout,
                    lastInventorySummary)
                .ConfigureAwait(false);

            Assert.Fail(
                "Could not build a real assigned work inventory matching the expected runtime shape before crash. " +
                $"TenantId='{tenant.TenantId}', ExpectedTotal='{runCount}', ExpectedInFlight='{minimumInFlightExecutionCount}', ExpectedLocalQueued='{minimumLocalQueuedRunCount}', InventoryTimeout='{inventoryTimeout}', LastInventorySummary='{lastInventorySummary}'.");

            throw new InvalidOperationException("Unreachable assertion path.");
        }

        /// <summary>
        /// Writes one bounded durable diagnostic snapshot when assigned-work inventory
        /// construction times out. This method does not mutate shared-run, queue,
        /// scale-out, runtime-index, provider, or recovery state.
        /// </summary>
        private static async Task WriteAssignedWorkInventoryTimeoutDiagnosticsAsync(
            ITestOutputHelper output,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            IReadOnlyList<(
                string SharedRunId,
                string PipelineName,
                AiSharedRunRecord? InitialRun)> trackedRuns,
            TimeSpan inventoryTimeout,
            string lastInventorySummary)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(scaleOutRequestStore);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(trackedRuns);

            output.WriteLine(string.Empty);
            output.WriteLine("[ASSIGNED WORK INVENTORY TIMEOUT DIAGNOSTICS]");
            output.WriteLine($"CapturedAtUtc='{DateTimeOffset.UtcNow:O}'");
            output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"TenantId='{tenant.TenantId}'");
            output.WriteLine($"InventoryTimeout='{inventoryTimeout}'");
            output.WriteLine($"TrackedRunCount='{trackedRuns.Count}'");
            output.WriteLine($"LastInventorySummary='{lastInventorySummary}'");

            foreach (var trackedRun in trackedRuns)
            {
                output.WriteLine(string.Empty);
                output.WriteLine(
                    $"[ASSIGNED WORK INVENTORY RUN DIAGNOSTIC] SharedRunId='{trackedRun.SharedRunId}', PipelineName='{trackedRun.PipelineName}'.");

                AiSharedRunRecord? persistedRun = null;

                try
                {
                    persistedRun =
                        await sharedRunStore
                            .GetAsync(trackedRun.SharedRunId)
                            .ConfigureAwait(false);

                    var effectiveRun =
                        persistedRun ??
                        trackedRun.InitialRun;

                    if (effectiveRun is null)
                    {
                        output.WriteLine("  SharedRun.Exists='false'");
                    }
                    else
                    {
                        output.WriteLine("  SharedRun.Exists='true'");
                        output.WriteLine($"  SharedRun.Source='{(persistedRun is null ? "initial-submit-result" : "durable-store")}'");
                        output.WriteLine($"  SharedRun.Status='{effectiveRun.Status}'");
                        output.WriteLine($"  SharedRun.ControlPlaneId='{effectiveRun.ControlPlaneId}'");
                        output.WriteLine($"  SharedRun.AssignedRuntimeInstanceId='{effectiveRun.AssignedRuntimeInstanceId}'");
                        output.WriteLine($"  SharedRun.LocalRunId='{effectiveRun.LocalRunId}'");
                        output.WriteLine($"  SharedRun.ExecutionId='{effectiveRun.ExecutionId}'");
                        output.WriteLine($"  SharedRun.PipelineKey='{effectiveRun.PipelineKey}'");
                        output.WriteLine($"  SharedRun.Reason='{effectiveRun.Reason}'");
                        output.WriteLine($"  SharedRun.FailureReason='{effectiveRun.FailureReason}'");
                        output.WriteLine($"  SharedRun.SubmittedAtUtc='{effectiveRun.SubmittedAtUtc:O}'");
                        output.WriteLine($"  SharedRun.UpdatedAtUtc='{effectiveRun.UpdatedAtUtc:O}'");

                        var admission =
                            effectiveRun.AdmissionDecision;

                        if (admission is null)
                        {
                            output.WriteLine("  AdmissionDecision.Exists='false'");
                        }
                        else
                        {
                            output.WriteLine("  AdmissionDecision.Exists='true'");
                            output.WriteLine($"  AdmissionDecision.DecisionType='{admission.DecisionType}'");
                            output.WriteLine($"  AdmissionDecision.Accepted='{admission.Accepted}'");
                            output.WriteLine($"  AdmissionDecision.ShouldRequestScaleOut='{admission.ShouldRequestScaleOut}'");
                            output.WriteLine($"  AdmissionDecision.ShouldQueueGlobally='{admission.ShouldQueueGlobally}'");
                            output.WriteLine($"  AdmissionDecision.AssignedRuntimeInstanceId='{admission.AssignedRuntimeInstanceId}'");
                            output.WriteLine($"  AdmissionDecision.Reason='{admission.Reason}'");
                            output.WriteLine($"  AdmissionDecision.VisibleInstanceCount='{admission.VisibleInstanceCount}'");
                            output.WriteLine($"  AdmissionDecision.AvailableInstanceCount='{admission.AvailableInstanceCount}'");
                            output.WriteLine($"  AdmissionDecision.CurrentInstanceCount='{admission.CurrentInstanceCount}'");
                            output.WriteLine($"  AdmissionDecision.MaxInstanceCount='{admission.MaxInstanceCount}'");
                            output.WriteLine($"  AdmissionDecision.DecidedAtUtc='{admission.DecidedAtUtc:O}'");
                            output.WriteLine($"  AdmissionDecision.Diagnostics='{string.Join(" || ", admission.Diagnostics)}'");
                        }

                        if (!string.IsNullOrWhiteSpace(effectiveRun.LocalRunId))
                        {
                            var indexEntry =
                                await runExecutionIndex
                                    .GetAsync(effectiveRun.LocalRunId)
                                    .ConfigureAwait(false);

                            if (indexEntry is null)
                            {
                                output.WriteLine("  RuntimeIndex.Exists='false'");
                            }
                            else
                            {
                                output.WriteLine("  RuntimeIndex.Exists='true'");
                                output.WriteLine($"  RuntimeIndex.Status='{indexEntry.Status}'");
                                output.WriteLine($"  RuntimeIndex.RuntimeInstanceId='{indexEntry.RuntimeInstanceId}'");
                                output.WriteLine($"  RuntimeIndex.ExecutionId='{indexEntry.ExecutionId}'");
                                output.WriteLine($"  RuntimeIndex.CompletedAtUtc='{indexEntry.CompletedAtUtc:O}'");
                            }
                        }
                        else
                        {
                            output.WriteLine("  RuntimeIndex.Skipped='local-run-id-missing'");
                        }
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"  SharedRunOrRuntimeIndex.ReadError.Type='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }

                try
                {
                    var queueItem =
                        await sharedQueue
                            .GetAsync(trackedRun.SharedRunId)
                            .ConfigureAwait(false);

                    if (queueItem is null)
                    {
                        output.WriteLine("  SharedQueueItem.Exists='false'");
                    }
                    else
                    {
                        output.WriteLine("  SharedQueueItem.Exists='true'");
                        output.WriteLine($"  SharedQueueItem.Status='{queueItem.Status}'");
                        output.WriteLine($"  SharedQueueItem.ControlPlaneId='{queueItem.ControlPlaneId}'");
                        output.WriteLine($"  SharedQueueItem.PipelineKey='{queueItem.PipelineKey}'");
                        output.WriteLine($"  SharedQueueItem.ClaimedByRuntimeInstanceId='{queueItem.ClaimedByRuntimeInstanceId}'");
                        output.WriteLine($"  SharedQueueItem.ClaimedByWorkerId='{queueItem.ClaimedByWorkerId}'");
                        output.WriteLine($"  SharedQueueItem.ClaimToken='{queueItem.ClaimToken}'");
                        output.WriteLine($"  SharedQueueItem.EnqueuedAtUtc='{queueItem.EnqueuedAtUtc:O}'");
                        output.WriteLine($"  SharedQueueItem.UpdatedAtUtc='{queueItem.UpdatedAtUtc:O}'");
                        output.WriteLine($"  SharedQueueItem.ClaimedAtUtc='{queueItem.ClaimedAtUtc:O}'");
                        output.WriteLine($"  SharedQueueItem.ClaimExpiresAtUtc='{queueItem.ClaimExpiresAtUtc:O}'");
                        output.WriteLine($"  SharedQueueItem.Reason='{queueItem.Reason}'");
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"  SharedQueueItem.ReadError.Type='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }

                try
                {
                    var scaleOutRequests =
                        await scaleOutRequestStore
                            .ListAsync(
                                new AiRuntimeScaleOutRequestQuery
                                {
                                    ControlPlaneId = controlPlaneId,
                                    TenantId = tenant.TenantId,
                                    SharedRunId = trackedRun.SharedRunId,
                                    IncludeExpired = true,
                                    MaxResults = 100
                                })
                            .ConfigureAwait(false);

                    output.WriteLine($"  ScaleOutRequest.Count='{scaleOutRequests.Count}'");

                    var requestIndex =
                        1;

                    foreach (var request in scaleOutRequests
                        .OrderBy(item => item.CreatedAtUtc))
                    {
                        output.WriteLine(
                            $"  ScaleOutRequest[{requestIndex:00}]." +
                            $"RequestId='{request.RequestId}', " +
                            $"Status='{request.Status}', " +
                            $"ControlPlaneId='{request.ControlPlaneId}', " +
                            $"SharedRunId='{request.SharedRunId}', " +
                            $"Reason='{request.Reason}', " +
                            $"ProviderHint='{request.ProviderHint}', " +
                            $"VisibleInstanceCount='{request.VisibleInstanceCount}', " +
                            $"AvailableInstanceCount='{request.AvailableInstanceCount}', " +
                            $"CurrentInstanceCount='{request.CurrentInstanceCount}', " +
                            $"MaxInstanceCount='{request.MaxInstanceCount}', " +
                            $"RequestedTargetInstanceCount='{request.RequestedTargetInstanceCount}', " +
                            $"CreatedAtUtc='{request.CreatedAtUtc:O}', " +
                            $"ObservedAtUtc='{request.ObservedAtUtc:O}', " +
                            $"FulfilledAtUtc='{request.FulfilledAtUtc:O}', " +
                            $"RejectedAtUtc='{request.RejectedAtUtc:O}', " +
                            $"ExpiredAtUtc='{request.ExpiredAtUtc:O}', " +
                            $"CancelledAtUtc='{request.CancelledAtUtc:O}', " +
                            $"ExpiresAtUtc='{request.ExpiresAtUtc:O}', " +
                            $"FulfilledRuntimeInstanceId='{request.FulfilledRuntimeInstanceId}', " +
                            $"ObservedBy='{request.ObservedBy}', " +
                            $"FulfilledBy='{request.FulfilledBy}', " +
                            $"RejectedBy='{request.RejectedBy}', " +
                            $"RejectionReason='{request.RejectionReason}'.");

                        requestIndex++;
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"  ScaleOutRequest.ReadError.Type='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }

            output.WriteLine("[ASSIGNED WORK INVENTORY TIMEOUT DIAGNOSTICS END]");
            output.WriteLine(string.Empty);
        }

        /// <summary>
        /// Waits until the selected in-flight execution reaches the required progress,
        /// immediately kills the owning runtime process, waits for automatic recovery,
        /// and verifies strict resume semantics for all in-flight executions.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="processControl">The runtime host process control.</param>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The durable shared queue.</param>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="inventory">The selected failed-runtime inventory.</param>
        /// <param name="minimumCompletedStepsBeforeKill">The minimum durable progress required before process termination.</param>
        /// <param name="progressTimeout">The maximum crash-window observation duration.</param>
        /// <param name="unsafeTimeout">The maximum unsafe-runtime detection duration.</param>
        /// <param name="requeueTimeout">The maximum recovery requeue duration.</param>
        /// <param name="redispatchTimeout">The maximum replacement redispatch duration.</param>
        /// <param name="executionResolveTimeout">The maximum durable execution resolution duration.</param>
        /// <param name="observationMode">The production recovery observation mode.</param>
        /// <param name="signalSubscriber">The runtime signal subscriber used only in hybrid mode.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier used only in hybrid mode.</param>
        /// <param name="hybridFallbackPollInterval">The slow durable fallback interval used only in hybrid mode.</param>
        /// <param name="crashCheckpointGate">The optional durable crash checkpoint released immediately after process termination.</param>
        /// <param name="runtimeTenantOwnershipAssertion">The optional authoritative runtime tenant ownership assertion. When omitted, the historical runtime-id naming assertion is preserved.</param>
        /// <returns>The failed-runtime recovery proof.</returns>
        public static async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            KillRuntimeAndRecoverAssignedInventoryAsync(
                ITestOutputHelper output,
                IAiRuntimeHostProcessControl processControl,
                IAiRuntimeInstanceRegistry registry,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IAiSharedRunStore sharedRunStore,
                IAiSharedQueue sharedQueue,
                IAiDagExecutionStore dagStore,
                RealRuntimeCrashAssignedWorkInventoryProof inventory,
                int minimumCompletedStepsBeforeKill,
                TimeSpan progressTimeout,
                TimeSpan unsafeTimeout,
                TimeSpan requeueTimeout,
                TimeSpan redispatchTimeout,
                TimeSpan executionResolveTimeout,
                ProductionRecoveryObservationMode observationMode =
                    ProductionRecoveryObservationMode.Polling,
                IAiRuntimeSignalSubscriber? signalSubscriber = null,
                string? controlPlaneId = null,
                TimeSpan? hybridFallbackPollInterval = null,
                ProductionCrashCheckpointGate? crashCheckpointGate = null,
                Func<IAiRuntimeInstanceRegistry, string, ProductionTenantScenarioDefinition, Task>?
                    runtimeTenantOwnershipAssertion = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(processControl);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(inventory);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedStepsBeforeKill);

            if (progressTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(progressTimeout),
                    progressTimeout,
                    "The progress timeout must be greater than zero.");
            }

            if (observationMode != ProductionRecoveryObservationMode.Polling &&
                observationMode != ProductionRecoveryObservationMode.HybridSignals)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(observationMode),
                    observationMode,
                    "The production recovery observation mode is not supported.");
            }

            var resolvedHybridFallbackPollInterval =
                hybridFallbackPollInterval ?? TimeSpan.FromSeconds(2);

            if (observationMode == ProductionRecoveryObservationMode.HybridSignals)
            {
                ArgumentNullException.ThrowIfNull(signalSubscriber);
                ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

                if (resolvedHybridFallbackPollInterval <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(hybridFallbackPollInterval),
                        resolvedHybridFallbackPollInterval,
                        "The hybrid fallback polling interval must be greater than zero.");
                }
            }

            using var sharedRunSignalLifetime =
                new CancellationTokenSource();

            var sharedRunDispatchSubscriptions =
                new Dictionary<string, IAiRuntimeSignalSubscription>(
                    StringComparer.Ordinal);

            var sharedRunDispatchSignalTasks =
                new Dictionary<string, Task<AiRuntimeSignal>>(
                    StringComparer.Ordinal);

            try
            {
                var inFlightWork = Assert.Single(inventory.InFlightExecutions);

                Assert.False(string.IsNullOrWhiteSpace(inFlightWork.ExecutionId));

                /*
                 * Start local-queued diagnostic reads without awaiting them.
                 * They must never delay observation and termination of the in-flight DAG.
                 */
                var localQueuedPreKillSnapshotTasks = inventory.LocalQueuedRuns
                    .Select(CaptureLocalQueuedPreKillSnapshotAsync)
                    .ToArray();

                var killObservation =
                    observationMode == ProductionRecoveryObservationMode.HybridSignals
                        ? await ObserveCrashProgressAndKillHybridAsync(
                                processControl,
                                runExecutionIndex,
                                dagStore,
                                signalSubscriber!,
                                controlPlaneId!,
                                inventory,
                                inFlightWork,
                                minimumCompletedStepsBeforeKill,
                                progressTimeout,
                                resolvedHybridFallbackPollInterval)
                            .ConfigureAwait(false)
                        : await ObserveCrashProgressAndKillPollingAsync(
                                processControl,
                                runExecutionIndex,
                                dagStore,
                                inventory,
                                inFlightWork,
                                minimumCompletedStepsBeforeKill,
                                progressTimeout)
                            .ConfigureAwait(false);

                if (killObservation.Killed &&
                    crashCheckpointGate is not null)
                {
                    await crashCheckpointGate
                        .ReleaseAsync()
                        .ConfigureAwait(false);
                }

                /*
                 * Redispatch subscriptions belong after process termination.
                 * Creating them before crash observation can consume the entire
                 * in-flight DAG window under high parallel pressure. Durable
                 * fallback polling still guarantees convergence if a signal is
                 * published before a subscription becomes active.
                 */
                if (observationMode == ProductionRecoveryObservationMode.HybridSignals)
                {
                    foreach (var work in inventory.Works)
                    {
                        var subscription = await signalSubscriber!
                            .SubscribeAsync(
                                AiRuntimeSignalType.SharedRunDispatched,
                                controlPlaneId!,
                                work.SharedRunId)
                            .ConfigureAwait(false);

                        sharedRunDispatchSubscriptions.Add(
                            work.SharedRunId,
                            subscription);

                        sharedRunDispatchSignalTasks.Add(
                            work.SharedRunId,
                            ReadRequiredSharedRunDispatchedSignalAsync(
                                subscription,
                                controlPlaneId!,
                                work.SharedRunId,
                                sharedRunSignalLifetime.Token));

                        output.WriteLine(
                            $"[REAL RUNTIME INVENTORY REDISPATCH SIGNAL SUBSCRIBED AFTER KILL] " +
                            $"ObservationMode='{observationMode}', " +
                            $"TenantId='{inventory.Tenant.TenantId}', " +
                            $"SharedRunId='{work.SharedRunId}', " +
                            $"Kind='{work.Kind}'.");
                    }
                }

                var observedCrashSnapshot =
                    killObservation.ObservedCrashSnapshot;

                var lastObservedInFlightSnapshot =
                    killObservation.LastObservedInFlightSnapshot;

                var killed =
                    killObservation.Killed;

                var killRequestedAtUtc =
                    killObservation.KillRequestedAtUtc;

                var killCompletedAtUtc =
                    killObservation.KillCompletedAtUtc;

                if (observedCrashSnapshot is null)
                {
                    Assert.Fail(
                        "The selected in-flight execution did not reach the required crash progress before the timeout. " +
                        $"TenantId='{inventory.Tenant.TenantId}', " +
                        $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                        $"SharedRunId='{inFlightWork.SharedRunId}', " +
                        $"LocalRunId='{inFlightWork.LocalRunId}', " +
                        $"ExecutionId='{inFlightWork.ExecutionId}', " +
                        $"ExpectedCompletedSteps='{minimumCompletedStepsBeforeKill}', " +
                        $"LastIndexStatus='{lastObservedInFlightSnapshot?.IndexStatus}', " +
                        $"LastIndexRuntimeInstanceId='{lastObservedInFlightSnapshot?.RuntimeInstanceId}', " +
                        $"LastIndexExecutionId='{lastObservedInFlightSnapshot?.ExecutionId}', " +
                        $"LastDagStatus='{lastObservedInFlightSnapshot?.DagStatus}', " +
                        $"LastCompletedSteps='{lastObservedInFlightSnapshot?.DagCompletedStepCount}', " +
                        $"LastTotalSteps='{lastObservedInFlightSnapshot?.DagTotalStepCount}', " +
                        $"ObservationMode='{observationMode}', " +
                        $"ProgressWakeSource='{killObservation.ProgressWakeSource}', " +
                        $"SignalObserved='{killObservation.ProgressSignal is not null}', " +
                        $"SignalCompletedSteps='{killObservation.ProgressSignal?.CompletedStepCount}', " +
                        $"SignalTotalSteps='{killObservation.ProgressSignal?.TotalStepCount}', " +
                        $"FallbackReadCount='{killObservation.FallbackReadCount}', " +
                        $"ProgressTimeout='{progressTimeout}'.");

                    throw new InvalidOperationException("Unreachable assertion path.");
                }

                Assert.True(
                    killed,
                    $"Runtime process was not killed. " +
                    $"TenantId='{inventory.Tenant.TenantId}', " +
                    $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                    $"ExecutionId='{inFlightWork.ExecutionId}'.");

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY CRASH READY] " +
                    $"TenantId='{inventory.Tenant.TenantId}', " +
                    $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                    $"SharedRunId='{inFlightWork.SharedRunId}', " +
                    $"LocalRunId='{inFlightWork.LocalRunId}', " +
                    $"ExecutionId='{inFlightWork.ExecutionId}', " +
                    $"IndexStatus='{observedCrashSnapshot.IndexStatus}', " +
                    $"DagStatus='{observedCrashSnapshot.DagStatus}', " +
                    $"CompletedSteps='{observedCrashSnapshot.DagCompletedStepCount}', " +
                    $"TotalSteps='{observedCrashSnapshot.DagTotalStepCount}', " +
                    $"MinimumCompletedSteps='{minimumCompletedStepsBeforeKill}', " +
                    $"ObservationMode='{observationMode}', " +
                    $"ProgressWakeSource='{killObservation.ProgressWakeSource}', " +
                    $"SignalObserved='{killObservation.ProgressSignal is not null}', " +
                    $"SignalCompletedSteps='{killObservation.ProgressSignal?.CompletedStepCount}', " +
                    $"SignalTotalSteps='{killObservation.ProgressSignal?.TotalStepCount}', " +
                    $"FallbackReadCount='{killObservation.FallbackReadCount}'.");

                /*
                 * Full inventory capture is safe now because the runtime process
                 * has already been terminated.
                 */
                var postKillSnapshots = await CaptureWorkStateSnapshotsAsync(
                        runExecutionIndex,
                        dagStore,
                        inventory)
                    .ConfigureAwait(false);

                var localQueuedPreKillSnapshots = await Task
                    .WhenAll(localQueuedPreKillSnapshotTasks)
                    .ConfigureAwait(false);

                var preKillSnapshotMap = localQueuedPreKillSnapshots.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);

                preKillSnapshotMap[inFlightWork.LocalRunId] = observedCrashSnapshot;

                IReadOnlyDictionary<string, RealRuntimeCrashWorkStateSnapshot> preKillSnapshots =
                    preKillSnapshotMap;

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY CRASH] Runtime process killed. " +
                    $"TenantId='{inventory.Tenant.TenantId}', " +
                    $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                    $"WorkCount='{inventory.Works.Count}', " +
                    $"InFlight='{inventory.InFlightExecutions.Count}', " +
                    $"LocalQueued='{inventory.LocalQueuedRuns.Count}', " +
                    $"KillRequestedAtUtc='{killRequestedAtUtc:O}', " +
                    $"KillCompletedAtUtc='{killCompletedAtUtc:O}', " +
                    $"KillDuration='{killCompletedAtUtc - killRequestedAtUtc}'.");

                foreach (var work in inventory.Works)
                {
                    var preKill = preKillSnapshots[work.LocalRunId];
                    var postKill = postKillSnapshots[work.LocalRunId];

                    var completionTiming = ClassifyCompletionTiming(
                        postKill.IndexCompletedAtUtc,
                        killRequestedAtUtc,
                        killCompletedAtUtc);

                    output.WriteLine(
                        $"[REAL RUNTIME INVENTORY KILL WINDOW] " +
                        $"TenantId='{inventory.Tenant.TenantId}', " +
                        $"FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                        $"SharedRunId='{work.SharedRunId}', " +
                        $"LocalRunId='{work.LocalRunId}', " +
                        $"ExpectedExecutionId='{work.ExecutionId}', " +
                        $"Kind='{work.Kind}', " +
                        $"PreKillIndexStatus='{preKill.IndexStatus}', " +
                        $"PreKillIndexRuntimeInstanceId='{preKill.RuntimeInstanceId}', " +
                        $"PreKillIndexExecutionId='{preKill.ExecutionId}', " +
                        $"PreKillIndexCompletedAtUtc='{preKill.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                        $"PreKillDagStatus='{preKill.DagStatus}', " +
                        $"PreKillCompletedSteps='{preKill.DagCompletedStepCount}', " +
                        $"PreKillTotalSteps='{preKill.DagTotalStepCount}', " +
                        $"PreKillDagStepStatusBreakdown='{preKill.DagStepStatusBreakdown}', " +
                        $"PreKillCapturedAtUtc='{preKill.CapturedAtUtc:O}', " +
                        $"PostKillIndexStatus='{postKill.IndexStatus}', " +
                        $"PostKillIndexRuntimeInstanceId='{postKill.RuntimeInstanceId}', " +
                        $"PostKillIndexExecutionId='{postKill.ExecutionId}', " +
                        $"PostKillIndexCompletedAtUtc='{postKill.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                        $"PostKillDagStatus='{postKill.DagStatus}', " +
                        $"PostKillCompletedSteps='{postKill.DagCompletedStepCount}', " +
                        $"PostKillTotalSteps='{postKill.DagTotalStepCount}', " +
                        $"PostKillDagStepStatusBreakdown='{postKill.DagStepStatusBreakdown}', " +
                        $"PostKillCapturedAtUtc='{postKill.CapturedAtUtc:O}', " +
                        $"KillRequestedAtUtc='{killRequestedAtUtc:O}', " +
                        $"KillCompletedAtUtc='{killCompletedAtUtc:O}', " +
                        $"CompletionTiming='{completionTiming}'.");
                }

                await ProductionRecoveryWaitHelpers
                    .WaitForRuntimeInstanceUnsafeAsync(
                        registry,
                        inventory.RuntimeInstanceId,
                        unsafeTimeout)
                    .ConfigureAwait(false);

                output.WriteLine(
                    $"[REAL RUNTIME INVENTORY CRASH] Runtime instance marked unsafe. " +
                    $"TenantId='{inventory.Tenant.TenantId}', " +
                    $"RuntimeInstanceId='{inventory.RuntimeInstanceId}'. " +
                    "Waiting for automatic execution recovery reconciliation.");

                foreach (var work in inventory.Works)
                {
                    await WaitForWorkRequeuedForRecoveryAsync(
                            runExecutionIndex,
                            dagStore,
                            inventory,
                            work,
                            preKillSnapshots[work.LocalRunId],
                            postKillSnapshots[work.LocalRunId],
                            killRequestedAtUtc,
                            killCompletedAtUtc,
                            requeueTimeout)
                        .ConfigureAwait(false);
                }

                var recoveredWorks = new List<RealRuntimeCrashRecoveredWorkProof>();

                foreach (var work in inventory.Works)
                {
                    var redispatchedRun =
                        observationMode == ProductionRecoveryObservationMode.HybridSignals
                            ? await ProductionRecoveryWaitHelpers
                                .WaitForRecoveredRunRedispatchedHybridAsync(
                                    sharedRunStore,
                                    sharedQueue,
                                    work.SharedRunId,
                                    inventory.RuntimeInstanceId,
                                    work.LocalRunId,
                                    sharedRunDispatchSignalTasks[work.SharedRunId],
                                    redispatchTimeout,
                                    resolvedHybridFallbackPollInterval)
                                .ConfigureAwait(false)
                            : await ProductionRecoveryWaitHelpers
                                .WaitForRecoveredRunRedispatchedAsync(
                                    sharedRunStore,
                                    sharedQueue,
                                    work.SharedRunId,
                                    inventory.RuntimeInstanceId,
                                    work.LocalRunId,
                                    redispatchTimeout)
                                .ConfigureAwait(false);

                    Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
                    Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
                    Assert.NotEqual(inventory.RuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
                    Assert.NotEqual(work.LocalRunId, redispatchedRun.LocalRunId);

                    await AssertRuntimeBelongsToTenantAsync(
                            registry,
                            redispatchedRun.AssignedRuntimeInstanceId!,
                            inventory.Tenant,
                            runtimeTenantOwnershipAssertion)
                        .ConfigureAwait(false);

                    string recoveredExecutionId;

                    if (work.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                    {
                        var recoveredExecution = await ProductionRecoveryWaitHelpers
                            .WaitForDurableDagExecutionAsync(
                                sharedRunStore,
                                runExecutionIndex,
                                dagStore,
                                redispatchedRun.SharedRunId,
                                executionResolveTimeout)
                            .ConfigureAwait(false);

                        recoveredExecutionId = recoveredExecution.ExecutionId;

                        Assert.False(string.IsNullOrWhiteSpace(work.ExecutionId));
                        Assert.Equal(work.ExecutionId, recoveredExecutionId);
                    }
                    else
                    {
                        var replacementIndex = await WaitForReplacementLocalQueuedRunIndexAsync(
                                runExecutionIndex,
                                redispatchedRun.LocalRunId!,
                                redispatchedRun.AssignedRuntimeInstanceId!,
                                executionResolveTimeout)
                            .ConfigureAwait(false);

                        recoveredExecutionId = replacementIndex.ExecutionId ?? string.Empty;

                        Assert.True(
                            string.Equals(
                                replacementIndex.Status,
                                "queued",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                replacementIndex.Status,
                                "creating-execution",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                replacementIndex.Status,
                                "running",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                replacementIndex.Status,
                                "completed",
                                StringComparison.OrdinalIgnoreCase),
                            $"Recovered local queued run has an unexpected runtime index status. " +
                            $"SharedRunId='{work.SharedRunId}', " +
                            $"ReplacementLocalRunId='{redispatchedRun.LocalRunId}', " +
                            $"Status='{replacementIndex.Status}'.");

                        output.WriteLine(
                            $"[REAL RUNTIME INVENTORY LOCAL QUEUED RECOVERY] " +
                            $"Local queued work redispatched. " +
                            $"TenantId='{inventory.Tenant.TenantId}', " +
                            $"SharedRunId='{work.SharedRunId}', " +
                            $"FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                            $"FailedLocalRunId='{work.LocalRunId}', " +
                            $"ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', " +
                            $"ReplacementLocalRunId='{redispatchedRun.LocalRunId}', " +
                            $"ReplacementIndexStatus='{replacementIndex.Status}', " +
                            $"ReplacementExecutionId='{replacementIndex.ExecutionId}'.");
                    }

                    recoveredWorks.Add(
                        new RealRuntimeCrashRecoveredWorkProof
                        {
                            Original = work,
                            RedispatchedRun = redispatchedRun,
                            ReplacementRuntimeInstanceId =
                                redispatchedRun.AssignedRuntimeInstanceId!,
                            ReplacementLocalRunId = redispatchedRun.LocalRunId!,
                            RecoveredExecutionId = recoveredExecutionId
                        });

                    output.WriteLine(
                        $"[REAL RUNTIME INVENTORY RECOVERY] Work recovered. " +
                        $"TenantId='{inventory.Tenant.TenantId}', " +
                        $"Kind='{work.Kind}', " +
                        $"SharedRunId='{work.SharedRunId}', " +
                        $"FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                        $"FailedLocalRunId='{work.LocalRunId}', " +
                        $"ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', " +
                        $"ReplacementLocalRunId='{redispatchedRun.LocalRunId}', " +
                        $"ExecutionIdBefore='{work.ExecutionId}', " +
                        $"ExecutionIdAfter='{recoveredExecutionId}'.");
                }

                var proof = new RealRuntimeCrashFailedRuntimeRecoveryProof
                {
                    FailedInventory = inventory,
                    RecoveredWorks = recoveredWorks
                };

                AssertRecoveredInventoryStrictResume(proof);
                WriteRecoveredInventory(output, proof);

                return proof;

                async Task<KeyValuePair<string, RealRuntimeCrashWorkStateSnapshot>>
                    CaptureLocalQueuedPreKillSnapshotAsync(
                        RealRuntimeCrashWorkProof work)
                {
                    var indexEntry = await runExecutionIndex
                        .GetAsync(work.LocalRunId)
                        .ConfigureAwait(false);

                    return new KeyValuePair<string, RealRuntimeCrashWorkStateSnapshot>(
                        work.LocalRunId,
                        new RealRuntimeCrashWorkStateSnapshot(
                            indexEntry?.Status,
                            indexEntry?.RuntimeInstanceId,
                            indexEntry?.ExecutionId,
                            indexEntry?.CompletedAtUtc,
                            null,
                            0,
                            0,
                            string.Empty,
                            DateTimeOffset.UtcNow));
                }
            }
            finally
            {
                sharedRunSignalLifetime.Cancel();

                foreach (var subscription in
                    sharedRunDispatchSubscriptions.Values)
                {
                    await subscription
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }

                foreach (var signalTask in
                    sharedRunDispatchSignalTasks.Values)
                {
                    if (!signalTask.IsCompleted)
                    {
                        continue;
                    }

                    try
                    {
                        await signalTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when durable convergence wins before the signal.
                    }
                    catch (InvalidOperationException)
                    {
                        // A best-effort signal stream may close during test-host shutdown.
                    }
                }
            }
        }

        /// <summary>
        /// Observes the crash window through the historical durable polling path.
        /// </summary>
        private static async Task<RealRuntimeCrashKillObservation> ObserveCrashProgressAndKillPollingAsync(
            IAiRuntimeHostProcessControl processControl,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            RealRuntimeCrashAssignedWorkInventoryProof inventory,
            RealRuntimeCrashWorkProof inFlightWork,
            int minimumCompletedStepsBeforeKill,
            TimeSpan progressTimeout)
        {
            ArgumentNullException.ThrowIfNull(processControl);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(inFlightWork);

            var progressDeadline =
                DateTimeOffset.UtcNow.Add(progressTimeout);

            RealRuntimeCrashWorkStateSnapshot? observedCrashSnapshot =
                null;

            RealRuntimeCrashWorkStateSnapshot? lastObservedInFlightSnapshot =
                null;

            var killed =
                false;

            var killRequestedAtUtc =
                default(DateTimeOffset);

            var killCompletedAtUtc =
                default(DateTimeOffset);

            while (DateTimeOffset.UtcNow < progressDeadline)
            {
                /*
                 * This is the historical polling implementation. Keep this path
                 * unchanged so Polling remains the stable baseline.
                 */
                var dagRecord =
                    await dagStore
                        .GetRecordAsync(inFlightWork.ExecutionId!)
                        .ConfigureAwait(false);

                var completedStepCount =
                    dagRecord?.CompletedSteps?.Count ?? 0;

                var totalStepCount =
                    dagRecord?.Steps?.Count ?? 0;

                var statusBreakdown =
                    dagRecord is null
                        ? string.Empty
                        : $"Completed:{completedStepCount},Remaining:{Math.Max(0, totalStepCount - completedStepCount)}";

                /*
                 * The runtime execution index remains the final durable read before
                 * evaluating the crash condition and calling KillAsync.
                 */
                var indexEntry =
                    await runExecutionIndex
                        .GetAsync(inFlightWork.LocalRunId)
                        .ConfigureAwait(false);

                var currentInFlightSnapshot =
                    new RealRuntimeCrashWorkStateSnapshot(
                        indexEntry?.Status,
                        indexEntry?.RuntimeInstanceId,
                        indexEntry?.ExecutionId,
                        indexEntry?.CompletedAtUtc,
                        dagRecord?.Status.ToString(),
                        completedStepCount,
                        totalStepCount,
                        statusBreakdown,
                        DateTimeOffset.UtcNow);

                lastObservedInFlightSnapshot =
                    currentInFlightSnapshot;

                var runtimeInstanceMatches =
                    string.Equals(
                        currentInFlightSnapshot.RuntimeInstanceId,
                        inventory.RuntimeInstanceId,
                        StringComparison.Ordinal);

                var executionMatches =
                    string.Equals(
                        currentInFlightSnapshot.ExecutionId,
                        inFlightWork.ExecutionId,
                        StringComparison.Ordinal);

                var indexIsRunning =
                    string.Equals(
                        currentInFlightSnapshot.IndexStatus,
                        "running",
                        StringComparison.OrdinalIgnoreCase);

                var dagIsRunning =
                    string.Equals(
                        currentInFlightSnapshot.DagStatus,
                        "Running",
                        StringComparison.OrdinalIgnoreCase);

                var requiredProgressReached =
                    currentInFlightSnapshot.DagCompletedStepCount >=
                    minimumCompletedStepsBeforeKill;

                var executionStillIncomplete =
                    currentInFlightSnapshot.DagTotalStepCount > 0 &&
                    currentInFlightSnapshot.DagCompletedStepCount <
                    currentInFlightSnapshot.DagTotalStepCount;

                if (runtimeInstanceMatches &&
                    executionMatches &&
                    indexIsRunning &&
                    dagIsRunning &&
                    requiredProgressReached &&
                    executionStillIncomplete)
                {
                    observedCrashSnapshot =
                        currentInFlightSnapshot;

                    /*
                     * No additional read, logging operation, delay, or snapshot capture
                     * is permitted between this timestamp and KillAsync.
                     */
                    killRequestedAtUtc =
                        DateTimeOffset.UtcNow;

                    killed =
                        await processControl
                            .KillAsync(inventory.RuntimeInstanceId)
                            .ConfigureAwait(false);

                    killCompletedAtUtc =
                        DateTimeOffset.UtcNow;

                    break;
                }

                var indexIsCompleted =
                    string.Equals(
                        currentInFlightSnapshot.IndexStatus,
                        "completed",
                        StringComparison.OrdinalIgnoreCase);

                var dagIsCompleted =
                    string.Equals(
                        currentInFlightSnapshot.DagStatus,
                        "Completed",
                        StringComparison.OrdinalIgnoreCase);

                var allDagStepsCompleted =
                    currentInFlightSnapshot.DagTotalStepCount > 0 &&
                    currentInFlightSnapshot.DagCompletedStepCount >=
                    currentInFlightSnapshot.DagTotalStepCount;

                if (indexIsCompleted ||
                    dagIsCompleted ||
                    allDagStepsCompleted)
                {
                    Assert.Fail(
                        "The selected in-flight execution completed before the runtime process could be killed. " +
                        $"TenantId='{inventory.Tenant.TenantId}', " +
                        $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                        $"SharedRunId='{inFlightWork.SharedRunId}', " +
                        $"LocalRunId='{inFlightWork.LocalRunId}', " +
                        $"ExecutionId='{inFlightWork.ExecutionId}', " +
                        $"ObservationMode='{ProductionRecoveryObservationMode.Polling}', " +
                        $"IndexStatus='{currentInFlightSnapshot.IndexStatus}', " +
                        $"IndexRuntimeInstanceId='{currentInFlightSnapshot.RuntimeInstanceId}', " +
                        $"IndexExecutionId='{currentInFlightSnapshot.ExecutionId}', " +
                        $"DagStatus='{currentInFlightSnapshot.DagStatus}', " +
                        $"CompletedSteps='{currentInFlightSnapshot.DagCompletedStepCount}', " +
                        $"TotalSteps='{currentInFlightSnapshot.DagTotalStepCount}', " +
                        $"MinimumCompletedSteps='{minimumCompletedStepsBeforeKill}', " +
                        $"IndexCompletedAtUtc='{currentInFlightSnapshot.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                        $"CapturedAtUtc='{currentInFlightSnapshot.CapturedAtUtc:O}'.");
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(50))
                    .ConfigureAwait(false);
            }

            return new RealRuntimeCrashKillObservation(
                observedCrashSnapshot,
                lastObservedInFlightSnapshot,
                killed,
                killRequestedAtUtc,
                killCompletedAtUtc,
                "Polling",
                0,
                null);
        }

        /// <summary>
        /// Observes the crash window through targeted DAG progress signals with a slow durable fallback.
        /// </summary>
        private static async Task<RealRuntimeCrashKillObservation> ObserveCrashProgressAndKillHybridAsync(
            IAiRuntimeHostProcessControl processControl,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            IAiRuntimeSignalSubscriber signalSubscriber,
            string controlPlaneId,
            RealRuntimeCrashAssignedWorkInventoryProof inventory,
            RealRuntimeCrashWorkProof inFlightWork,
            int minimumCompletedStepsBeforeKill,
            TimeSpan progressTimeout,
            TimeSpan fallbackPollInterval)
        {
            ArgumentNullException.ThrowIfNull(processControl);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(signalSubscriber);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(inFlightWork);

            var progressDeadline =
                DateTimeOffset.UtcNow.Add(progressTimeout);

            RealRuntimeCrashWorkStateSnapshot? observedCrashSnapshot =
                null;

            RealRuntimeCrashWorkStateSnapshot? lastObservedInFlightSnapshot =
                null;

            var killed =
                false;

            var killRequestedAtUtc =
                default(DateTimeOffset);

            var killCompletedAtUtc =
                default(DateTimeOffset);

            var progressWakeSource =
                "DurableInitial";

            var fallbackReadCount =
                0;

            AiRuntimeSignal? progressSignal =
                null;

            using var signalLifetime =
                new CancellationTokenSource();

            await using var subscription =
                await signalSubscriber
                    .SubscribeAsync(
                        AiRuntimeSignalType.DagProgressChanged,
                        controlPlaneId,
                        inFlightWork.ExecutionId!)
                    .ConfigureAwait(false);



            Task<AiRuntimeSignal>? progressSignalTask =
                ReadRequiredDagProgressSignalAsync(
                    subscription,
                    controlPlaneId,
                    inFlightWork.ExecutionId!,
                    minimumCompletedStepsBeforeKill,
                    signalLifetime.Token);

            var initialDurableRead =
                true;

            try
            {
                while (DateTimeOffset.UtcNow < progressDeadline)
                {
                    if (!initialDurableRead)
                    {
                        var remaining =
                            progressDeadline - DateTimeOffset.UtcNow;

                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        var waitDuration =
                            remaining < fallbackPollInterval
                                ? remaining
                                : fallbackPollInterval;

                        var fallbackDelayTask =
                            Task.Delay(waitDuration);

                        if (progressSignalTask is not null)
                        {
                            var completedTask =
                                await Task
                                    .WhenAny(
                                        progressSignalTask,
                                        fallbackDelayTask)
                                    .ConfigureAwait(false);

                            if (completedTask == progressSignalTask)
                            {
                                progressSignal =
                                    await progressSignalTask
                                        .ConfigureAwait(false);

                                progressSignalTask =
                                    null;

                                progressWakeSource =
                                    "DagProgressChanged";
                            }
                            else
                            {
                                fallbackReadCount++;
                                progressWakeSource =
                                    "DurableFallback";
                            }
                        }
                        else
                        {
                            await fallbackDelayTask
                                .ConfigureAwait(false);

                            fallbackReadCount++;
                            progressWakeSource =
                                "DurableFallback";
                        }
                    }

                    initialDurableRead =
                        false;

                    /*
                     * Signals only wake the observer. The complete durable DAG state
                     * remains the authoritative progress proof in hybrid mode.
                     */
                    var dagState =
                        await dagStore
                            .GetStateAsync(inFlightWork.ExecutionId!)
                            .ConfigureAwait(false);

                    var completedStepCount =
                        dagState?.Steps.Values.Count(step =>
                            step.Status == AiStepExecutionStatus.Completed) ?? 0;

                    var totalStepCount =
                        dagState?.Steps.Count ?? 0;

                    var statusBreakdown =
                        dagState is null
                            ? string.Empty
                            : string.Join(
                                ",",
                                dagState.Steps.Values
                                    .GroupBy(step => step.Status)
                                    .OrderBy(group => group.Key)
                                    .Select(group =>
                                        $"{group.Key}:{group.Count()}"));

                    var dagRecord =
                        await dagStore
                            .GetRecordAsync(inFlightWork.ExecutionId!)
                            .ConfigureAwait(false);

                    /*
                     * The runtime execution index remains the final durable read before
                     * evaluating the crash condition and calling KillAsync.
                     */
                    var indexEntry =
                        await runExecutionIndex
                            .GetAsync(inFlightWork.LocalRunId)
                            .ConfigureAwait(false);

                    var currentInFlightSnapshot =
                        new RealRuntimeCrashWorkStateSnapshot(
                            indexEntry?.Status,
                            indexEntry?.RuntimeInstanceId,
                            indexEntry?.ExecutionId,
                            indexEntry?.CompletedAtUtc,
                            dagRecord?.Status.ToString(),
                            completedStepCount,
                            totalStepCount,
                            statusBreakdown,
                            DateTimeOffset.UtcNow);

                    lastObservedInFlightSnapshot =
                        currentInFlightSnapshot;

                    var runtimeInstanceMatches =
                        string.Equals(
                            currentInFlightSnapshot.RuntimeInstanceId,
                            inventory.RuntimeInstanceId,
                            StringComparison.Ordinal);

                    var executionMatches =
                        string.Equals(
                            currentInFlightSnapshot.ExecutionId,
                            inFlightWork.ExecutionId,
                            StringComparison.Ordinal);

                    var indexIsRunning =
                        string.Equals(
                            currentInFlightSnapshot.IndexStatus,
                            "running",
                            StringComparison.OrdinalIgnoreCase);

                    var dagIsRunning =
                        string.Equals(
                            currentInFlightSnapshot.DagStatus,
                            "Running",
                            StringComparison.OrdinalIgnoreCase);

                    var requiredProgressReached =
                        currentInFlightSnapshot.DagCompletedStepCount >=
                        minimumCompletedStepsBeforeKill;

                    var executionStillIncomplete =
                        currentInFlightSnapshot.DagTotalStepCount > 0 &&
                        currentInFlightSnapshot.DagCompletedStepCount <
                        currentInFlightSnapshot.DagTotalStepCount;

                    if (runtimeInstanceMatches &&
                        executionMatches &&
                        indexIsRunning &&
                        dagIsRunning &&
                        requiredProgressReached &&
                        executionStillIncomplete)
                    {
                        observedCrashSnapshot =
                            currentInFlightSnapshot;

                        /*
                         * No additional read, logging operation, delay, or snapshot capture
                         * is permitted between this timestamp and KillAsync.
                         */
                        killRequestedAtUtc =
                            DateTimeOffset.UtcNow;

                        killed =
                            await processControl
                                .KillAsync(inventory.RuntimeInstanceId)
                                .ConfigureAwait(false);

                        killCompletedAtUtc =
                            DateTimeOffset.UtcNow;

                        break;
                    }

                    var indexIsCompleted =
                        string.Equals(
                            currentInFlightSnapshot.IndexStatus,
                            "completed",
                            StringComparison.OrdinalIgnoreCase);

                    var dagIsCompleted =
                        string.Equals(
                            currentInFlightSnapshot.DagStatus,
                            "Completed",
                            StringComparison.OrdinalIgnoreCase);

                    var allDagStepsCompleted =
                        currentInFlightSnapshot.DagTotalStepCount > 0 &&
                        currentInFlightSnapshot.DagCompletedStepCount >=
                        currentInFlightSnapshot.DagTotalStepCount;

                    if (indexIsCompleted ||
                        dagIsCompleted ||
                        allDagStepsCompleted)
                    {
                        Assert.Fail(
                            "The selected in-flight execution completed before the runtime process could be killed. " +
                            $"TenantId='{inventory.Tenant.TenantId}', " +
                            $"RuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                            $"SharedRunId='{inFlightWork.SharedRunId}', " +
                            $"LocalRunId='{inFlightWork.LocalRunId}', " +
                            $"ExecutionId='{inFlightWork.ExecutionId}', " +
                            $"ObservationMode='{ProductionRecoveryObservationMode.HybridSignals}', " +
                            $"ProgressWakeSource='{progressWakeSource}', " +
                            $"SignalObserved='{progressSignal is not null}', " +
                            $"SignalCompletedSteps='{progressSignal?.CompletedStepCount}', " +
                            $"SignalTotalSteps='{progressSignal?.TotalStepCount}', " +
                            $"FallbackReadCount='{fallbackReadCount}', " +
                            $"IndexStatus='{currentInFlightSnapshot.IndexStatus}', " +
                            $"IndexRuntimeInstanceId='{currentInFlightSnapshot.RuntimeInstanceId}', " +
                            $"IndexExecutionId='{currentInFlightSnapshot.ExecutionId}', " +
                            $"DagStatus='{currentInFlightSnapshot.DagStatus}', " +
                            $"CompletedSteps='{currentInFlightSnapshot.DagCompletedStepCount}', " +
                            $"TotalSteps='{currentInFlightSnapshot.DagTotalStepCount}', " +
                            $"MinimumCompletedSteps='{minimumCompletedStepsBeforeKill}', " +
                            $"IndexCompletedAtUtc='{currentInFlightSnapshot.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                            $"CapturedAtUtc='{currentInFlightSnapshot.CapturedAtUtc:O}'.");
                    }
                }
            }
            finally
            {
                signalLifetime.Cancel();

                if (progressSignalTask is not null)
                {
                    try
                    {
                        await progressSignalTask
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when durable convergence or process termination wins.
                    }
                }
            }

            return new RealRuntimeCrashKillObservation(
                observedCrashSnapshot,
                lastObservedInFlightSnapshot,
                killed,
                killRequestedAtUtc,
                killCompletedAtUtc,
                progressWakeSource,
                fallbackReadCount,
                progressSignal);
        }

        /// <summary>
        /// Reads the first targeted DAG progress signal that reaches the required threshold.
        /// </summary>
        private static async Task<AiRuntimeSignal> ReadRequiredDagProgressSignalAsync(
            IAiRuntimeSignalSubscription subscription,
            string controlPlaneId,
            string executionId,
            int minimumCompletedSteps,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedSteps);

            await foreach (var signal in subscription
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (signal.Type != AiRuntimeSignalType.DagProgressChanged ||
                    !string.Equals(
                        signal.ControlPlaneId,
                        controlPlaneId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        signal.ExecutionId,
                        executionId,
                        StringComparison.Ordinal) ||
                    signal.CompletedStepCount is not int completedStepCount ||
                    completedStepCount < minimumCompletedSteps)
                {
                    continue;
                }

                return signal;
            }

            throw new InvalidOperationException(
                "The targeted DAG progress signal subscription completed unexpectedly.");
        }

        /// <summary>
        /// Verifies that recovered in-flight DAG executions and recovered local queued DAG executions
        /// reached the expected completed step count after durable redispatch.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="proof">The failed runtime recovery proof.</param>
        /// <param name="expectedCompletedStepCount">The expected completed step count.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>A task that completes when all recovered DAG executions have reached the expected progress.</returns>
        public static async Task AssertRecoveredInventoryDagCompletedAsync(
            ITestOutputHelper output,
            IAiDagExecutionStore dagStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            RealRuntimeCrashFailedRuntimeRecoveryProof proof,
            int expectedCompletedStepCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(proof);
            ArgumentNullException.ThrowIfNull(proof.FailedInventory);
            ArgumentNullException.ThrowIfNull(proof.RecoveredWorks);

            var completedInFlightExecutionCount =
                0;

            var completedRecoveredLocalQueuedCount =
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

                    var recoveredExecutionId =
                        recovered.RecoveredExecutionId;

                    if (string.IsNullOrWhiteSpace(recoveredExecutionId))
                    {
                        var runtimeIndexEntry =
                            await ProductionRecoveryWaitHelpers
                                .WaitForRuntimeIndexWithExecutionIdAsync(
                                    runExecutionIndex,
                                    recovered.ReplacementLocalRunId,
                                    timeout)
                                .ConfigureAwait(false);

                        recoveredExecutionId =
                            runtimeIndexEntry.ExecutionId;
                    }

                    Assert.False(
                        string.IsNullOrWhiteSpace(recoveredExecutionId),
                        $"Recovered local queued work must eventually expose a durable execution id. SharedRunId='{recovered.Original.SharedRunId}', ReplacementRuntimeInstanceId='{recovered.ReplacementRuntimeInstanceId}', ReplacementLocalRunId='{recovered.ReplacementLocalRunId}'.");

                    await ProductionRecoveryWaitHelpers
                        .WaitForDagCompletedStepCountAsync(
                            dagStore,
                            recoveredExecutionId,
                            expectedCompletedStepCount,
                            timeout)
                        .ConfigureAwait(false);

                    completedRecoveredLocalQueuedCount++;

                    output.WriteLine(
                        $"[REAL RUNTIME INVENTORY COMPLETION] Recovered local queued DAG execution completed. TenantId='{proof.FailedInventory.Tenant.TenantId}', SharedRunId='{recovered.Original.SharedRunId}', FailedLocalRunId='{recovered.Original.LocalRunId}', ReplacementRuntimeInstanceId='{recovered.ReplacementRuntimeInstanceId}', ReplacementLocalRunId='{recovered.ReplacementLocalRunId}', ExecutionId='{recoveredExecutionId}', CompletedSteps='{expectedCompletedStepCount}'.");

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
                completedRecoveredLocalQueuedCount);
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

            AssertNoDuplicateCrossTenantInventoryRecovery(proofs);
        }

        /// <summary>
        /// Asserts that multiple failed-runtime recoveries did not leak work across tenant boundaries
        /// using an optional authoritative runtime tenant ownership assertion.
        /// </summary>
        /// <param name="registry">The authoritative runtime instance registry.</param>
        /// <param name="proofs">The failed-runtime recovery proofs.</param>
        /// <param name="runtimeTenantOwnershipAssertion">The optional authoritative runtime tenant ownership assertion. When omitted, the historical runtime-id naming assertion is preserved.</param>
        /// <returns>A task that completes when tenant ownership and duplicate recovery have been validated.</returns>
        public static async Task AssertNoCrossTenantInventoryRecoveryLeakAsync(
            IAiRuntimeInstanceRegistry registry,
            IReadOnlyCollection<RealRuntimeCrashFailedRuntimeRecoveryProof> proofs,
            Func<IAiRuntimeInstanceRegistry, string, ProductionTenantScenarioDefinition, Task>?
                runtimeTenantOwnershipAssertion = null)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(proofs);

            foreach (var proof in proofs)
            {
                foreach (var recoveredWork in proof.RecoveredWorks)
                {
                    await AssertRuntimeBelongsToTenantAsync(
                            registry,
                            recoveredWork.ReplacementRuntimeInstanceId,
                            proof.FailedInventory.Tenant,
                            runtimeTenantOwnershipAssertion)
                        .ConfigureAwait(false);
                }
            }

            AssertNoDuplicateCrossTenantInventoryRecovery(proofs);
        }

        private static void AssertNoDuplicateCrossTenantInventoryRecovery(
            IReadOnlyCollection<RealRuntimeCrashFailedRuntimeRecoveryProof> proofs)
        {
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

        private static Task AssertRuntimeBelongsToTenantAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant,
            Func<IAiRuntimeInstanceRegistry, string, ProductionTenantScenarioDefinition, Task>?
                runtimeTenantOwnershipAssertion)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(tenant);

            if (runtimeTenantOwnershipAssertion is not null)
            {
                return runtimeTenantOwnershipAssertion(
                    registry,
                    runtimeInstanceId,
                    tenant);
            }

            AssertRuntimeBelongsToTenant(
                runtimeInstanceId,
                tenant);

            return Task.CompletedTask;
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
    IAiDagExecutionStore dagStore,
    RealRuntimeCrashAssignedWorkInventoryProof inventory,
    RealRuntimeCrashWorkProof work,
    RealRuntimeCrashWorkStateSnapshot preKillSnapshot,
    RealRuntimeCrashWorkStateSnapshot postKillSnapshot,
    DateTimeOffset killRequestedAtUtc,
    DateTimeOffset killCompletedAtUtc,
    TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(work);
            ArgumentNullException.ThrowIfNull(preKillSnapshot);
            ArgumentNullException.ThrowIfNull(postKillSnapshot);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRunExecutionIndexEntry? lastEntry =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(work.LocalRunId)
                        .ConfigureAwait(false);

                var runtimeMatches =
                    string.Equals(
                        lastEntry?.RuntimeInstanceId,
                        inventory.RuntimeInstanceId,
                        StringComparison.Ordinal);

                var executionMatches =
                    string.IsNullOrWhiteSpace(work.ExecutionId) ||
                    string.Equals(
                        lastEntry?.ExecutionId,
                        work.ExecutionId,
                        StringComparison.Ordinal);

                var statusMatches =
                    work.Kind == RealRuntimeCrashWorkKind.InFlightExecution
                        ? string.Equals(
                            lastEntry?.Status,
                            "requeued-for-recovery",
                            StringComparison.OrdinalIgnoreCase)
                        : string.Equals(
                              lastEntry?.Status,
                              "queued",
                              StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(
                              lastEntry?.Status,
                              "requeued-for-recovery",
                              StringComparison.OrdinalIgnoreCase);

                if (statusMatches &&
                    runtimeMatches &&
                    executionMatches)
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            string? dagStatus =
                null;

            var completedStepCount =
                0;

            var totalStepCount =
                0;

            var stepStatusBreakdown =
                string.Empty;

            if (work.Kind == RealRuntimeCrashWorkKind.InFlightExecution &&
                !string.IsNullOrWhiteSpace(work.ExecutionId))
            {
                var dagRecord =
                    await dagStore
                        .GetRecordAsync(work.ExecutionId)
                        .ConfigureAwait(false);

                var dagState =
                    await dagStore
                        .GetStateAsync(work.ExecutionId)
                        .ConfigureAwait(false);

                dagStatus =
                    dagRecord?.Status.ToString();

                if (dagState is not null)
                {
                    completedStepCount =
                        dagState.Steps.Values.Count(step =>
                            step.Status == AiStepExecutionStatus.Completed);

                    totalStepCount =
                        dagState.Steps.Count;

                    stepStatusBreakdown =
                        string.Join(
                            ",",
                            dagState.Steps.Values
                                .GroupBy(step => step.Status)
                                .OrderBy(group => group.Key)
                                .Select(group =>
                                    $"{group.Key}:{group.Count()}"));
                }
            }

            var completionTiming =
                ClassifyCompletionTiming(
                    postKillSnapshot.IndexCompletedAtUtc,
                    killRequestedAtUtc,
                    killCompletedAtUtc);

            Assert.Fail(
                "Runtime work was not observed in a recoverable pre-redispatch state within the timeout. " +
                $"TenantId='{inventory.Tenant.TenantId}', " +
                $"FailedRuntimeInstanceId='{inventory.RuntimeInstanceId}', " +
                $"SharedRunId='{work.SharedRunId}', " +
                $"LocalRunId='{work.LocalRunId}', " +
                $"Kind='{work.Kind}', " +
                $"ExpectedExecutionId='{work.ExecutionId}', " +

                $"LastStatus='{lastEntry?.Status}', " +
                $"LastRuntimeInstanceId='{lastEntry?.RuntimeInstanceId}', " +
                $"LastExecutionId='{lastEntry?.ExecutionId}', " +
                $"LastIndexCompletedAtUtc='{lastEntry?.CompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"DagStatus='{dagStatus}', " +
                $"DagCompletedStepCount='{completedStepCount}', " +
                $"DagTotalStepCount='{totalStepCount}', " +
                $"DagStepStatusBreakdown='{stepStatusBreakdown}', " +

                $"PreKillIndexStatus='{preKillSnapshot.IndexStatus}', " +
                $"PreKillIndexRuntimeInstanceId='{preKillSnapshot.RuntimeInstanceId}', " +
                $"PreKillIndexExecutionId='{preKillSnapshot.ExecutionId}', " +
                $"PreKillIndexCompletedAtUtc='{preKillSnapshot.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"PreKillDagStatus='{preKillSnapshot.DagStatus}', " +
                $"PreKillCompletedSteps='{preKillSnapshot.DagCompletedStepCount}', " +
                $"PreKillTotalSteps='{preKillSnapshot.DagTotalStepCount}', " +
                $"PreKillDagStepStatusBreakdown='{preKillSnapshot.DagStepStatusBreakdown}', " +
                $"PreKillCapturedAtUtc='{preKillSnapshot.CapturedAtUtc:O}', " +

                $"PostKillIndexStatus='{postKillSnapshot.IndexStatus}', " +
                $"PostKillIndexRuntimeInstanceId='{postKillSnapshot.RuntimeInstanceId}', " +
                $"PostKillIndexExecutionId='{postKillSnapshot.ExecutionId}', " +
                $"PostKillIndexCompletedAtUtc='{postKillSnapshot.IndexCompletedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"PostKillDagStatus='{postKillSnapshot.DagStatus}', " +
                $"PostKillCompletedSteps='{postKillSnapshot.DagCompletedStepCount}', " +
                $"PostKillTotalSteps='{postKillSnapshot.DagTotalStepCount}', " +
                $"PostKillDagStepStatusBreakdown='{postKillSnapshot.DagStepStatusBreakdown}', " +
                $"PostKillCapturedAtUtc='{postKillSnapshot.CapturedAtUtc:O}', " +

                $"KillRequestedAtUtc='{killRequestedAtUtc:O}', " +
                $"KillCompletedAtUtc='{killCompletedAtUtc:O}', " +
                $"KillDuration='{killCompletedAtUtc - killRequestedAtUtc}', " +
                $"CompletionTiming='{completionTiming}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
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

        private static async Task<IReadOnlyDictionary<string, RealRuntimeCrashWorkStateSnapshot>>
    CaptureWorkStateSnapshotsAsync(
        IAiRuntimeRunExecutionIndex runExecutionIndex,
        IAiDagExecutionStore dagStore,
        RealRuntimeCrashAssignedWorkInventoryProof inventory,
        string? captureLastLocalRunId = null)
        {
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(inventory);

            var snapshots =
                new Dictionary<string, RealRuntimeCrashWorkStateSnapshot>(
                    StringComparer.Ordinal);

            var orderedWorks =
                inventory.Works
                    .OrderBy(work =>
                        string.Equals(
                            work.LocalRunId,
                            captureLastLocalRunId,
                            StringComparison.Ordinal)
                            ? 1
                            : 0)
                    .ToArray();

            foreach (var work in orderedWorks)
            {
                string? dagStatus =
                    null;

                var completedStepCount =
                    0;

                var totalStepCount =
                    0;

                var statusBreakdown =
                    string.Empty;

                if (!string.IsNullOrWhiteSpace(work.ExecutionId))
                {
                    /*
                     * Read DAG state first because it can change while the execution
                     * is progressing.
                     */
                    var dagState =
                        await dagStore
                            .GetStateAsync(work.ExecutionId)
                            .ConfigureAwait(false);

                    if (dagState is not null)
                    {
                        completedStepCount =
                            dagState.Steps.Values.Count(step =>
                                step.Status == AiStepExecutionStatus.Completed);

                        totalStepCount =
                            dagState.Steps.Count;

                        statusBreakdown =
                            string.Join(
                                ",",
                                dagState.Steps.Values
                                    .GroupBy(step => step.Status)
                                    .OrderBy(group => group.Key)
                                    .Select(group =>
                                        $"{group.Key}:{group.Count()}"));
                    }

                    /*
                     * Read the durable DAG record after the detailed state.
                     */
                    var dagRecord =
                        await dagStore
                            .GetRecordAsync(work.ExecutionId)
                            .ConfigureAwait(false);

                    dagStatus =
                        dagRecord?.Status.ToString();
                }

                /*
                 * Read the runtime index last so its lifecycle status is the freshest
                 * observation stored in the diagnostic snapshot.
                 */
                var indexEntry =
                    await runExecutionIndex
                        .GetAsync(work.LocalRunId)
                        .ConfigureAwait(false);

                snapshots[work.LocalRunId] =
                    new RealRuntimeCrashWorkStateSnapshot(
                        indexEntry?.Status,
                        indexEntry?.RuntimeInstanceId,
                        indexEntry?.ExecutionId,
                        indexEntry?.CompletedAtUtc,
                        dagStatus,
                        completedStepCount,
                        totalStepCount,
                        statusBreakdown,
                        DateTimeOffset.UtcNow);
            }

            return snapshots;
        }

        private static string ClassifyCompletionTiming(
            DateTimeOffset? completedAtUtc,
            DateTimeOffset killRequestedAtUtc,
            DateTimeOffset killCompletedAtUtc)
        {
            if (completedAtUtc is null)
            {
                return "not-completed";
            }

            if (completedAtUtc < killRequestedAtUtc)
            {
                return "before-kill-request";
            }

            if (completedAtUtc <= killCompletedAtUtc)
            {
                return "during-kill";
            }

            return "after-kill-return";
        }

        /// <summary>
        /// Reads the first targeted shared-run dispatch signal for the requested shared run.
        /// </summary>
        private static async Task<AiRuntimeSignal> ReadRequiredSharedRunDispatchedSignalAsync(
            IAiRuntimeSignalSubscription subscription,
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            await foreach (var signal in subscription
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (signal.Type != AiRuntimeSignalType.SharedRunDispatched ||
                    !string.Equals(
                        signal.ControlPlaneId,
                        controlPlaneId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        signal.SharedRunId,
                        sharedRunId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                return signal;
            }

            throw new InvalidOperationException(
                "The targeted shared-run dispatch signal subscription completed unexpectedly.");
        }

        private sealed record RealRuntimeCrashKillObservation(
            RealRuntimeCrashWorkStateSnapshot? ObservedCrashSnapshot,
            RealRuntimeCrashWorkStateSnapshot? LastObservedInFlightSnapshot,
            bool Killed,
            DateTimeOffset KillRequestedAtUtc,
            DateTimeOffset KillCompletedAtUtc,
            string ProgressWakeSource,
            int FallbackReadCount,
            AiRuntimeSignal? ProgressSignal);

        private sealed record RealRuntimeCrashWorkStateSnapshot(
            string? IndexStatus,
            string? RuntimeInstanceId,
            string? ExecutionId,
            DateTimeOffset? IndexCompletedAtUtc,
            string? DagStatus,
            int DagCompletedStepCount,
            int DagTotalStepCount,
            string DagStepStatusBreakdown,
            DateTimeOffset CapturedAtUtc);
    }
}