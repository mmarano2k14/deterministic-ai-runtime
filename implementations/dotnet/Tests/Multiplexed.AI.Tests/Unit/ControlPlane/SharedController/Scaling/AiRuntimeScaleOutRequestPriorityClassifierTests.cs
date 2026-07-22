using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="AiRuntimeScaleOutRequestPriorityClassifier" />.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestPriorityClassifierTests
    {
        /// <summary>
        /// Verifies that generic recovery control-plane aliases do not promote an
        /// ordinary initial scale-out request to recovery priority.
        /// </summary>
        [Fact]
        public void IsRecoveryRequest_Should_Not_Classify_Generic_ControlPlane_Aliases_As_Recovery()
        {
            var metadataKeys = new[]
            {
                "recovery.control-plane.id",
                "recovery.controlPlaneId",
                "recovery.controlplane.id",
                "runtime.control-plane.id",
                "scenario.control-plane.id"
            };

            var result =
                AiRuntimeScaleOutRequestPriorityClassifier.IsRecoveryRequest(
                    "scale-out-ordinary-shared-run",
                    metadataKeys,
                    "integration-test",
                    "No runtime instance can currently accept the run and scale-out is allowed.");

            Assert.False(result);
        }

        /// <summary>
        /// Verifies redispatch request identity classification.
        /// </summary>
        [Fact]
        public void IsRecoveryRequest_Should_Classify_Redispatch_RequestId_As_Recovery()
        {
            var result =
                AiRuntimeScaleOutRequestPriorityClassifier.IsRecoveryRequest(
                    "scale-out-redispatch-shared-run-generation",
                    Array.Empty<string>(),
                    "integration-test",
                    "scale-out requested");

            Assert.True(result);
        }

        /// <summary>
        /// Verifies work-specific recovery metadata classification.
        /// </summary>
        [Theory]
        [InlineData("failed.runtimeInstanceId")]
        [InlineData("recovery.failedExecutionId")]
        [InlineData("recovery.failedLocalRunId")]
        [InlineData("recovery.mode")]
        [InlineData("recovery.reason")]
        [InlineData("recovery.forensicsId")]
        [InlineData("recovery.runtimeFailureIncidentId")]
        public void IsRecoveryRequest_Should_Classify_WorkSpecific_Recovery_Metadata(
            string metadataKey)
        {
            var result =
                AiRuntimeScaleOutRequestPriorityClassifier.IsRecoveryRequest(
                    "scale-out-request",
                    new[] { metadataKey },
                    "integration-test",
                    "scale-out requested");

            Assert.True(result);
        }
    }
}
