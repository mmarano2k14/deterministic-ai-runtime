using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Verifies recursive Child DAG logical-step expectations used by the production reference proofs.
    /// </summary>
    public sealed class ProductionChildDagStepLedgerAssertionsTests
    {
        /// <summary>
        /// Verifies that every non-deepest child includes its own execute-child-dag logical step while the deepest child does not.
        /// </summary>
        [Theory]
        [InlineData(50, 1, 1, 50)]
        [InlineData(50, 2, 1, 51)]
        [InlineData(50, 2, 2, 50)]
        [InlineData(50, 3, 1, 51)]
        [InlineData(50, 3, 2, 51)]
        [InlineData(50, 3, 3, 50)]
        public void GetExpectedLogicalStepCountAtDepth_Should_Match_Recursive_Pipeline_Shape(
            int baseStepCount,
            int childDepth,
            int depth,
            int expectedStepCount)
        {
            var actual =
                ProductionChildDagStepLedgerAssertions
                    .GetExpectedLogicalStepCountAtDepth(
                        baseStepCount,
                        childDepth,
                        depth);

            Assert.Equal(expectedStepCount, actual);
        }

        /// <summary>
        /// Verifies that proof depth cannot escape the configured recursive Child DAG range.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void GetExpectedLogicalStepCountAtDepth_Should_Reject_Depth_Outside_Configured_Range(
            int depth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ProductionChildDagStepLedgerAssertions
                    .GetExpectedLogicalStepCountAtDepth(
                        baseStepCount: 50,
                        childDepth: 3,
                        depth));
        }
    }
}
