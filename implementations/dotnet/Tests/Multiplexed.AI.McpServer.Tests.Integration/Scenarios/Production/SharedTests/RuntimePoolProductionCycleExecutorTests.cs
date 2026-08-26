using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Xunit;
using Xunit.Sdk;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    public sealed class RuntimePoolProductionCycleExecutorTests
    {
        [Fact]
        public void ResolveRunSubmissionOffsets_Should_Preserve_Natural_Ordering()
        {
            var offsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    5,
                    ProductionChildDagSubmissionOrdering.Natural);

            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, offsets);
        }

        [Fact]
        public void ResolveRunSubmissionOffsets_Should_Reverse_Logical_Run_Order_For_Seeded_Interleaving()
        {
            var offsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    5,
                    ProductionChildDagSubmissionOrdering.Reverse);

            Assert.Equal(new[] { 4, 3, 2, 1, 0 }, offsets);
        }

        [Fact]
        public void ResolveRunSubmissionOffsets_Should_Alternate_Low_And_High_Edges_For_SeedB()
        {
            var oddOffsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    5,
                    ProductionChildDagSubmissionOrdering.OutsideIn);

            var evenOffsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    6,
                    ProductionChildDagSubmissionOrdering.OutsideIn);

            Assert.Equal(new[] { 0, 4, 1, 3, 2 }, oddOffsets);
            Assert.Equal(new[] { 0, 5, 1, 4, 2, 3 }, evenOffsets);
        }

        [Fact]
        public void ResolveRunSubmissionOffsets_Should_Expand_From_Center_For_SeedC()
        {
            var oddOffsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    5,
                    ProductionChildDagSubmissionOrdering.CenterOut);

            var evenOffsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    6,
                    ProductionChildDagSubmissionOrdering.CenterOut);

            var nineOffsets =
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    9,
                    ProductionChildDagSubmissionOrdering.CenterOut);

            Assert.Equal(new[] { 2, 1, 3, 0, 4 }, oddOffsets);
            Assert.Equal(new[] { 2, 3, 1, 4, 0, 5 }, evenOffsets);
            Assert.Equal(new[] { 4, 3, 5, 2, 6, 1, 7, 0, 8 }, nineOffsets);
        }

        [Fact]
        public void NormalizeSubmissionResults_Should_Preserve_Historical_Logical_Result_Order()
        {
            var physicallyInvoked = new[]
            {
                (Iteration: 2, RunNumber: 3, Result: "wave2-run3"),
                (Iteration: 1, RunNumber: 3, Result: "wave1-run3"),
                (Iteration: 1, RunNumber: 2, Result: "wave1-run2"),
                (Iteration: 1, RunNumber: 1, Result: "wave1-run1"),
                (Iteration: 2, RunNumber: 2, Result: "wave2-run2"),
                (Iteration: 2, RunNumber: 1, Result: "wave2-run1")
            };

            var normalized =
                RuntimePoolProductionCycleExecutor.NormalizeSubmissionResults(
                    physicallyInvoked);

            Assert.Equal(
                new[]
                {
                    "wave1-run1",
                    "wave1-run2",
                    "wave1-run3",
                    "wave2-run1",
                    "wave2-run2",
                    "wave2-run3"
                },
                normalized);
        }

        [Fact]
        public void ResolveRunSubmissionOffsets_Should_Reject_Unknown_Ordering()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RuntimePoolProductionCycleExecutor.ResolveRunSubmissionOffsets(
                    5,
                    (ProductionChildDagSubmissionOrdering)999));
        }

        [Fact]
        public void SelectRecoveredSubmittedSharedRunIdsForDispatchProof_Should_Return_Only_Submitted_Recovery_And_Require_Exact_Supplemental_Set()
        {
            var submitted =
                Set(
                    "parent-1",
                    "parent-2",
                    "parent-3");

            var recovered =
                Set(
                    "parent-2",
                    "parent-3",
                    "child-continuation-child-invocation-1");

            var supplemental =
                Set(
                    "child-continuation-child-invocation-1");

            var selected =
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredSubmittedSharedRunIdsForDispatchProof(
                        submitted,
                        recovered,
                        supplemental,
                        "dispatch proof scope");

            Assert.Equal(
                Set("parent-2", "parent-3"),
                selected);
        }

        [Fact]
        public void SelectRecoveredSubmittedSharedRunIdsForDispatchProof_Should_Reject_Unexpected_NonSubmitted_Recovery()
        {
            var submitted =
                Set(
                    "parent-1",
                    "parent-2");

            var recovered =
                Set(
                    "parent-2",
                    "child-continuation-expected",
                    "unexpected-control-run");

            var supplemental =
                Set(
                    "child-continuation-expected");

            Assert.ThrowsAny<XunitException>(() =>
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredSubmittedSharedRunIdsForDispatchProof(
                        submitted,
                        recovered,
                        supplemental,
                        "unexpected supplemental recovery proof"));
        }

        [Fact]
        public void SelectRecoveredSubmittedSharedRunIdsForDispatchProof_Should_Reject_Missing_Expected_Supplemental_Recovery()
        {
            var submitted =
                Set(
                    "parent-1",
                    "parent-2");

            var recovered =
                Set(
                    "parent-2");

            var supplemental =
                Set(
                    "child-continuation-expected");

            Assert.ThrowsAny<XunitException>(() =>
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredSubmittedSharedRunIdsForDispatchProof(
                        submitted,
                        recovered,
                        supplemental,
                        "missing supplemental recovery proof"));
        }

        [Fact]
        public void SelectRecoveredSubmittedSharedRunIdsForDispatchProof_Should_Preserve_Historical_Parent_Only_Scope()
        {
            var submitted =
                Set(
                    "parent-1",
                    "parent-2");

            var recovered =
                Set(
                    "parent-2");

            var selected =
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredSubmittedSharedRunIdsForDispatchProof(
                        submitted,
                        recovered,
                        Set(),
                        "historical dispatch proof scope");

            Assert.Equal(
                Set("parent-2"),
                selected);
        }

        [Fact]
        public void SelectRecoveredExecutionIdsForExpectedProofScope_Should_Return_Only_Expected_Executions_And_Require_Exact_Supplemental_Set()
        {
            var expected =
                Set(
                    "parent-execution-1",
                    "parent-execution-2");

            var recovered =
                Set(
                    "parent-execution-2",
                    "depth-2-child-execution");

            var supplemental =
                Set(
                    "depth-2-child-execution");

            var selected =
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredExecutionIdsForExpectedProofScope(
                        expected,
                        recovered,
                        supplemental,
                        "parent logical proof scope");

            Assert.Equal(
                Set("parent-execution-2"),
                selected);
        }

        [Fact]
        public void SelectRecoveredExecutionIdsForExpectedProofScope_Should_Reject_Unexpected_OutOfScope_Recovery()
        {
            var expected =
                Set(
                    "parent-execution-1",
                    "parent-execution-2");

            var recovered =
                Set(
                    "parent-execution-2",
                    "depth-2-child-execution",
                    "unexpected-execution");

            Assert.ThrowsAny<XunitException>(() =>
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredExecutionIdsForExpectedProofScope(
                        expected,
                        recovered,
                        Set("depth-2-child-execution"),
                        "unexpected recovery scope"));
        }

        [Fact]
        public void SelectRecoveredExecutionIdsForExpectedProofScope_Should_Reject_Missing_Expected_Supplemental_Recovery()
        {
            var expected =
                Set(
                    "parent-execution-1",
                    "parent-execution-2");

            var recovered =
                Set(
                    "parent-execution-2");

            Assert.ThrowsAny<XunitException>(() =>
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredExecutionIdsForExpectedProofScope(
                        expected,
                        recovered,
                        Set("depth-2-child-execution"),
                        "missing expected recovery scope"));
        }

        [Fact]
        public void SelectRecoveredExecutionIdsForExpectedProofScope_Should_Preserve_Historical_Parent_Only_Scope()
        {
            var expected =
                Set(
                    "parent-execution-1",
                    "parent-execution-2");

            var recovered =
                Set(
                    "parent-execution-2");

            var selected =
                RuntimePoolProductionCycleExecutor
                    .SelectRecoveredExecutionIdsForExpectedProofScope(
                        expected,
                        recovered,
                        Set(),
                        "historical parent proof scope");

            Assert.Equal(
                Set("parent-execution-2"),
                selected);
        }

        private static IReadOnlySet<string> Set(
            params string[] values)
        {
            return values.ToHashSet(StringComparer.Ordinal);
        }
    }
}
