using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Verifies that every recovered parent SharedRun ends on one exact replacement
    /// runtime ownership outside the failed runtime set.
    /// </summary>
    /// <remarks>
    /// This assertion proves the concrete recovery edges exercised by a production
    /// reference scenario. It deliberately does not infer ownership intervals from
    /// sampled runtime bindings or lifecycle-event timestamp ordering.
    ///
    /// The non-overlapping ownership interval itself is proved independently by the
    /// real-Redis atomic ownership protocol test in RedisRuntimeOwnershipHandoffProofTests.
    /// </remarks>
    internal static class ProductionRuntimeOwnershipTransitionAssertions
    {
        public static ProductionRuntimeOwnershipTransitionProof
            AssertExactRecoveredFinalOwnership(
                IReadOnlyCollection<AiSharedRunRecord> completedParentRuns,
                IReadOnlySet<string> recoveredSharedRunIds,
                IReadOnlySet<string> recoveredExecutionIds,
                IReadOnlySet<string> failedRuntimeInstanceIds,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(completedParentRuns);

            return AssertExactRecoveredFinalOwnership(
                completedParentRuns
                    .Select(
                        run =>
                            ProductionRuntimeOwnershipFinalTarget
                                .FromSharedRun(run))
                    .ToArray(),
                recoveredSharedRunIds,
                recoveredExecutionIds,
                failedRuntimeInstanceIds,
                proofName);
        }

        internal static ProductionRuntimeOwnershipTransitionProof
            AssertExactRecoveredFinalOwnership(
                IReadOnlyCollection<ProductionRuntimeOwnershipFinalTarget>
                    completedParentRuns,
                IReadOnlySet<string> recoveredSharedRunIds,
                IReadOnlySet<string> recoveredExecutionIds,
                IReadOnlySet<string> failedRuntimeInstanceIds,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(completedParentRuns);
            ArgumentNullException.ThrowIfNull(recoveredSharedRunIds);
            ArgumentNullException.ThrowIfNull(recoveredExecutionIds);
            ArgumentNullException.ThrowIfNull(failedRuntimeInstanceIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var completedBySharedRunId =
                completedParentRuns.ToDictionary(
                    run => run.SharedRunId,
                    StringComparer.Ordinal);

            Assert.Equal(
                completedParentRuns.Count,
                completedBySharedRunId.Count);

            var unexpectedRecoveredSharedRunIds =
                recoveredSharedRunIds
                    .Where(
                        sharedRunId =>
                            !completedBySharedRunId.ContainsKey(sharedRunId))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                unexpectedRecoveredSharedRunIds.Length == 0,
                $"{proofName} contains recovered SharedRunIds outside the completed parent-run set. " +
                $"Unexpected='{string.Join(",", unexpectedRecoveredSharedRunIds)}'.");

            Assert.Equal(
                recoveredSharedRunIds.Count,
                recoveredExecutionIds.Count);

            var observedRecoveredExecutionIds =
                new HashSet<string>(StringComparer.Ordinal);
            var observedReplacementBindings =
                new HashSet<ProductionRuntimeOwnershipBinding>();

            foreach (var sharedRunId in recoveredSharedRunIds.OrderBy(
                         value => value,
                         StringComparer.Ordinal))
            {
                var finalRun =
                    completedBySharedRunId[sharedRunId];

                Assert.False(
                    string.IsNullOrWhiteSpace(
                        finalRun.AssignedRuntimeInstanceId),
                    $"{proofName} recovered SharedRunId='{sharedRunId}' has no final RuntimeInstanceId.");

                Assert.False(
                    string.IsNullOrWhiteSpace(finalRun.LocalRunId),
                    $"{proofName} recovered SharedRunId='{sharedRunId}' has no final LocalRunId.");

                Assert.False(
                    string.IsNullOrWhiteSpace(finalRun.ExecutionId),
                    $"{proofName} recovered SharedRunId='{sharedRunId}' has no final ExecutionId.");

                Assert.DoesNotContain(
                    finalRun.AssignedRuntimeInstanceId!,
                    failedRuntimeInstanceIds);

                Assert.Contains(
                    finalRun.ExecutionId!,
                    recoveredExecutionIds);

                Assert.True(
                    observedRecoveredExecutionIds.Add(
                        finalRun.ExecutionId!),
                    $"{proofName} recovered ExecutionId='{finalRun.ExecutionId}' is bound to more than one recovered parent SharedRun.");

                Assert.True(
                    observedReplacementBindings.Add(
                        new ProductionRuntimeOwnershipBinding(
                            finalRun.AssignedRuntimeInstanceId!,
                            finalRun.LocalRunId!,
                            finalRun.ExecutionId!)),
                    $"{proofName} duplicated a final replacement ownership binding. " +
                    $"RuntimeInstanceId='{finalRun.AssignedRuntimeInstanceId}', " +
                    $"LocalRunId='{finalRun.LocalRunId}', " +
                    $"ExecutionId='{finalRun.ExecutionId}'.");
            }

            Assert.Equal(
                recoveredExecutionIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal),
                observedRecoveredExecutionIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal));

            Assert.Equal(
                recoveredSharedRunIds.Count,
                observedReplacementBindings.Count);

            return new ProductionRuntimeOwnershipTransitionProof(
                ExpectedRecoveredSharedRunCount:
                    recoveredSharedRunIds.Count,
                ObservedRecoveredSharedRunCount:
                    observedReplacementBindings.Count,
                FinalReplacementBindingCount:
                    observedReplacementBindings.Count,
                TransitionViolationCount: 0);
        }
    }

    /// <summary>
    /// Normalized final durable parent-run ownership used by the reference tests.
    /// </summary>
    internal sealed record ProductionRuntimeOwnershipFinalTarget(
        string SharedRunId,
        string? AssignedRuntimeInstanceId,
        string? LocalRunId,
        string? ExecutionId)
    {
        public static ProductionRuntimeOwnershipFinalTarget FromSharedRun(
            AiSharedRunRecord sharedRun,
            string? resolvedExecutionId = null)
        {
            ArgumentNullException.ThrowIfNull(sharedRun);

            return new ProductionRuntimeOwnershipFinalTarget(
                sharedRun.SharedRunId,
                sharedRun.AssignedRuntimeInstanceId,
                sharedRun.LocalRunId,
                !string.IsNullOrWhiteSpace(resolvedExecutionId)
                    ? resolvedExecutionId
                    : sharedRun.ExecutionId);
        }
    }

    internal sealed record ProductionRuntimeOwnershipBinding(
        string RuntimeInstanceId,
        string LocalRunId,
        string ExecutionId);

    internal sealed record ProductionRuntimeOwnershipTransitionProof(
        int ExpectedRecoveredSharedRunCount,
        int ObservedRecoveredSharedRunCount,
        int FinalReplacementBindingCount,
        int TransitionViolationCount);
}
