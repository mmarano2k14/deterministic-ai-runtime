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
    }
}
