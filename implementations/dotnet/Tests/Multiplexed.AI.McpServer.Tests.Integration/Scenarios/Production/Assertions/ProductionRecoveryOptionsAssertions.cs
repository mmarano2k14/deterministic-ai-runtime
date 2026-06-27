using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Provides reusable assertions for production runtime recovery options.
    /// </summary>
    public static class ProductionRecoveryOptionsAssertions
    {
        /// <summary>
        /// Verifies that runtime execution recovery options are enabled for DAG resume recovery scenarios.
        /// </summary>
        /// <param name="options">The recovery reconciliation options.</param>
        public static void AssertDagResumeRecoveryEnabled(
            AiRuntimeExecutionRecoveryReconciliationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            Assert.True(
                options.Enabled,
                "Runtime execution recovery must be enabled for this test.");

            Assert.True(
                options.IncludeUnhealthyRuntimeInstances,
                "Runtime execution recovery must scan unhealthy runtime instances.");

            Assert.True(
                options.IncludeStoppedRuntimeInstances,
                "Runtime execution recovery must scan stopped runtime instances.");

            Assert.True(
                options.IncludeDrainingRuntimeInstances,
                "Runtime execution recovery must scan draining runtime instances.");

            Assert.True(
                options.RequeueUnfinishedRuns,
                "Runtime execution recovery must requeue unfinished runs.");

            Assert.True(
                options.EnableDagExecutionResume,
                "DAG resume recovery must be enabled for this test.");

            Assert.False(
                options.DryRun,
                "Runtime execution recovery must not run in dry-run mode for this test.");
        }
    }
}