using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    public sealed class NoopAiRuntimeScaleOutRequestPublisherTests
    {
        [Fact]
        public async Task PublishAsync_Should_Acknowledge_ScaleOut_Request()
        {
            var publisher = new NoopAiRuntimeScaleOutRequestPublisher();

            var result = await publisher.PublishAsync(
                CreateRequest(
                    currentInstanceCount: 1,
                    maxInstanceCount: 3));

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("noop-scale-out-shared-run-1", result.ScaleOutRequestId);
            Assert.Equal(2, result.RequestedTargetInstanceCount);
            Assert.Equal("Scale-out request acknowledged by no-op publisher.", result.Message);
            Assert.NotEqual(default, result.PublishedAtUtc);
        }

        [Fact]
        public async Task PublishAsync_Should_Not_Exceed_Max_Instance_Count()
        {
            var publisher = new NoopAiRuntimeScaleOutRequestPublisher();

            var result = await publisher.PublishAsync(
                CreateRequest(
                    currentInstanceCount: 3,
                    maxInstanceCount: 3));

            Assert.True(result.Success);
            Assert.Equal(3, result.RequestedTargetInstanceCount);
        }

        [Fact]
        public async Task PublishAsync_Should_Increment_When_Max_Instance_Count_Is_Not_Set()
        {
            var publisher = new NoopAiRuntimeScaleOutRequestPublisher();

            var result = await publisher.PublishAsync(
                CreateRequest(
                    currentInstanceCount: 5,
                    maxInstanceCount: null));

            Assert.True(result.Success);
            Assert.Equal(6, result.RequestedTargetInstanceCount);
        }

        [Fact]
        public async Task PublishAsync_Should_Throw_When_Request_Is_Null()
        {
            var publisher = new NoopAiRuntimeScaleOutRequestPublisher();

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                publisher.PublishAsync(null!));
        }

        [Fact]
        public async Task PublishAsync_Should_Throw_When_SharedRunId_Is_Missing()
        {
            var publisher = new NoopAiRuntimeScaleOutRequestPublisher();

            await Assert.ThrowsAsync<ArgumentException>(() =>
                publisher.PublishAsync(CreateRequest(
                    sharedRunId: " ",
                    currentInstanceCount: 1,
                    maxInstanceCount: 3)));
        }

        private static AiRuntimeScaleOutRequest CreateRequest(
            string sharedRunId = "shared-run-1",
            int currentInstanceCount = 1,
            int? maxInstanceCount = 3)
        {
            var now = DateTimeOffset.UtcNow;

            var sharedRun = new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.ScaleOutRequested,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                },
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: "tenant-1"),
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>()
            };

            return new AiRuntimeScaleOutRequest
            {
                SharedRun = sharedRun,
                SharedRunId = sharedRunId,
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1",
                VisibleInstanceCount = currentInstanceCount,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = currentInstanceCount,
                MaxInstanceCount = maxInstanceCount,
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "Scale-out required.",
                Metadata = new Dictionary<string, string>()
            };
        }
    }
}