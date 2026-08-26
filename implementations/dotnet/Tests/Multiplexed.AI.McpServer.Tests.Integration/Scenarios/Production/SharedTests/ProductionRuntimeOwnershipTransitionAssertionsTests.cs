using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Xunit;
using Xunit.Sdk;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    public sealed class ProductionRuntimeOwnershipTransitionAssertionsTests
    {
        [Fact]
        public void AssertExactRecoveredFinalOwnership_Should_Accept_Exact_Replacement_Owners()
        {
            var proof =
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        new[]
                        {
                            CreateFinal(
                                "shared-1",
                                "runtime-new-1",
                                "local-new-1",
                                "execution-1"),
                            CreateFinal(
                                "shared-2",
                                "runtime-new-2",
                                "local-new-2",
                                "execution-2")
                        },
                        Set("shared-1", "shared-2"),
                        Set("execution-1", "execution-2"),
                        Set("runtime-old-1", "runtime-old-2"),
                        "exact replacement ownership proof");

            Assert.Equal(2, proof.ExpectedRecoveredSharedRunCount);
            Assert.Equal(2, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(2, proof.FinalReplacementBindingCount);
            Assert.Equal(0, proof.TransitionViolationCount);
        }

        [Fact]
        public void IncludeExactSupplementalRecoveredFinalTarget_Should_Add_Continuation_Without_Broadening_Parent_Set()
        {
            var parentTargets =
                new[]
                {
                    CreateFinal(
                        "parent-shared-1",
                        "runtime-parent-final",
                        "local-parent-final",
                        "parent-execution-1")
                };

            var continuationTarget =
                CreateFinal(
                    "child-continuation-child-invocation-1",
                    "runtime-continuation-final",
                    "local-continuation-final",
                    "parent-execution-2");

            var merged =
                ProductionRuntimeOwnershipTransitionAssertions
                    .IncludeExactSupplementalRecoveredFinalTarget(
                        parentTargets,
                        continuationTarget,
                        "continuation ownership target proof");

            Assert.Equal(2, merged.Count);
            Assert.Contains(
                merged,
                target =>
                    target == continuationTarget);

            var proof =
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        merged,
                        Set("child-continuation-child-invocation-1"),
                        Set("parent-execution-2"),
                        Set("runtime-continuation-failed"),
                        "continuation recovered ownership proof");

            Assert.Equal(1, proof.ExpectedRecoveredSharedRunCount);
            Assert.Equal(1, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(1, proof.FinalReplacementBindingCount);
            Assert.Equal(0, proof.TransitionViolationCount);
        }

        [Fact]
        public void IncludeExactSupplementalRecoveredFinalTarget_Should_Reject_Conflicting_Duplicate_Target()
        {
            var existing =
                new[]
                {
                    CreateFinal(
                        "child-continuation-child-invocation-1",
                        "runtime-final-a",
                        "local-final-a",
                        "parent-execution-1")
                };

            var conflicting =
                CreateFinal(
                    "child-continuation-child-invocation-1",
                    "runtime-final-b",
                    "local-final-b",
                    "parent-execution-1");

            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipTransitionAssertions
                    .IncludeExactSupplementalRecoveredFinalTarget(
                        existing,
                        conflicting,
                        "conflicting continuation ownership target proof"));
        }

        [Fact]
        public void AssertExactRecoveredFinalOwnership_Should_Reject_Failed_Runtime_As_Final_Owner()
        {
            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        new[]
                        {
                            CreateFinal(
                                "shared-1",
                                "runtime-failed",
                                "local-new",
                                "execution-1")
                        },
                        Set("shared-1"),
                        Set("execution-1"),
                        Set("runtime-failed"),
                        "failed owner proof"));
        }

        [Fact]
        public void AssertExactRecoveredFinalOwnership_Should_Reject_Unexpected_Execution()
        {
            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        new[]
                        {
                            CreateFinal(
                                "shared-1",
                                "runtime-new",
                                "local-new",
                                "execution-unexpected")
                        },
                        Set("shared-1"),
                        Set("execution-expected"),
                        Set("runtime-failed"),
                        "execution identity proof"));
        }

        [Fact]
        public void AssertExactRecoveredFinalOwnership_Should_Reject_Missing_Final_Owner()
        {
            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        new[]
                        {
                            CreateFinal(
                                "shared-1",
                                runtimeInstanceId: null,
                                "local-new",
                                "execution-1")
                        },
                        Set("shared-1"),
                        Set("execution-1"),
                        Set("runtime-failed"),
                        "missing final owner proof"));
        }

        [Fact]
        public void AssertExactRecoveredFinalOwnership_Should_Accept_Empty_Recovery_Set()
        {
            var proof =
                ProductionRuntimeOwnershipTransitionAssertions
                    .AssertExactRecoveredFinalOwnership(
                        new[]
                        {
                            CreateFinal(
                                "shared-1",
                                "runtime-1",
                                "local-1",
                                "execution-1")
                        },
                        Set(),
                        Set(),
                        Set(),
                        "no recovery ownership proof");

            Assert.Equal(0, proof.ExpectedRecoveredSharedRunCount);
            Assert.Equal(0, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(0, proof.FinalReplacementBindingCount);
            Assert.Equal(0, proof.TransitionViolationCount);
        }

        private static ProductionRuntimeOwnershipFinalTarget CreateFinal(
            string sharedRunId,
            string? runtimeInstanceId,
            string? localRunId,
            string? executionId)
        {
            return new ProductionRuntimeOwnershipFinalTarget(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId);
        }

        private static IReadOnlySet<string> Set(params string[] values)
        {
            return values.ToHashSet(StringComparer.Ordinal);
        }
    }
}
