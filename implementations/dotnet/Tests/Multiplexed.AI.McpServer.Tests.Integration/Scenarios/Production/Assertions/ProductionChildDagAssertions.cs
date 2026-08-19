using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains opt-in assertions for deterministic nested child DAG composition in production scenarios.
    /// </summary>
    public static class ProductionChildDagAssertions
    {
        /// <summary>
        /// Verifies the exact parent-to-child relation chain requested by each production scenario run.
        /// </summary>
        /// <param name="scenario">The expected production scenario.</param>
        /// <param name="result">The actual production scenario result.</param>
        public static void AssertNestedComposition(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants)
            {
                var tenantResult = result.Tenants.Single(actual =>
                    string.Equals(actual.TenantId, tenant.TenantId, StringComparison.Ordinal));

                foreach (var run in tenantResult.Runs)
                {
                    Assert.Equal(tenant.Run.ChildDepth, run.ChildDagExecutions.Count);

                    if (tenant.Run.ChildDepth == 0)
                    {
                        continue;
                    }

                    Assert.False(string.IsNullOrWhiteSpace(run.ExecutionId));

                    var expectedParentExecutionId = run.ExecutionId!;
                    var expectedParentPipelineName = tenantResult.PipelineKey;
                    var childExecutionIds = new HashSet<string>(StringComparer.Ordinal);
                    var invocationKeys = new HashSet<string>(StringComparer.Ordinal);

                    for (var depth = 1; depth <= tenant.Run.ChildDepth; depth++)
                    {
                        var remainingDepth = tenant.Run.ChildDepth - depth + 1;
                        var relation = run.ChildDagExecutions[depth - 1];

                        Assert.Equal(depth, relation.Depth);
                        Assert.Equal(tenant.TenantId, relation.TenantId);
                        Assert.Equal(expectedParentExecutionId, relation.ParentExecutionId);
                        Assert.NotEqual(relation.ParentExecutionId, relation.ChildExecutionId);
                        Assert.Equal(
                            McpTestPipelineFactory.CreateChildPipelineName(
                                expectedParentPipelineName,
                                remainingDepth),
                            relation.ChildDagId);
                        Assert.Equal(
                            McpTestPipelineFactory.PipelineVersion,
                            relation.ChildDagDefinitionVersion);
                        Assert.Equal(0, relation.InvocationGeneration);
                        Assert.Equal(AiChildExecutionRelationStatus.Completed, relation.RelationStatus);
                        Assert.Equal(AiChildContinuationStatus.Resumed, relation.ContinuationStatus);
                        Assert.False(string.IsNullOrWhiteSpace(relation.ChildResultDigest));
                        Assert.True(
                            string.IsNullOrWhiteSpace(relation.ChildFailureReason),
                            $"Expected child execution '{relation.ChildExecutionId}' to complete successfully, but failure '{relation.ChildFailureReason}' was recorded.");
                        Assert.True(
                            childExecutionIds.Add(relation.ChildExecutionId),
                            $"Duplicate ChildExecutionId '{relation.ChildExecutionId}' was observed in one nested composition chain.");
                        Assert.True(
                            invocationKeys.Add(relation.ChildInvocationKey),
                            $"Duplicate ChildInvocationKey '{relation.ChildInvocationKey}' was observed in one nested composition chain.");

                        expectedParentExecutionId = relation.ChildExecutionId;
                        expectedParentPipelineName = relation.ChildDagId;
                    }
                }
            }
        }

        /// <summary>
        /// Verifies the opt-in physical child-runtime kill/recovery proof captured by the production runner.
        /// </summary>
        /// <param name="scenario">The expected production scenario.</param>
        /// <param name="result">The actual production scenario result.</param>
        public static void AssertRuntimeFailureRecovery(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants)
            {
                var failure = tenant.Run.ChildRuntimeFailure;
                var tenantResult = result.Tenants.Single(actual =>
                    string.Equals(actual.TenantId, tenant.TenantId, StringComparison.Ordinal));

                if (failure is null)
                {
                    Assert.All(
                        tenantResult.Runs,
                        run => Assert.Null(run.ChildDagRuntimeFailure));
                    continue;
                }

                var run = Assert.Single(tenantResult.Runs);
                var proof = Assert.IsType<ProductionChildDagRuntimeFailureResult>(
                    run.ChildDagRuntimeFailure);

                Assert.InRange(failure.TargetDepth, 1, run.ChildDagExecutions.Count);

                var relation = run.ChildDagExecutions[failure.TargetDepth - 1];

                Assert.Equal(failure.Target, proof.FailureTarget);
                Assert.Equal(failure.TargetDepth, proof.TargetDepth);
                Assert.Equal(relation.ParentExecutionId, proof.ParentExecutionId);
                Assert.Equal(relation.ChildExecutionId, proof.ChildExecutionId);
                Assert.True(proof.ParentWaitingForExternalObserved);
                Assert.True(proof.ParentRuntimeCapacityReleased);
                Assert.True(proof.KillSucceeded);
                Assert.False(string.IsNullOrWhiteSpace(proof.OriginalRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(proof.OriginalLocalRunId));
                Assert.Equal(0, relation.InvocationGeneration);
                Assert.Equal(AiChildExecutionRelationStatus.Completed, relation.RelationStatus);
                Assert.Equal(AiChildContinuationStatus.Resumed, relation.ContinuationStatus);

                switch (failure.Target)
                {
                    case ProductionChildDagFailureTarget.ChildRuntime:
                        Assert.False(string.IsNullOrWhiteSpace(proof.RecoveredRuntimeInstanceId));
                        Assert.NotEqual(
                            proof.OriginalRuntimeInstanceId,
                            proof.RecoveredRuntimeInstanceId);
                        Assert.False(string.IsNullOrWhiteSpace(proof.RecoveredLocalRunId));
                        Assert.NotEqual(
                            proof.OriginalLocalRunId,
                            proof.RecoveredLocalRunId);
                        Assert.Null(proof.ObservedChildRuntimeInstanceId);
                        Assert.Null(proof.ObservedChildHostId);
                        Assert.Null(proof.ObservedChildLocalRunId);
                        break;

                    case ProductionChildDagFailureTarget.ParentRuntimeAfterPark:
                        Assert.Equal(run.ExecutionId, proof.ParentExecutionId);
                        Assert.Equal(run.RuntimeInstanceId, proof.OriginalRuntimeInstanceId);
                        Assert.Equal(run.LocalRunId, proof.OriginalLocalRunId);
                        Assert.Null(proof.RecoveredRuntimeInstanceId);
                        Assert.Null(proof.RecoveredHostId);
                        Assert.Null(proof.RecoveredLocalRunId);
                        Assert.False(string.IsNullOrWhiteSpace(proof.ObservedChildRuntimeInstanceId));
                        Assert.False(string.IsNullOrWhiteSpace(proof.ObservedChildLocalRunId));
                        Assert.NotEqual(
                            proof.OriginalRuntimeInstanceId,
                            proof.ObservedChildRuntimeInstanceId);
                        Assert.NotEqual(
                            proof.OriginalLocalRunId,
                            proof.ObservedChildLocalRunId);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(failure.Target),
                            failure.Target,
                            "Unsupported Child DAG physical failure target.");
                }
            }
        }

        /// <summary>
        /// Verifies that a focused Kubernetes in-Pod runtime-process failure preserves the owning Pod incarnation.
        /// </summary>
        /// <param name="result">The production scenario result.</param>
        public static void AssertKubernetesRuntimeProcessFailureBoundary(
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var proofs = result.Tenants
                .SelectMany(tenant => tenant.Runs)
                .Select(run => run.ChildDagRuntimeFailure)
                .Where(value =>
                    value?.FailureTarget == ProductionChildDagFailureTarget.ChildRuntime)
                .Cast<ProductionChildDagRuntimeFailureResult>()
                .ToArray();

            Assert.NotEmpty(proofs);

            foreach (var proof in proofs)
            {
                Assert.False(string.IsNullOrWhiteSpace(proof.OriginalHostId));
                Assert.False(string.IsNullOrWhiteSpace(proof.RecoveredHostId));
                Assert.Equal(proof.OriginalHostId, proof.RecoveredHostId);
            }
        }

        /// <summary>
        /// Verifies that a focused Kubernetes Pod failure replaces the immutable Pod incarnation of a failed child.
        /// </summary>
        /// <param name="result">The production scenario result.</param>
        public static void AssertKubernetesPodFailureBoundary(
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var proofs = result.Tenants
                .SelectMany(tenant => tenant.Runs)
                .Select(run => run.ChildDagRuntimeFailure)
                .Where(value =>
                    value?.FailureTarget == ProductionChildDagFailureTarget.ChildRuntime)
                .Cast<ProductionChildDagRuntimeFailureResult>()
                .ToArray();

            Assert.NotEmpty(proofs);

            foreach (var proof in proofs)
            {
                Assert.False(string.IsNullOrWhiteSpace(proof.OriginalHostId));
                Assert.False(string.IsNullOrWhiteSpace(proof.RecoveredHostId));
                Assert.NotEqual(proof.OriginalHostId, proof.RecoveredHostId);
            }
        }

        /// <summary>
        /// Verifies the final Kubernetes parent-Pod failure boundary: the parked parent Pod is distinct from the
        /// checkpoint-blocked child Pod that remains active while the parent boundary is destroyed.
        /// </summary>
        /// <param name="result">The production scenario result.</param>
        public static void AssertKubernetesParentPodFailureWhileChildContinues(
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var proofs = result.Tenants
                .SelectMany(tenant => tenant.Runs)
                .Select(run => run.ChildDagRuntimeFailure)
                .Where(value =>
                    value?.FailureTarget == ProductionChildDagFailureTarget.ParentRuntimeAfterPark)
                .Cast<ProductionChildDagRuntimeFailureResult>()
                .ToArray();

            Assert.NotEmpty(proofs);

            foreach (var proof in proofs)
            {
                Assert.False(string.IsNullOrWhiteSpace(proof.OriginalHostId));
                Assert.False(string.IsNullOrWhiteSpace(proof.ObservedChildHostId));
                Assert.NotEqual(proof.OriginalHostId, proof.ObservedChildHostId);
                Assert.False(string.IsNullOrWhiteSpace(proof.ObservedChildRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(proof.ObservedChildLocalRunId));
            }
        }

        /// <summary>
        /// Verifies the complete Step 9J production proof by composing the existing production, nested-composition,
        /// runtime-failure, and Kubernetes failure-boundary assertions, then closes the explicit aggregate counters
        /// required by the final proof contract.
        /// </summary>
        /// <param name="scenario">The final Child DAG production scenario.</param>
        /// <param name="result">The final production scenario result.</param>
        public static void AssertFinalProductionProof(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
            AssertNestedComposition(
                scenario,
                result);
            AssertRuntimeFailureRecovery(
                scenario,
                result);
            AssertKubernetesParentPodFailureWhileChildContinues(
                result);

            var tenant = Assert.Single(result.Tenants);
            var run = Assert.Single(tenant.Runs);
            var relation = Assert.Single(run.ChildDagExecutions);

            var parentExecutionCount = new[] { run.ExecutionId }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var childExecutionCount = run.ChildDagExecutions
                .Select(value => value.ChildExecutionId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var childResultCount = run.ChildDagExecutions
                .Count(value => !string.IsNullOrWhiteSpace(value.ChildResultDigest));
            var duplicateChildCount = run.ChildDagExecutions.Count - childExecutionCount;
            var resumedContinuations = run.ChildDagExecutions
                .Where(value => value.ContinuationStatus == AiChildContinuationStatus.Resumed)
                .ToArray();
            var effectiveContinuationCount = resumedContinuations
                .Select(value => $"{value.ParentExecutionId}\u001f{value.ChildInvocationKey}")
                .Distinct(StringComparer.Ordinal)
                .Count();
            var duplicateEffectiveContinuationCount =
                resumedContinuations.Length - effectiveContinuationCount;

            Assert.Equal(1, parentExecutionCount);
            Assert.Equal(1, childExecutionCount);
            Assert.Equal(0, relation.InvocationGeneration);
            Assert.Equal(1, childResultCount);
            Assert.Equal(0, duplicateChildCount);
            Assert.Single(resumedContinuations);
            Assert.Equal(0, duplicateEffectiveContinuationCount);
        }

    }
}
