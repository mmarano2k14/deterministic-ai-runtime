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
    /// Projects the runtime's authoritative parent-child relation state into a
    /// bounded demo DTO. The relation store remains the source of truth.
    /// </summary>
    public sealed class RuntimeAnalysisChildDagEvidenceReader
    {
        private readonly IAiDagExecutionStore _dagStore;
        private readonly IAiChildExecutionRelationStore _relationStore;

        public RuntimeAnalysisChildDagEvidenceReader(
            IAiDagExecutionStore dagStore,
            IAiChildExecutionRelationStore relationStore)
        {
            _dagStore = dagStore ?? throw new ArgumentNullException(nameof(dagStore));
            _relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
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

            var relations =
                await ReadRelationsAsync(
                        tenantId,
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return BuildResult(
                childStep.Status,
                relations);
        }

        private async Task<IReadOnlyList<RuntimeAnalysisChildDagRelationResult>>
            ReadRelationsAsync(
                string tenantId,
                string rootExecutionId,
                CancellationToken cancellationToken)
        {
            var results =
                new List<RuntimeAnalysisChildDagRelationResult>(
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDepth);

            var currentParentExecutionId = rootExecutionId;
            var currentParentPipelineName =
                RuntimeAnalysisPipelineDefinitionFactory.PipelineName;

            for (var depth = 1;
                 depth <= RuntimeAnalysisChildDagDefinitionFactory.ChildDepth;
                 depth++)
            {
                var remainingDepth =
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDepth
                    - depth
                    + 1;

                var identity = new AiChildInvocationIdentity
                {
                    TenantId = tenantId,
                    ParentExecutionId = currentParentExecutionId,
                    ParentCallSiteId =
                        RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName,
                    ChildDagId =
                        RuntimeAnalysisChildDagDefinitionFactory
                            .CreateChildPipelineName(
                                currentParentPipelineName,
                                remainingDepth),
                    ChildDagDefinitionVersion =
                        RuntimeAnalysisChildDagDefinitionFactory.PipelineVersion,
                    CanonicalLogicalInvocationKey =
                        RuntimeAnalysisChildDagDefinitionFactory
                            .CreateChildLogicalInvocationKey(
                                currentParentPipelineName,
                                remainingDepth),
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
                    new RuntimeAnalysisChildDagRelationResult
                    {
                        Depth = depth,
                        TenantId = relation.TenantId,
                        ParentExecutionId = relation.ParentExecutionId,
                        ChildExecutionId = relation.ChildExecutionId,
                        ChildInvocationKey = relation.ChildInvocationKey,
                        ChildDagId = relation.ChildDagId,
                        ChildDagDefinitionVersion =
                            relation.ChildDagDefinitionVersion,
                        InvocationGeneration = relation.InvocationGeneration,
                        RelationStatus = relation.Status.ToString(),
                        ContinuationStatus =
                            relation.ContinuationStatus.ToString(),
                        ChildResultDigest =
                            relation.ChildResult?.ContentHash,
                        ChildFailureReason = relation.ChildFailureReason,
                        CreatedAtUtc = relation.CreatedAtUtc,
                        CompletedAtUtc = relation.CompletedAtUtc,
                        ParentResumedAtUtc = relation.ParentResumedAtUtc
                    });

                if (string.IsNullOrWhiteSpace(
                        relation.ChildExecutionId))
                {
                    break;
                }

                currentParentExecutionId = relation.ChildExecutionId;
                currentParentPipelineName = relation.ChildDagId;
            }

            return results;
        }

        private static RuntimeAnalysisChildDagResult BuildResult(
            AiStepExecutionStatus childStepStatus,
            IReadOnlyList<RuntimeAnalysisChildDagRelationResult> relations)
        {
            var allCompleted =
                relations.Count == RuntimeAnalysisChildDagDefinitionFactory.ChildDepth
                && relations.All(
                    relation => string.Equals(
                        relation.RelationStatus,
                        AiChildExecutionRelationStatus.Completed.ToString(),
                        StringComparison.Ordinal));

            var allResumed =
                relations.Count == RuntimeAnalysisChildDagDefinitionFactory.ChildDepth
                && relations.All(
                    relation => string.Equals(
                        relation.ContinuationStatus,
                        AiChildContinuationStatus.Resumed.ToString(),
                        StringComparison.Ordinal));

            var allGenerationZero =
                relations.All(
                    relation => relation.InvocationGeneration == 0);

            var childIds = relations
                .Select(relation => relation.ChildExecutionId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            var childIdsUnique =
                childIds.Distinct(StringComparer.Ordinal).Count()
                == childIds.Length;

            var status = childStepStatus switch
            {
                AiStepExecutionStatus.Completed when allCompleted =>
                    RuntimeAnalysisChildDagStatuses.Completed,
                AiStepExecutionStatus.Failed =>
                    RuntimeAnalysisChildDagStatuses.Failed,
                _ => RuntimeAnalysisChildDagStatuses.Running
            };

            return new RuntimeAnalysisChildDagResult
            {
                Status = status,
                ExpectedDepth =
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDepth,
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
                ExpectedDepth =
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDepth,
                Summary =
                    "Recursive Child DAG evidence has not started yet."
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

            if (string.Equals(
                    status,
                    RuntimeAnalysisChildDagStatuses.Completed,
                    StringComparison.Ordinal))
            {
                var resumedCount =
                    relations.Count(
                        relation => string.Equals(
                            relation.ContinuationStatus,
                            AiChildContinuationStatus.Resumed.ToString(),
                            StringComparison.Ordinal));

                var scheduledCount =
                    relations.Count(
                        relation => string.Equals(
                            relation.ContinuationStatus,
                            AiChildContinuationStatus.Scheduled.ToString(),
                            StringComparison.Ordinal));

                var suppressedCount =
                    relations.Count(
                        relation => string.Equals(
                            relation.ContinuationStatus,
                            AiChildContinuationStatus.Suppressed.ToString(),
                            StringComparison.Ordinal));

                if (!allResumed
                    && suppressedCount == 0
                    && resumedCount + scheduledCount == relations.Count)
                {
                    return
                        $"Recursive Child DAG depth {observedDepth} completed; "
                        + "all child relations are terminal and continuation "
                        + $"proof is still converging ({resumedCount}/{relations.Count} resumed, "
                        + $"{scheduledCount} scheduled).";
                }

                return
                    $"Recursive Child DAG depth {observedDepth} completed; "
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
                    $"Recursive Child DAG execution failed after observing depth {observedDepth}.";
            }

            return
                $"Recursive Child DAG is converging; observed depth {observedDepth}/{RuntimeAnalysisChildDagDefinitionFactory.ChildDepth}.";
        }
    }
}
