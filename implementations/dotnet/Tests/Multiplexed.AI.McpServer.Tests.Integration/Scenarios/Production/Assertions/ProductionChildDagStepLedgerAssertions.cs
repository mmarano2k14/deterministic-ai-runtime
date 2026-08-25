using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Provides exact recursive Child DAG logical-step ledger assertions for production reference scenarios.
    /// </summary>
    internal static class ProductionChildDagStepLedgerAssertions
    {
        /// <summary>
        /// Reconstructs every configured child execution from the durable relation authority and proves the exact
        /// logical-step ledger contract independently at every recursive depth.
        /// </summary>
        /// <param name="relationStore">The authoritative durable parent-to-child relation store.</param>
        /// <param name="completedParentRuns">The completed root shared runs for the current production cycle.</param>
        /// <param name="childDepth">The configured recursive child depth.</param>
        /// <param name="baseStepCount">The number of ordinary logical steps contained by every generated pipeline.</param>
        /// <param name="queryExecutionLedgerAsync">Queries ledger evidence for an exact set of execution identifiers.</param>
        /// <param name="recoveredExecutionIds">The exact executions already proven to have crossed a recovery boundary.</param>
        /// <param name="proofName">The diagnostic proof name.</param>
        /// <param name="relationTimeout">The maximum time allowed for each durable child relation chain to converge.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The aggregate recursive Child DAG step-ledger proof.</returns>
        public static async Task<ProductionChildDagRecursiveStepLedgerProof>
            AssertExactRecursiveLogicalStepCompletionAsync(
                IAiChildExecutionRelationStore relationStore,
                IReadOnlyCollection<AiSharedRunRecord> completedParentRuns,
                int childDepth,
                int baseStepCount,
                Func<IReadOnlySet<string>, Task<IReadOnlyList<AiDecisionLedgerEntry>>> queryExecutionLedgerAsync,
                IReadOnlySet<string> recoveredExecutionIds,
                string proofName,
                TimeSpan relationTimeout,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(completedParentRuns);

            return await AssertExactRecursiveLogicalStepCompletionAsync(
                    relationStore,
                    completedParentRuns
                        .Select(parentRun =>
                            ProductionChildDagParentExecutionProofTarget
                                .FromSharedRun(parentRun))
                        .ToArray(),
                    childDepth,
                    baseStepCount,
                    queryExecutionLedgerAsync,
                    recoveredExecutionIds,
                    proofName,
                    relationTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Reconstructs every configured child execution from durable parent execution references and proves the exact
        /// logical-step ledger contract independently at every recursive depth.
        /// </summary>
        /// <param name="relationStore">The authoritative durable parent-to-child relation store.</param>
        /// <param name="completedParentRuns">The completed root execution references for the current production cycle.</param>
        /// <param name="childDepth">The configured recursive child depth.</param>
        /// <param name="baseStepCount">The number of ordinary logical steps contained by every generated pipeline.</param>
        /// <param name="queryExecutionLedgerAsync">Queries ledger evidence for an exact set of execution identifiers.</param>
        /// <param name="recoveredExecutionIds">The exact executions already proven to have crossed a recovery boundary.</param>
        /// <param name="proofName">The diagnostic proof name.</param>
        /// <param name="relationTimeout">The maximum time allowed for each durable child relation chain to converge.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The aggregate recursive Child DAG step-ledger proof.</returns>
        internal static async Task<ProductionChildDagRecursiveStepLedgerProof>
            AssertExactRecursiveLogicalStepCompletionAsync(
                IAiChildExecutionRelationStore relationStore,
                IReadOnlyCollection<ProductionChildDagParentExecutionProofTarget> completedParentRuns,
                int childDepth,
                int baseStepCount,
                Func<IReadOnlySet<string>, Task<IReadOnlyList<AiDecisionLedgerEntry>>> queryExecutionLedgerAsync,
                IReadOnlySet<string> recoveredExecutionIds,
                string proofName,
                TimeSpan relationTimeout,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relationStore);
            ArgumentNullException.ThrowIfNull(completedParentRuns);
            ArgumentOutOfRangeException.ThrowIfNegative(childDepth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseStepCount);
            ArgumentNullException.ThrowIfNull(queryExecutionLedgerAsync);
            ArgumentNullException.ThrowIfNull(recoveredExecutionIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            if (relationTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relationTimeout),
                    relationTimeout,
                    "The recursive Child DAG relation timeout must be greater than zero.");
            }

            if (childDepth == 0)
            {
                return ProductionChildDagRecursiveStepLedgerProof.Empty;
            }

            Assert.NotEmpty(completedParentRuns);

            var childExecutionIdsByDepth =
                Enumerable.Range(0, childDepth)
                    .Select(_ => new HashSet<string>(StringComparer.Ordinal))
                    .ToArray();

            foreach (var parentRun in completedParentRuns)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(parentRun.ExecutionId),
                    $"{proofName} encountered a completed parent SharedRun without an ExecutionId. SharedRunId='{parentRun.SharedRunId}'.");

                var parentPipelineName =
                    parentRun.PipelineName;

                Assert.False(
                    string.IsNullOrWhiteSpace(parentPipelineName),
                    $"{proofName} encountered parent execution '{parentRun.ExecutionId}' without a pipeline identity.");

                var relations =
                    await ProductionChildDagScenarioHelpers
                        .WaitForNestedRelationsAsync(
                            relationStore,
                            parentRun.TenantId,
                            parentRun.ExecutionId!,
                            parentPipelineName!,
                            childDepth,
                            relationTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);

                Assert.Equal(childDepth, relations.Count);

                for (var depthIndex = 0; depthIndex < relations.Count; depthIndex++)
                {
                    var relation = relations[depthIndex];
                    var expectedDepth = depthIndex + 1;

                    Assert.Equal(expectedDepth, relation.Depth);
                    Assert.True(
                        childExecutionIdsByDepth[depthIndex].Add(relation.ChildExecutionId),
                        $"{proofName} observed duplicate ChildExecutionId '{relation.ChildExecutionId}' at Depth='{expectedDepth}'.");
                }
            }

            var expectedExecutionCountPerDepth = completedParentRuns.Count;

            for (var depthIndex = 0; depthIndex < childExecutionIdsByDepth.Length; depthIndex++)
            {
                Assert.Equal(
                    expectedExecutionCountPerDepth,
                    childExecutionIdsByDepth[depthIndex].Count);
            }

            var allChildExecutionIds =
                childExecutionIdsByDepth
                    .SelectMany(executionIds => executionIds)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                checked(completedParentRuns.Count * childDepth),
                allChildExecutionIds.Count);

            var childLedgerEntries =
                await queryExecutionLedgerAsync(allChildExecutionIds)
                    .ConfigureAwait(false);

            var depthProofs =
                new List<ProductionChildDagDepthStepLedgerProof>(childDepth);

            for (var depthIndex = 0; depthIndex < childExecutionIdsByDepth.Length; depthIndex++)
            {
                var depth = depthIndex + 1;
                var expectedExecutionIds = childExecutionIdsByDepth[depthIndex];
                var expectedStepCountPerExecution =
                    GetExpectedLogicalStepCountAtDepth(
                        baseStepCount,
                        childDepth,
                        depth);
                var recoveredExecutionIdsAtDepth =
                    recoveredExecutionIds
                        .Intersect(expectedExecutionIds, StringComparer.Ordinal)
                        .ToHashSet(StringComparer.Ordinal);
                var ledgerEntriesAtDepth =
                    childLedgerEntries
                        .Where(entry =>
                            expectedExecutionIds.Contains(
                                entry.CorrelationContext.ExecutionId))
                        .ToArray();

                var stepProof =
                    RuntimePoolProductionCycleExecutor
                        .AssertLogicalStepCompletionEvidence(
                            ledgerEntriesAtDepth,
                            expectedExecutionIds,
                            recoveredExecutionIdsAtDepth,
                            expectedStepCountPerExecution,
                            $"{proofName} Depth='{depth}'");

                depthProofs.Add(
                    new ProductionChildDagDepthStepLedgerProof(
                        depth,
                        expectedExecutionIds.Count,
                        expectedStepCountPerExecution,
                        checked(expectedExecutionIds.Count * expectedStepCountPerExecution),
                        stepProof.RawStepCompletedEntryCount,
                        stepProof.DistinctLogicalStepCompletedCount,
                        stepProof.DuplicateStepCompletedEntryCount,
                        stepProof.DuplicateEvidenceExecutionIds));
            }

            var expectedLogicalStepCount =
                depthProofs.Sum(proof => proof.ExpectedLogicalStepCount);
            var distinctLogicalStepCompletedCount =
                depthProofs.Sum(proof => proof.DistinctLogicalStepCompletedCount);

            Assert.Equal(
                expectedLogicalStepCount,
                distinctLogicalStepCompletedCount);

            return new ProductionChildDagRecursiveStepLedgerProof(
                allChildExecutionIds.Count,
                expectedLogicalStepCount,
                depthProofs.Sum(proof => proof.RawStepCompletedEntryCount),
                distinctLogicalStepCompletedCount,
                depthProofs.Sum(proof => proof.DuplicateStepCompletedEntryCount),
                depthProofs);
        }

        /// <summary>
        /// Gets the exact logical-step count for one child execution at the requested recursive depth.
        /// </summary>
        /// <param name="baseStepCount">The number of ordinary logical steps in every generated pipeline.</param>
        /// <param name="childDepth">The total configured child depth below the root parent.</param>
        /// <param name="depth">The one-based child depth being proven.</param>
        /// <returns>
        /// The ordinary step count plus the durable <c>execute-child-dag</c> step for every child that itself
        /// contains another nested child level.
        /// </returns>
        internal static int GetExpectedLogicalStepCountAtDepth(
            int baseStepCount,
            int childDepth,
            int depth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseStepCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);

            if (depth <= 0 || depth > childDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depth),
                    depth,
                    $"Child depth must be between 1 and '{childDepth}'.");
            }

            return checked(baseStepCount + (depth < childDepth ? 1 : 0));
        }
    }

    /// <summary>
    /// Captures the authoritative root execution identity required to reconstruct one recursive Child DAG chain.
    /// </summary>
    /// <param name="SharedRunId">The shared-run identifier used for diagnostics.</param>
    /// <param name="TenantId">The persistent tenant boundary.</param>
    /// <param name="ExecutionId">The resolved durable root execution identifier.</param>
    /// <param name="PipelineName">The parent pipeline identity used by the durable child relation contract.</param>
    internal sealed record ProductionChildDagParentExecutionProofTarget(
        string SharedRunId,
        string TenantId,
        string? ExecutionId,
        string? PipelineName)
    {
        /// <summary>
        /// Creates a proof target from a completed shared-run record, optionally preserving a more authoritative
        /// execution identifier already resolved by the provider-specific completion observer.
        /// </summary>
        /// <param name="sharedRun">The completed shared-run record.</param>
        /// <param name="resolvedExecutionId">The provider-resolved execution identifier when available.</param>
        /// <returns>The normalized root execution proof target.</returns>
        public static ProductionChildDagParentExecutionProofTarget FromSharedRun(
            AiSharedRunRecord sharedRun,
            string? resolvedExecutionId = null)
        {
            ArgumentNullException.ThrowIfNull(sharedRun);

            return new ProductionChildDagParentExecutionProofTarget(
                sharedRun.SharedRunId,
                sharedRun.ExecutionContextSnapshot.TenantId,
                !string.IsNullOrWhiteSpace(resolvedExecutionId)
                    ? resolvedExecutionId
                    : sharedRun.ExecutionId,
                sharedRun.PipelineKey ??
                sharedRun.RunRequest.PipelineName);
        }
    }

    /// <summary>
    /// Captures exact logical-step ledger proof for one recursive Child DAG depth.
    /// </summary>
    /// <param name="Depth">The one-based recursive child depth.</param>
    /// <param name="ExpectedExecutionCount">The exact number of child executions expected at this depth.</param>
    /// <param name="ExpectedStepCountPerExecution">The exact logical-step count expected for each child execution.</param>
    /// <param name="ExpectedLogicalStepCount">The exact aggregate logical-step count expected at this depth.</param>
    /// <param name="RawStepCompletedEntryCount">The raw append-only step-completed ledger entry count.</param>
    /// <param name="DistinctLogicalStepCompletedCount">The exact distinct logical-step completion count.</param>
    /// <param name="DuplicateStepCompletedEntryCount">The number of duplicate raw entries already constrained to recovered executions.</param>
    /// <param name="DuplicateEvidenceExecutionIds">The execution identifiers containing duplicate raw recovery evidence.</param>
    internal sealed record ProductionChildDagDepthStepLedgerProof(
        int Depth,
        int ExpectedExecutionCount,
        int ExpectedStepCountPerExecution,
        int ExpectedLogicalStepCount,
        int RawStepCompletedEntryCount,
        int DistinctLogicalStepCompletedCount,
        int DuplicateStepCompletedEntryCount,
        IReadOnlySet<string> DuplicateEvidenceExecutionIds);

    /// <summary>
    /// Captures the exact recursive Child DAG logical-step ledger proof for one production cycle.
    /// </summary>
    /// <param name="ExpectedChildExecutionCount">The exact number of nested child executions.</param>
    /// <param name="ExpectedLogicalStepCount">The exact aggregate logical-step count across all child executions.</param>
    /// <param name="RawStepCompletedEntryCount">The raw append-only child step-completed ledger entry count.</param>
    /// <param name="DistinctLogicalStepCompletedCount">The exact distinct child logical-step completion count.</param>
    /// <param name="DuplicateStepCompletedEntryCount">The duplicate raw child ledger entry count accepted only for recovered executions.</param>
    /// <param name="DepthProofs">The exact proof split by recursive depth.</param>
    internal sealed record ProductionChildDagRecursiveStepLedgerProof(
        int ExpectedChildExecutionCount,
        int ExpectedLogicalStepCount,
        int RawStepCompletedEntryCount,
        int DistinctLogicalStepCompletedCount,
        int DuplicateStepCompletedEntryCount,
        IReadOnlyList<ProductionChildDagDepthStepLedgerProof> DepthProofs)
    {
        /// <summary>
        /// Gets the empty proof used when recursive Child DAG composition is disabled.
        /// </summary>
        public static ProductionChildDagRecursiveStepLedgerProof Empty { get; } =
            new(
                0,
                0,
                0,
                0,
                0,
                Array.Empty<ProductionChildDagDepthStepLedgerProof>());
    }
}
