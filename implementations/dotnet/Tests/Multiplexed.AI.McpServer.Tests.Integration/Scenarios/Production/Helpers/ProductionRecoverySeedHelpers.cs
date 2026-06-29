using ModelContextProtocol.Protocol;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.Stores;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable seed helpers for production runtime recovery scenarios.
    /// </summary>
    public static class ProductionRecoverySeedHelpers
    {
        /// <summary>
        /// Seeds durable shared queue ownership and runtime execution index for recovery.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="runtimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="localRunId">The failed local runtime run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <returns>A task that completes when the recovery seed data has been persisted.</returns>
        public static async Task SeedInFlightRuntimeExecutionAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            AiSharedRunRecord sharedRun,
            string runtimeInstanceId,
            string localRunId,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            await sharedRunStore
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    runtimeInstanceId,
                    localRunId,
                    executionId,
                    reason: "http-dag-resume-recovery-seed")
                .ConfigureAwait(false);

            var queueItem =
                await sharedQueue
                    .GetAsync(sharedRun.SharedRunId)
                    .ConfigureAwait(false);

            if (queueItem is null)
            {
                await sharedQueue
                    .EnqueueAsync(new AiSharedQueueItem
                    {
                        SharedRunId = sharedRun.SharedRunId,
                        Status = AiSharedQueueItemStatus.Pending,
                        ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                        PipelineKey = sharedRun.PipelineKey,
                        Priority = 0,
                        EnqueuedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = "http-dag-resume-recovery",
                            ["seeded"] = "true"
                        }
                    })
                    .ConfigureAwait(false);

                queueItem =
                    await sharedQueue
                        .GetAsync(sharedRun.SharedRunId)
                        .ConfigureAwait(false);
            }

            Assert.NotNull(queueItem);

            if (queueItem!.Status != AiSharedQueueItemStatus.Dispatched ||
                string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                var claim =
                    await sharedQueue
                        .ClaimNextAsync(new AiSharedQueueClaimRequest
                        {
                            RuntimeInstanceId = runtimeInstanceId,
                            WorkerId = "http-dag-resume-recovery-seed-worker",
                            TenantId = sharedRun.ExecutionContextSnapshot?.TenantId,
                            PipelineKey = sharedRun.PipelineKey,
                            ClaimTtl = TimeSpan.FromMinutes(5),
                            Reason = "http-dag-resume-recovery-seed-claim"
                        })
                        .ConfigureAwait(false);

                Assert.NotNull(claim);
                Assert.Equal(sharedRun.SharedRunId, claim!.SharedRunId);
                Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));

                await sharedQueue
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        claim.ClaimToken!,
                        reason: "http-dag-resume-recovery-seed-dispatch")
                    .ConfigureAwait(false);
            }

            await runExecutionIndex
                .RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = localRunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = runtimeInstanceId,
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "http-dag-resume-recovery",
                        ["seeded"] = "true"
                    }
                })
                .ConfigureAwait(false);

            await runExecutionIndex
                .MarkStartedAsync(
                    localRunId,
                    executionId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Seeds a durable DAG state that has completed all steps before the failure point
        /// and has the failure step running with an expired lease.
        /// </summary>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="definition">The optional runtime-generated pipeline definition.</param>
        /// <param name="contextKey">The RBAC context key to persist on the durable execution record.</param>
        /// <param name="stepCount">The total number of DAG steps.</param>
        /// <param name="failureStepNumber">The one-based failure step number.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <returns>A task that completes when the durable DAG state has been seeded.</returns>
        public static async Task SeedDurableDagStoppedAtStepAsync(
            IAiDagExecutionStore dagStore,
            string executionId,
            string pipelineName,
            AiPipelineDefinition? definition,
            string? contextKey,
            int stepCount,
            int failureStepNumber,
            string failedRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            Assert.False(
                string.IsNullOrWhiteSpace(contextKey),
                "The seeded DAG execution record must carry the RBAC ContextKey from the shared run snapshot.");

            var stepNames =
                ResolveStepNames(
                    definition,
                    stepCount);

            Assert.Equal(stepCount, stepNames.Count);

            var record =
                new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = pipelineName,
                    ContextKey = contextKey!,
                    ExecutionMode = AiExecutionMode.Dag,
                    Status = AiExecutionStatus.Running,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                };

            for (var stepNumber = 1; stepNumber < failureStepNumber; stepNumber++)
            {
                record.CompletedSteps.Add(stepNames[stepNumber - 1]);
            }

            var state =
                new AiExecutionState
                {
                    ExecutionId = executionId,
                    PipelineName = pipelineName
                };

            for (var stepNumber = 1; stepNumber <= stepCount; stepNumber++)
            {
                var stepName =
                    stepNames[stepNumber - 1];

                var dependsOn =
                    ResolveStepDependencies(
                        definition,
                        stepName,
                        stepNumber);

                var step =
                    new AiStepState
                    {
                        StepName = stepName,
                        DependsOn = dependsOn,
                        ClaimTimeoutSeconds = 30,
                        Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                        Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                    };

                if (stepNumber < failureStepNumber)
                {
                    step.Status = AiStepExecutionStatus.Completed;
                    step.StartedAtUtc = DateTime.UtcNow.AddMinutes(-5);
                    step.CompletedAtUtc = DateTime.UtcNow.AddMinutes(-4);
                    step.UpdatedAtUtc = step.CompletedAtUtc;
                    step.Duration = TimeSpan.FromMinutes(1);
                    step.Result = CreateSeededCompletedStepResult(
                        executionId,
                        pipelineName,
                        stepName,
                        stepNumber,
                        failedRuntimeInstanceId);
                    step.Error = null;
                    step.ClaimedBy = null;
                    step.ClaimToken = null;
                    step.ClaimedAtUtc = null;
                    step.LeaseExpiresAtUtc = null;
                    step.IsCompacted = false;
                    step.CompactedAtUtc = null;
                    step.ArchivePayloadId = null;
                    step.CompactionReason = null;
                }
                else if (stepNumber == failureStepNumber)
                {
                    step.Status = AiStepExecutionStatus.Running;
                    step.ClaimedBy = $"{failedRuntimeInstanceId}:worker-old";
                    step.ClaimToken = $"claim-token-{Guid.NewGuid():N}";
                    step.ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10);
                    step.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-9);
                    step.StartedAtUtc = DateTime.UtcNow.AddMinutes(-10);
                    step.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-9);
                    step.RecoveryCount = 0;
                }
                else
                {
                    step.Status = AiStepExecutionStatus.Ready;
                    step.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
                }

                state.Steps[stepName] = step;
            }

            await dagStore
                .CreateAsync(
                    record,
                    state)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves step names from the runtime-generated pipeline definition.
        /// </summary>
        /// <param name="definition">The optional runtime-generated pipeline definition.</param>
        /// <param name="stepCount">The expected step count.</param>
        /// <returns>The ordered step names.</returns>
        public static IReadOnlyList<string> ResolveStepNames(
            AiPipelineDefinition? definition,
            int stepCount)
        {
            if (definition is not null &&
                definition.Steps.Count == stepCount)
            {
                return definition.Steps
                    .OrderBy(step => step.Order)
                    .Select(step => step.Name)
                    .ToArray();
            }

            return Enumerable
                .Range(1, stepCount)
                .Select(FormatStepName)
                .ToArray();
        }

        /// <summary>
        /// Resolves step dependencies from the generated definition, falling back to a linear DAG.
        /// </summary>
        /// <param name="definition">The optional runtime-generated pipeline definition.</param>
        /// <param name="stepName">The step name.</param>
        /// <param name="stepNumber">The one-based step number.</param>
        /// <returns>The resolved dependency step names.</returns>
        public static List<string> ResolveStepDependencies(
            AiPipelineDefinition? definition,
            string stepName,
            int stepNumber)
        {
            var definitionStep =
                definition?.Steps.FirstOrDefault(step =>
                    string.Equals(step.Name, stepName, StringComparison.Ordinal));

            if (definitionStep is not null)
            {
                return definitionStep.DependsOn.ToList();
            }

            if (stepNumber == 1)
            {
                return new List<string>();
            }

            return new List<string>
            {
                FormatStepName(stepNumber - 1)
            };
        }

        /// <summary>
        /// Seeds a durable failed-runtime assigned-work inventory without faking recovery results.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="templateSharedRun">The template shared run used to copy request and context data.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="queuedLocalRunCount">The number of locally queued runs to seed.</param>
        /// <param name="inFlightExecutionCount">The number of in-flight executions to seed.</param>
        /// <param name="stepCount">The DAG step count.</param>
        /// <param name="failureStepNumber">The DAG failure step number.</param>
        /// <returns>The seeded failed-runtime work inventory.</returns>
        public static async Task<IReadOnlyList<FailedRuntimeWorkSeed>> SeedFailedRuntimeAssignedWorkInventoryAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            AiSharedRunRecord templateSharedRun,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string failedRuntimeInstanceId,
            int queuedLocalRunCount,
            int inFlightExecutionCount,
            int stepCount,
            int failureStepNumber,
            string requestedBy,
            string source)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegative(queuedLocalRunCount);
            ArgumentOutOfRangeException.ThrowIfNegative(inFlightExecutionCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureStepNumber);

            if (failureStepNumber > stepCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failureStepNumber),
                    $"Failure step number must be less than or equal to the DAG step count. FailureStepNumber='{failureStepNumber}', StepCount='{stepCount}'.");
            }

            var seeded =
                new List<FailedRuntimeWorkSeed>();

            for (var index = 1; index <= queuedLocalRunCount; index++)
            {
                var sharedRunId =
                    Guid.NewGuid().ToString("N");

                var localRunId =
                    $"failed-local-queued-{index}-{Guid.NewGuid():N}";

                var sharedRun =
                    await CreateDurableSharedRunForFailedRuntimeAsync(
                            sharedRunStore,
                            templateSharedRun,
                            tenant,
                            pipelineName,
                            sharedRunId,
                            requestedBy,
                            source,
                            reason: "seeded-local-queued-work")
                        .ConfigureAwait(false);

                await SeedDurableAssignedQueueOwnershipAsync(
                        sharedQueue,
                        sharedRun,
                        failedRuntimeInstanceId,
                        reason: "seeded-local-queued-work")
                    .ConfigureAwait(false);

                await sharedRunStore
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        failedRuntimeInstanceId,
                        localRunId,
                        executionId: null,
                        reason: "seeded-local-queued-work")
                    .ConfigureAwait(false);

                await runExecutionIndex
                    .RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = localRunId,
                        ExecutionId = null,
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        Status = "queued",
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = "http-runtime-recovery-inventory",
                            ["inventory.kind"] = "local-queued",
                            ["seeded"] = "true"
                        }
                    })
                    .ConfigureAwait(false);

                seeded.Add(new FailedRuntimeWorkSeed
                {
                    SharedRunId = sharedRun.SharedRunId,
                    FailedLocalRunId = localRunId,
                    ExecutionId = null,
                    Kind = FailedRuntimeWorkKind.LocalQueued
                });
            }

            for (var index = 1; index <= inFlightExecutionCount; index++)
            {
                var sharedRunId =
                    Guid.NewGuid().ToString("N");

                var localRunId =
                    $"failed-local-running-{index}-{Guid.NewGuid():N}";

                var executionId =
                    $"http-runtime-inventory-running-execution-{index}-{Guid.NewGuid():N}";

                var sharedRun =
                    await CreateDurableSharedRunForFailedRuntimeAsync(
                            sharedRunStore,
                            templateSharedRun,
                            tenant,
                            pipelineName,
                            sharedRunId,
                            requestedBy,
                            source,
                            reason: "seeded-in-flight-execution")
                        .ConfigureAwait(false);

                await SeedInFlightRuntimeExecutionAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        sharedRun,
                        failedRuntimeInstanceId,
                        localRunId,
                        executionId)
                    .ConfigureAwait(false);

                await SeedDurableDagStoppedAtStepAsync(
                        dagStore,
                        executionId,
                        pipelineName,
                        sharedRun.RunRequest?.PipelineDefinition,
                        sharedRun.ExecutionContextSnapshot?.ContextKey,
                        stepCount,
                        failureStepNumber,
                        failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                seeded.Add(new FailedRuntimeWorkSeed
                {
                    SharedRunId = sharedRun.SharedRunId,
                    FailedLocalRunId = localRunId,
                    ExecutionId = executionId,
                    Kind = FailedRuntimeWorkKind.InFlightExecution
                });
            }

            return seeded;
        }

        /// <summary>
        /// Formats a stable fallback step name.
        /// </summary>
        /// <param name="stepNumber">The one-based step number.</param>
        /// <returns>The formatted step name.</returns>
        public static string FormatStepName(
            int stepNumber)
        {
            return $"step-{stepNumber:000}";
        }

        /// <summary>
        /// Creates a durable synthetic result for a pre-crash completed seeded step.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="stepName">The step name.</param>
        /// <param name="stepNumber">The one-based step number.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <returns>The seeded completed step result.</returns>
        private static AiStepResult CreateSeededCompletedStepResult(
            string executionId,
            string pipelineName,
            string stepName,
            int stepNumber,
            string failedRuntimeInstanceId)
        {
            var result =
                new AiStepResult();

            result.Data["seeded"] = true;
            result.Data["seeded.reason"] = "pre-crash-completed-step";
            result.Data["executionId"] = executionId;
            result.Data["pipelineName"] = pipelineName;
            result.Data["stepName"] = stepName;
            result.Data["stepNumber"] = stepNumber;
            result.Data["failedRuntimeInstanceId"] = failedRuntimeInstanceId;

            return result;
        }

        /// <summary>
        /// Creates a durable shared run record used as crash-surviving ownership evidence.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="templateSharedRun">The template shared run used to copy request and context data.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="requestedBy">The requester identifier.</param>
        /// <param name="source">The source identifier.</param>
        /// <param name="reason">The creation reason.</param>
        /// <returns>The created shared run record.</returns>
        private static async Task<AiSharedRunRecord> CreateDurableSharedRunForFailedRuntimeAsync(
            IAiSharedRunStore sharedRunStore,
            AiSharedRunRecord templateSharedRun,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string sharedRunId,
            string requestedBy,
            string source,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            var record =
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = templateSharedRun.RunRequest,
                    ExecutionContextSnapshot = templateSharedRun.ExecutionContextSnapshot,
                    LocalRunId = null,
                    ExecutionId = null,
                    AssignedRuntimeInstanceId = null,
                    AdmissionDecision = null,
                    PipelineKey = pipelineName,
                    CorrelationId = $"correlation-{sharedRunId}",
                    RequestedBy = requestedBy,
                    Source = source,
                    Reason = reason,
                    FailureReason = null,
                    SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                        ["pipelineName"] = pipelineName,
                        ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix,
                        ["scenario"] = "http-runtime-recovery-inventory",
                        ["seeded"] = "true"
                    },
                    ControlPlaneId = templateSharedRun.ControlPlaneId
                };

            return await sharedRunStore
                .CreateAsync(record)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Seeds durable shared queue ownership for a failed runtime local queued item.
        /// </summary>
        private static async Task SeedDurableAssignedQueueOwnershipAsync(
            IAiSharedQueue sharedQueue,
            AiSharedRunRecord sharedRun,
            string failedRuntimeInstanceId,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(sharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            await sharedQueue
                .EnqueueAsync(new AiSharedQueueItem
                {
                    SharedRunId = sharedRun.SharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                    PipelineKey = sharedRun.PipelineKey,
                    Priority = 0,
                    EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "http-runtime-recovery-inventory",
                        ["inventory.kind"] = "local-queued",
                        ["seeded"] = "true"
                    }
                })
                .ConfigureAwait(false);

            var claim =
                await sharedQueue
                    .ClaimNextAsync(new AiSharedQueueClaimRequest
                    {
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        WorkerId = "http-runtime-inventory-seed-worker",
                        TenantId = sharedRun.ExecutionContextSnapshot?.TenantId,
                        PipelineKey = sharedRun.PipelineKey,
                        ClaimTtl = TimeSpan.FromMinutes(5),
                        Reason = reason
                    })
                    .ConfigureAwait(false);

            Assert.NotNull(claim);
            Assert.Equal(sharedRun.SharedRunId, claim!.SharedRunId);
            Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));

            await sharedQueue
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    claim.ClaimToken!,
                    reason: reason)
                .ConfigureAwait(false);
        }
    }
}