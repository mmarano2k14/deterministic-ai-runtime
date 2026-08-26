using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Xunit;
using Xunit.Sdk;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    public sealed class RuntimePoolProductionCycleExecutorTests
    {
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

        private static IReadOnlySet<string> Set(
            params string[] values)
        {
            return values.ToHashSet(StringComparer.Ordinal);
        }
    }
}
