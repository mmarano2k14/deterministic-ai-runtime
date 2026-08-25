using System;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Verifies the deterministic adversarial schedule contract without starting a runtime pool.
    /// </summary>
    public sealed class ProductionChildDagAdversarialScheduleDefinitionTests
    {
        /// <summary>
        /// Verifies that the explicit A0 schedule is exactly equivalent in semantic values to the historical
        /// hard-coded baseline coordinates.
        /// </summary>
        [Fact]
        public void Baseline_Should_Preserve_Historical_Production_Failure_Coordinates()
        {
            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Baseline;

            Assert.Equal("baseline", schedule.MatrixScenarioId);
            Assert.Equal("baseline", schedule.FailureSeed);
            Assert.Equal("DeterministicBaseline", schedule.FailureScheduleMode);
            Assert.Equal("mid-parent", schedule.FailurePosition);
            Assert.Equal("NOT_YET_VALIDATED", schedule.MatrixStatus);
            Assert.Equal(25, schedule.KillAfterCompletedStepCount);
            Assert.Equal(26, schedule.ResolveCrashCheckpointStepIndex(50));
        }

        /// <summary>
        /// Verifies that A1 uses the earliest crash coordinate already supported by the existing Child DAG
        /// failure contract: one durable completed step followed by checkpoint step two.
        /// </summary>
        [Fact]
        public void CrashEarly_Should_Use_The_Earliest_Existing_Supported_Checkpoint()
        {
            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly;

            Assert.Equal("crash-early", schedule.MatrixScenarioId);
            Assert.Equal("crash-early", schedule.FailureSeed);
            Assert.Equal("DeterministicAdversarial", schedule.FailureScheduleMode);
            Assert.Equal("early-parent", schedule.FailurePosition);
            Assert.Equal("IN_PROGRESS", schedule.MatrixStatus);
            Assert.Equal(1, schedule.KillAfterCompletedStepCount);
            Assert.Equal(2, schedule.ResolveCrashCheckpointStepIndex(50));
        }

        /// <summary>
        /// Verifies that introducing A1 does not mutate the frozen A0 schedule.
        /// </summary>
        [Fact]
        public void CrashEarly_Should_Not_Mutate_The_Frozen_Baseline_Coordinates()
        {
            var baseline =
                ProductionChildDagAdversarialScheduleDefinition.Baseline;
            var crashEarly =
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly;

            Assert.Equal(25, baseline.KillAfterCompletedStepCount);
            Assert.Equal(26, baseline.ResolveCrashCheckpointStepIndex(50));

            Assert.Equal(1, crashEarly.KillAfterCompletedStepCount);
            Assert.Equal(2, crashEarly.ResolveCrashCheckpointStepIndex(50));

            Assert.NotEqual(
                baseline.MatrixScenarioId,
                crashEarly.MatrixScenarioId);
        }

        /// <summary>
        /// Verifies that the child-invocation-boundary schedule stops at the final ordinary parent checkpoint
        /// immediately before the ExecuteChildDag call-site can become runnable.
        /// </summary>
        [Fact]
        public void ChildInvocationBoundary_Should_Stop_At_The_Final_Ordinary_Parent_Checkpoint()
        {
            const int productionParentStepCount = 50;

            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.ChildInvocationBoundary;

            Assert.Equal("child-invocation-boundary", schedule.MatrixScenarioId);
            Assert.Equal("child-invocation-boundary", schedule.FailureSeed);
            Assert.Equal("DeterministicAdversarial", schedule.FailureScheduleMode);
            Assert.Equal("pre-child-invocation", schedule.FailurePosition);
            Assert.Equal("IN_PROGRESS", schedule.MatrixStatus);
            Assert.Equal(productionParentStepCount - 1, schedule.KillAfterCompletedStepCount);
            Assert.Equal(
                productionParentStepCount,
                schedule.ResolveCrashCheckpointStepIndex(productionParentStepCount));
        }

        /// <summary>
        /// Verifies that introducing the child-invocation-boundary row leaves the already-proven baseline and
        /// crash-early coordinates unchanged.
        /// </summary>
        [Fact]
        public void ChildInvocationBoundary_Should_Not_Mutate_Previous_Matrix_Coordinates()
        {
            var baseline =
                ProductionChildDagAdversarialScheduleDefinition.Baseline;
            var crashEarly =
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly;
            var childInvocationBoundary =
                ProductionChildDagAdversarialScheduleDefinition.ChildInvocationBoundary;

            Assert.Equal(25, baseline.KillAfterCompletedStepCount);
            Assert.Equal(26, baseline.ResolveCrashCheckpointStepIndex(50));

            Assert.Equal(1, crashEarly.KillAfterCompletedStepCount);
            Assert.Equal(2, crashEarly.ResolveCrashCheckpointStepIndex(50));

            Assert.Equal(49, childInvocationBoundary.KillAfterCompletedStepCount);
            Assert.Equal(50, childInvocationBoundary.ResolveCrashCheckpointStepIndex(50));
        }

        /// <summary>
        /// Verifies that a schedule cannot place the crash checkpoint outside the ordinary parent-step range.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        [InlineData(51)]
        public void ResolveCrashCheckpointStepIndex_Should_Reject_Invalid_Progress_Boundaries(
            int killAfterCompletedStepCount)
        {
            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Baseline with
                {
                    KillAfterCompletedStepCount =
                        killAfterCompletedStepCount
                };

            Assert.Throws<ArgumentOutOfRangeException>(
                () => schedule.ResolveCrashCheckpointStepIndex(50));
        }
    }
}
