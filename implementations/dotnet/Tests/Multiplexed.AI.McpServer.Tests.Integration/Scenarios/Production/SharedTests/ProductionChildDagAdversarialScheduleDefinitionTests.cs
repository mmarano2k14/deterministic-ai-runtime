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
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint, schedule.FailureTarget);
            Assert.True(schedule.UsesParentCrashCheckpoint);
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
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint, schedule.FailureTarget);
            Assert.True(schedule.UsesParentCrashCheckpoint);
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
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint, schedule.FailureTarget);
            Assert.True(schedule.UsesParentCrashCheckpoint);
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
        /// Verifies that continuation-consume uses durable continuation state instead of an ordinary parent crash checkpoint.
        /// </summary>
        [Fact]
        public void ContinuationConsume_Should_Use_The_Durable_Continuation_Target()
        {
            var schedule = ProductionChildDagAdversarialScheduleDefinition.ContinuationConsume;

            Assert.Equal("continuation-consume", schedule.MatrixScenarioId);
            Assert.Equal("continuation-consume", schedule.FailureSeed);
            Assert.Equal("DeterministicAdversarial", schedule.FailureScheduleMode);
            Assert.Equal("continuation-consume", schedule.FailurePosition);
            Assert.Equal("IN_PROGRESS", schedule.MatrixStatus);
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.ContinuationConsume, schedule.FailureTarget);
            Assert.False(schedule.UsesParentCrashCheckpoint);
            Assert.Equal(50, schedule.KillAfterCompletedStepCount);
        }

        /// <summary>
        /// Verifies that continuation-consume cannot be projected through the ordinary parent checkpoint resolver.
        /// </summary>
        [Fact]
        public void ContinuationConsume_Should_Reject_Ordinary_Parent_Crash_Checkpoint_Resolution()
        {
            var schedule = ProductionChildDagAdversarialScheduleDefinition.ContinuationConsume;

            Assert.Throws<InvalidOperationException>(
                () => schedule.ResolveCrashCheckpointStepIndex(50));
        }

        /// <summary>
        /// Verifies that B1 targets the exact Depth 2 child and uses child step two as the deterministic physical
        /// runtime failure window without projecting through the ordinary parent checkpoint resolver.
        /// </summary>
        [Fact]
        public void Depth2RuntimeFailure_Should_Target_Exact_Recursive_Depth_Two_Checkpoint()
        {
            const int configuredChildDepth = 3;
            const int pipelineStepCount = 50;

            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Depth2RuntimeFailure;

            Assert.Equal("depth2-runtime-failure", schedule.MatrixScenarioId);
            Assert.Equal("depth2-runtime-failure", schedule.FailureSeed);
            Assert.Equal("DeterministicAdversarial", schedule.FailureScheduleMode);
            Assert.Equal("depth2-child-runtime", schedule.FailurePosition);
            Assert.Equal("IN_PROGRESS", schedule.MatrixStatus);
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.RecursiveChildRuntime, schedule.FailureTarget);
            Assert.False(schedule.UsesParentCrashCheckpoint);
            Assert.True(schedule.UsesRecursiveChildCrashCheckpoint);
            Assert.Equal(2, schedule.TargetRecursiveDepth);
            Assert.Equal(1, schedule.KillAfterCompletedStepCount);
            Assert.Equal(
                2,
                schedule.ResolveRecursiveChildCrashCheckpointStepIndex(
                    configuredChildDepth,
                    pipelineStepCount));
            Assert.Throws<InvalidOperationException>(
                () => schedule.ResolveCrashCheckpointStepIndex(pipelineStepCount));
        }

        /// <summary>
        /// Verifies that B1 cannot silently target a recursive depth outside the configured Child DAG chain.
        /// </summary>
        [Fact]
        public void Depth2RuntimeFailure_Should_Reject_Insufficient_Configured_Child_Depth()
        {
            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Depth2RuntimeFailure;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => schedule.ResolveRecursiveChildCrashCheckpointStepIndex(1, 50));
        }

        /// <summary>
        /// Verifies that B2 targets the exact deepest Depth 3 child at the same durable child checkpoint used by
        /// B1 while preserving the recursive-child physical-failure contract.
        /// </summary>
        [Fact]
        public void Depth3RuntimeFailure_Should_Target_Exact_Recursive_Depth_Three_Checkpoint()
        {
            const int configuredChildDepth = 3;
            const int pipelineStepCount = 50;

            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Depth3RuntimeFailure;

            Assert.Equal("depth3-runtime-failure", schedule.MatrixScenarioId);
            Assert.Equal("depth3-runtime-failure", schedule.FailureSeed);
            Assert.Equal("DeterministicAdversarial", schedule.FailureScheduleMode);
            Assert.Equal("depth3-child-runtime", schedule.FailurePosition);
            Assert.Equal("IN_PROGRESS", schedule.MatrixStatus);
            Assert.Equal(ProductionChildDagAdversarialFailureTarget.RecursiveChildRuntime, schedule.FailureTarget);
            Assert.False(schedule.UsesParentCrashCheckpoint);
            Assert.True(schedule.UsesRecursiveChildCrashCheckpoint);
            Assert.Equal(3, schedule.TargetRecursiveDepth);
            Assert.Equal(1, schedule.KillAfterCompletedStepCount);
            Assert.Equal(
                2,
                schedule.ResolveRecursiveChildCrashCheckpointStepIndex(
                    configuredChildDepth,
                    pipelineStepCount));
            Assert.Throws<InvalidOperationException>(
                () => schedule.ResolveCrashCheckpointStepIndex(pipelineStepCount));
        }

        /// <summary>
        /// Verifies that B2 cannot target Depth 3 when the configured recursive chain stops at Depth 2.
        /// </summary>
        [Fact]
        public void Depth3RuntimeFailure_Should_Reject_Insufficient_Configured_Child_Depth()
        {
            var schedule =
                ProductionChildDagAdversarialScheduleDefinition.Depth3RuntimeFailure;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => schedule.ResolveRecursiveChildCrashCheckpointStepIndex(2, 50));
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
