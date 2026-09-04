using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    /// <summary>
    /// Projects authoritative Child DAG relations plus bounded child workflow
    /// state into the demo DTO. The runtime stores remain the source of truth.
    /// </summary>
    public sealed class RuntimeAnalysisChildDagEvidenceReader
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IAiDagExecutionStore _dagStore;
        private readonly IAiChildExecutionRelationStore _relationStore;
        private readonly IRuntimeAnalysisHumanApprovalStore _approvalStore;
        private readonly IRuntimeAnalysisScenarioExecutionStore _executionStore;

        public RuntimeAnalysisChildDagEvidenceReader(
            IAiDagExecutionStore dagStore,
            IAiChildExecutionRelationStore relationStore,
            IRuntimeAnalysisHumanApprovalStore approvalStore,
            IRuntimeAnalysisScenarioExecutionStore executionStore)
        {
            _dagStore = dagStore ?? throw new ArgumentNullException(nameof(dagStore));
            _relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
            _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        }

        public async Task<RuntimeAnalysisChildDagResult> ReadAsync(
            AiExecutionState state,
            string executionId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName,
                    out var childStep)
                || childStep.Status == AiStepExecutionStatus.None)
            {
                return NotStarted();
            }

            var record = await _dagStore
                .GetRecordAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis execution '{executionId}' has no persisted execution record.");

            var tenantId = record.ExecutionContextSnapshot?.TenantId;

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis execution '{executionId}' does not contain the durable TenantId required to read Child DAG relations.");
            }

            var relations = await ReadApprovalDrivenRelationsAsync(
                    tenantId,
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            return BuildResult(
                childStep.Status,
                relations);
        }

        private async Task<IReadOnlyList<RuntimeAnalysisChildDagRelationResult>>
            ReadApprovalDrivenRelationsAsync(
                string tenantId,
                string rootExecutionId,
                CancellationToken cancellationToken)
        {
            var results = new List<RuntimeAnalysisChildDagRelationResult>();
            var currentParentExecutionId = rootExecutionId;

            for (var depth = 1;
                 depth <= RuntimeAnalysisChildDagDefinitionFactory.MaxProjectedApprovalDepth;
                 depth++)
            {
                var identity = new AiChildInvocationIdentity
                {
                    TenantId = tenantId,
                    ParentExecutionId = currentParentExecutionId,
                    ParentCallSiteId = RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName,
                    ChildDagId = RuntimeAnalysisChildDagDefinitionFactory.CreateChildPipelineName(depth),
                    ChildDagDefinitionVersion = RuntimeAnalysisChildDagDefinitionFactory.PipelineVersion,
                    CanonicalLogicalInvocationKey = RuntimeAnalysisChildDagDefinitionFactory.CreateChildLogicalInvocationKey(depth),
                    InvocationGeneration = 0
                };

                var relation = await _relationStore
                    .GetAsync(
                        identity,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (relation is null)
                {
                    break;
                }

                results.Add(
                    await MapRelationAsync(
                            relation,
                            depth,
                            cancellationToken)
                        .ConfigureAwait(false));

                if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
                {
                    break;
                }

                currentParentExecutionId = relation.ChildExecutionId;
            }

            return results;
        }

        private async Task<RuntimeAnalysisChildDagRelationResult> MapRelationAsync(
            AiChildExecutionRelation relation,
            int depth,
            CancellationToken cancellationToken)
        {
            var projection = await ReadChildProjectionAsync(
                    relation.ChildExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeAnalysisChildDagRelationResult
            {
                Depth = depth,
                TenantId = relation.TenantId,
                ParentExecutionId = relation.ParentExecutionId,
                ChildExecutionId = relation.ChildExecutionId,
                ChildInvocationKey = relation.ChildInvocationKey,
                ChildDagId = relation.ChildDagId,
                ChildDagDefinitionVersion = relation.ChildDagDefinitionVersion,
                InvocationGeneration = relation.InvocationGeneration,
                RelationStatus = relation.Status.ToString(),
                ContinuationStatus = relation.ContinuationStatus.ToString(),
                ChildResultDigest = relation.ChildResult?.ContentHash,
                ChildFailureReason = relation.ChildFailureReason,
                CreatedAtUtc = relation.CreatedAtUtc,
                CompletedAtUtc = relation.CompletedAtUtc,
                ParentResumedAtUtc = relation.ParentResumedAtUtc,
                RuntimeStatus = projection.RuntimeStatus,
                CurrentStep = projection.CurrentStep,
                InvestigationMode = projection.InvestigationMode,
                Reanalysis = projection.Reanalysis,
                PolicyValidation = projection.PolicyValidation,
                HumanApproval = projection.HumanApproval,
                ScenarioExecution = projection.ScenarioExecution,
                Verification = projection.Verification
            };
        }

        private async Task<ChildProjection> ReadChildProjectionAsync(
            string? childExecutionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(childExecutionId))
            {
                return ChildProjection.Empty;
            }

            var state = await _dagStore
                .GetStateAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var record = await _dagStore
                .GetRecordAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (state is null || record is null)
            {
                return ChildProjection.Empty;
            }

            var investigationMode =
                ReadInvestigationMode(
                    state);

            var reanalysis = TryReadCompletedOutput<RuntimeAnalysisReanalysisResult>(
                state,
                RuntimeAnalysisChildDagDefinitionFactory.ReanalysisStepName);

            var policyValidation = TryReadCompletedOutput<RuntimeAnalysisScenarioPolicyValidationResult>(
                state,
                RuntimeAnalysisChildDagDefinitionFactory.ValidateReanalysisStepName);

            var humanApproval = await ReadChildApprovalAsync(
                    state,
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var scenarioExecution = await ReadChildScenarioExecutionAsync(
                    state,
                    childExecutionId,
                    policyValidation,
                    cancellationToken)
                .ConfigureAwait(false);

            var verification = ReadChildVerification(
                state,
                scenarioExecution);

            return new ChildProjection
            {
                RuntimeStatus = record.Status.ToString(),
                CurrentStep = ResolveChildCurrentStep(state),
                InvestigationMode = investigationMode,
                Reanalysis = reanalysis,
                PolicyValidation = policyValidation,
                HumanApproval = humanApproval,
                ScenarioExecution = scenarioExecution,
                Verification = verification
            };
        }

        private static string ReadInvestigationMode(
            AiExecutionState state)
        {
            if (!state.Data.TryGetValue(
                    RuntimeAnalysisStepInputKeys.ProviderRequestJson,
                    out var raw)
                || raw is null)
            {
                return RuntimeAnalysisInvestigationModes.StopWhenConclusive;
            }

            string? json = raw switch
            {
                string value => value,
                JsonElement element
                    when element.ValueKind == JsonValueKind.String =>
                    element.GetString(),
                JsonElement element =>
                    element.GetRawText(),
                _ => JsonSerializer.Serialize(
                    raw)
            };

            if (string.IsNullOrWhiteSpace(
                    json))
            {
                return RuntimeAnalysisInvestigationModes.StopWhenConclusive;
            }

            try
            {
                var request =
                    JsonSerializer.Deserialize<RuntimeAnalysisProviderRequest>(
                        json,
                        SerializerOptions);

                return request is not null
                       && RuntimeAnalysisInvestigationModes.IsSupported(
                           request.InvestigationMode)
                    ? request.InvestigationMode
                    : RuntimeAnalysisInvestigationModes.StopWhenConclusive;
            }
            catch (JsonException)
            {
                return RuntimeAnalysisInvestigationModes.StopWhenConclusive;
            }
        }

        private async Task<RuntimeAnalysisHumanApprovalResult?> ReadChildApprovalAsync(
            AiExecutionState state,
            string childExecutionId,
            CancellationToken cancellationToken)
        {
            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisChildDagDefinitionFactory.AwaitHumanApprovalStepName,
                    out var step)
                || step.Status == AiStepExecutionStatus.None)
            {
                return null;
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                var record = await _approvalStore.GetAsync(
                        childExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (record is null)
                {
                    return null;
                }

                return new RuntimeAnalysisHumanApprovalResult
                {
                    Required = true,
                    Status = record.Status,
                    ContinuationId = record.ContinuationId,
                    RequestedAtUtc = record.RequestedAtUtc,
                    DecidedAtUtc = record.DecidedAtUtc,
                    DecidedBy = record.DecidedBy,
                    Message =
                        "Child re-analysis crossed deterministic policy. Explicit human approval is required before another experiment can run."
                };
            }

            return step.Status == AiStepExecutionStatus.Completed
                ? DeserializeStepOutput<RuntimeAnalysisHumanApprovalResult>(step)
                : null;
        }

        private async Task<RuntimeAnalysisScenarioExecutionResult?>
            ReadChildScenarioExecutionAsync(
                AiExecutionState state,
                string childExecutionId,
                RuntimeAnalysisScenarioPolicyValidationResult? policyValidation,
                CancellationToken cancellationToken)
        {
            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisChildDagDefinitionFactory.ExecuteApprovedScenarioStepName,
                    out var step))
            {
                return null;
            }

            if (step.Status == AiStepExecutionStatus.None)
            {
                return policyValidation is null
                    ? null
                    : new RuntimeAnalysisScenarioExecutionResult
                    {
                        Required = false,
                        Status = RuntimeAnalysisScenarioExecutionStatuses.NotStarted,
                        Scenario = policyValidation.Scenario,
                        PlanKey = policyValidation.PlanKey,
                        Message =
                            "Child follow-up scenario has not started because child approval has not completed."
                    };
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                var record = await _executionStore.GetAsync(
                        childExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (record is null)
                {
                    return null;
                }

                return new RuntimeAnalysisScenarioExecutionResult
                {
                    Required = true,
                    Status = record.Status,
                    ContinuationId = record.ContinuationId,
                    RequestedAtUtc = record.RequestedAtUtc,
                    CompletedAtUtc = record.CompletedAtUtc,
                    Scenario = record.Scenario,
                    PlanKey = record.PlanKey,
                    Observation = record.Observation,
                    CompletedBy = record.CompletedBy,
                    Message =
                        "Child approval accepted. The runtime is parked while the existing Next.js BurstController executes the follow-up scenario."
                };
            }

            if (step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeStepOutput<RuntimeAnalysisScenarioExecutionResult>(step);
            }

            return null;
        }

        private static RuntimeAnalysisVerificationResult? ReadChildVerification(
            AiExecutionState state,
            RuntimeAnalysisScenarioExecutionResult? scenarioExecution)
        {
            if (state.Steps.TryGetValue(
                    RuntimeAnalysisChildDagDefinitionFactory.VerifyScenarioOutcomeStepName,
                    out var step)
                && step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeStepOutput<RuntimeAnalysisVerificationResult>(step);
            }

            if (scenarioExecution is null)
            {
                return null;
            }

            if (string.Equals(
                    scenarioExecution.Status,
                    RuntimeAnalysisScenarioExecutionStatuses.NotExecuted,
                    StringComparison.Ordinal))
            {
                return new RuntimeAnalysisVerificationResult
                {
                    Status = RuntimeAnalysisVerificationStatuses.Skipped,
                    Executed = false,
                    ExpectedRequests = scenarioExecution.Scenario.TotalRequests,
                    Summary =
                        "Child verification was skipped because no follow-up scenario crossed the execution boundary."
                };
            }

            return new RuntimeAnalysisVerificationResult
            {
                Status = RuntimeAnalysisVerificationStatuses.Pending,
                Executed = false,
                ExpectedRequests = scenarioExecution.Scenario.TotalRequests,
                Summary =
                    "Child verification is waiting for the approved follow-up scenario result."
            };
        }

        private static string ResolveChildCurrentStep(
            AiExecutionState state)
        {
            var ordered = new[]
            {
                RuntimeAnalysisChildDagDefinitionFactory.CaptureEvidenceStepName,
                RuntimeAnalysisChildDagDefinitionFactory.ReanalysisStepName,
                RuntimeAnalysisChildDagDefinitionFactory.ValidateReanalysisStepName,
                RuntimeAnalysisChildDagDefinitionFactory.AwaitHumanApprovalStepName,
                RuntimeAnalysisChildDagDefinitionFactory.ExecuteApprovedScenarioStepName,
                RuntimeAnalysisChildDagDefinitionFactory.VerifyScenarioOutcomeStepName,
                RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName
            };

            foreach (var name in ordered)
            {
                if (state.Steps.TryGetValue(name, out var step)
                    && step.Status == AiStepExecutionStatus.WaitingForExternal)
                {
                    return name;
                }
            }

            for (var index = ordered.Length - 1; index >= 0; index--)
            {
                if (state.Steps.TryGetValue(ordered[index], out var step)
                    && step.Status == AiStepExecutionStatus.Completed)
                {
                    return step.StepName;
                }
            }

            return RuntimeAnalysisChildDagDefinitionFactory.CaptureEvidenceStepName;
        }

        private static T? TryReadCompletedOutput<T>(
            AiExecutionState state,
            string stepName)
            where T : class
        {
            if (!state.Steps.TryGetValue(stepName, out var step)
                || step.Status != AiStepExecutionStatus.Completed)
            {
                return null;
            }

            return DeserializeStepOutput<T>(step);
        }

        private static T DeserializeStepOutput<T>(
            AiStepState step)
        {
            var output = step.Result?.Output;

            if (string.IsNullOrWhiteSpace(output))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Child step '{step.StepName}' completed without structured output.");
            }

            try
            {
                return JsonSerializer.Deserialize<T>(
                           output,
                           SerializerOptions)
                       ?? throw new RuntimeAnalysisRuntimeExecutionException(
                           $"Child step '{step.StepName}' output deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Child step '{step.StepName}' output is invalid JSON.",
                    exception);
            }
        }

        private static RuntimeAnalysisChildDagResult BuildResult(
            AiStepExecutionStatus childStepStatus,
            IReadOnlyList<RuntimeAnalysisChildDagRelationResult> relations)
        {
            var allCompleted =
                relations.Count > 0
                && relations.All(
                    relation => string.Equals(
                        relation.RelationStatus,
                        AiChildExecutionRelationStatus.Completed.ToString(),
                        StringComparison.Ordinal));

            var allResumed =
                relations.Count > 0
                && relations.All(
                    relation => string.Equals(
                        relation.ContinuationStatus,
                        AiChildContinuationStatus.Resumed.ToString(),
                        StringComparison.Ordinal));

            var allGenerationZero =
                relations.Count > 0
                && relations.All(
                    relation => relation.InvocationGeneration == 0);

            var childIds = relations
                .Select(relation => relation.ChildExecutionId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            var childIdsUnique =
                childIds.Length > 0
                && childIds.Distinct(StringComparer.Ordinal).Count() == childIds.Length;

            var status = childStepStatus switch
            {
                AiStepExecutionStatus.Completed when relations.Count == 0 =>
                    RuntimeAnalysisChildDagStatuses.Completed,
                AiStepExecutionStatus.Completed when allCompleted =>
                    RuntimeAnalysisChildDagStatuses.Completed,
                AiStepExecutionStatus.Failed =>
                    RuntimeAnalysisChildDagStatuses.Failed,
                _ =>
                    RuntimeAnalysisChildDagStatuses.Running
            };

            return new RuntimeAnalysisChildDagResult
            {
                Status = status,
                ExpectedDepth = Math.Max(
                    RuntimeAnalysisChildDagDefinitionFactory.InitialApprovedChildDepth,
                    relations.Count),
                ObservedDepth = relations.Count,
                AllRelationsCompleted = allCompleted,
                AllContinuationsResumed = allResumed,
                AllInvocationGenerationsZero = allGenerationZero,
                ChildExecutionIdsUnique = childIdsUnique,
                Relations = relations,
                Summary = BuildSummary(
                    status,
                    relations,
                    allCompleted,
                    allResumed,
                    allGenerationZero,
                    childIdsUnique)
            };
        }

        private static RuntimeAnalysisChildDagResult NotStarted()
        {
            return new RuntimeAnalysisChildDagResult
            {
                Status = RuntimeAnalysisChildDagStatuses.NotStarted,
                ExpectedDepth = RuntimeAnalysisChildDagDefinitionFactory.InitialApprovedChildDepth,
                Summary =
                    "No approved child execution has started yet. One durable approval creates one child; deeper children require later child re-analysis and approval."
            };
        }

        private static string BuildSummary(
            string status,
            IReadOnlyList<RuntimeAnalysisChildDagRelationResult> relations,
            bool allCompleted,
            bool allResumed,
            bool allGenerationZero,
            bool childIdsUnique)
        {
            var observedDepth = relations.Count;
            var deepest = relations.LastOrDefault();

            if (deepest?.HumanApproval?.Status == RuntimeAnalysisHumanApprovalStatuses.Pending)
            {
                return
                    $"Approval-driven Child DAG reached depth {observedDepth}. Child depth {deepest.Depth} re-analysis is waiting for a new human approval before another experiment can continue.";
            }

            if (deepest?.ScenarioExecution?.Status == RuntimeAnalysisScenarioExecutionStatuses.Pending)
            {
                return
                    $"Approval-driven Child DAG reached depth {observedDepth}. Child depth {deepest.Depth} approval is durable and the follow-up workload is waiting for the existing browser BurstController.";
            }

            if (string.Equals(
                    status,
                    RuntimeAnalysisChildDagStatuses.Completed,
                    StringComparison.Ordinal))
            {
                if (observedDepth == 0)
                {
                    return
                        "Approval-driven Child DAG completed without creating a child relation because no additional decision crossed the approval boundary.";
                }

                return
                    $"Approval-driven Child DAG currently has {observedDepth} durable child execution(s); "
                    + $"relationsCompleted={allCompleted}, "
                    + $"continuationsResumed={allResumed}, "
                    + $"generationZero={allGenerationZero}, "
                    + $"childExecutionIdsUnique={childIdsUnique}.";
            }

            if (string.Equals(
                    status,
                    RuntimeAnalysisChildDagStatuses.Failed,
                    StringComparison.Ordinal))
            {
                return
                    $"Approval-driven Child DAG failed after observing {observedDepth} durable child execution(s).";
            }

            return
                $"Approval-driven Child DAG is active at depth {observedDepth}. Additional depth is created only by another approved child decision.";
        }

        private sealed class ChildProjection
        {
            public static ChildProjection Empty { get; } = new();

            public string RuntimeStatus { get; init; } = string.Empty;

            public string CurrentStep { get; init; } = string.Empty;

            public string InvestigationMode { get; init; } =
                RuntimeAnalysisInvestigationModes.StopWhenConclusive;

            public RuntimeAnalysisReanalysisResult? Reanalysis { get; init; }

            public RuntimeAnalysisScenarioPolicyValidationResult? PolicyValidation { get; init; }

            public RuntimeAnalysisHumanApprovalResult? HumanApproval { get; init; }

            public RuntimeAnalysisScenarioExecutionResult? ScenarioExecution { get; init; }

            public RuntimeAnalysisVerificationResult? Verification { get; init; }
        }
    }
}
