using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="StoreBackedAiRuntimeScaleOutRequestPublisher" />.
    /// </summary>
    public sealed class StoreBackedAiRuntimeScaleOutRequestPublisherTests
    {
        /// <summary>
        /// Verifies that publishing a scale-out request persists a request record.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Persist_ScaleOut_Request()
        {
            var store = new InMemoryAiRuntimeScaleOutRequestStore();
            var resolver = new TestControlPlaneIdResolver("cp-resolved");
            var publisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(store, resolver);

            var request = CreateRequest(controlPlaneId: "cp-shared-run");

            var result = await publisher.PublishAsync(request);

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("scale-out-shared-run-1", result.ScaleOutRequestId);
            Assert.Equal(2, result.RequestedTargetInstanceCount);

            var record = await store.GetAsync("scale-out-shared-run-1");

            Assert.NotNull(record);
            Assert.Equal("cp-shared-run", record.ControlPlaneId);
            Assert.Equal("shared-run-1", record.SharedRunId);
            Assert.Equal("tenant-test", record.TenantId);
            Assert.Equal("pipeline-test", record.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, record.Status);
            Assert.Equal(3, record.VisibleInstanceCount);
            Assert.Equal(0, record.AvailableInstanceCount);
            Assert.Equal(1, record.CurrentInstanceCount);
            Assert.Equal(5, record.MaxInstanceCount);
            Assert.Equal(2, record.RequestedTargetInstanceCount);
            Assert.Equal("correlation-test", record.CorrelationId);
            Assert.Equal("unit-test", record.RequestedBy);
            Assert.Equal("unit-test", record.Source);
            Assert.Equal("No runtime capacity was available for admission.", record.Reason);
            Assert.Equal("cp-shared-run", record.Metadata["controlPlaneId"]);
            Assert.Equal("shared-run-1", record.Metadata["sharedRunId"]);
        }

        /// <summary>
        /// Verifies that the publisher uses the resolver when the shared run has no control-plane identifier.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Use_Resolver_When_SharedRun_ControlPlaneId_Is_Missing()
        {
            var store = new InMemoryAiRuntimeScaleOutRequestStore();
            var resolver = new TestControlPlaneIdResolver("cp-resolved");
            var publisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(store, resolver);

            var request = CreateRequest(controlPlaneId: null);

            var result = await publisher.PublishAsync(request);

            Assert.True(result.Success);

            var record = await store.GetAsync("scale-out-shared-run-1");

            Assert.NotNull(record);
            Assert.Equal("cp-resolved", record.ControlPlaneId);
            Assert.Equal("cp-resolved", record.Metadata["controlPlaneId"]);
        }

        /// <summary>
        /// Verifies that publishing fails clearly when no control-plane identifier can be resolved.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Throw_When_ControlPlaneId_Cannot_Be_Resolved()
        {
            var store = new InMemoryAiRuntimeScaleOutRequestStore();
            var resolver = new TestControlPlaneIdResolver(null);
            var publisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(store, resolver);

            var request = CreateRequest(controlPlaneId: null);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                publisher.PublishAsync(request));
        }

        /// <summary>
        /// Verifies that the requested target instance count does not exceed the maximum instance count.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Cap_Target_Instance_Count_To_Max_Instance_Count()
        {
            var store = new InMemoryAiRuntimeScaleOutRequestStore();
            var resolver = new TestControlPlaneIdResolver("cp-resolved");
            var publisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(store, resolver);

            var request = CreateRequest(
                controlPlaneId: "cp-shared-run",
                currentInstanceCount: 5,
                maxInstanceCount: 5);

            var result = await publisher.PublishAsync(request);

            Assert.True(result.Success);
            Assert.Equal(5, result.RequestedTargetInstanceCount);

            var record = await store.GetAsync("scale-out-shared-run-1");

            Assert.NotNull(record);
            Assert.Equal(5, record.RequestedTargetInstanceCount);
        }

        /// <summary>
        /// Verifies that repeated publishes for the same shared run return the same persisted request.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Deduplicate_Repeated_SharedRun_Request()
        {
            var store = new InMemoryAiRuntimeScaleOutRequestStore();
            var resolver = new TestControlPlaneIdResolver("cp-resolved");
            var publisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(store, resolver);

            var request = CreateRequest(controlPlaneId: "cp-shared-run");

            var first = await publisher.PublishAsync(request);
            var second = await publisher.PublishAsync(request);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(first.ScaleOutRequestId, second.ScaleOutRequestId);

            var pending = await store.ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-shared-run"
            });

            Assert.Single(pending);
        }

        /// <summary>
        /// Creates a scale-out request for tests.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier assigned to the shared run.</param>
        /// <param name="currentInstanceCount">The current runtime instance count.</param>
        /// <param name="maxInstanceCount">The maximum runtime instance count.</param>
        /// <returns>The created scale-out request.</returns>
        private static AiRuntimeScaleOutRequest CreateRequest(
            string? controlPlaneId,
            int currentInstanceCount = 1,
            int? maxInstanceCount = 5)
        {
            var sharedRun = new AiSharedRunRecord
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedRunStatus.ScaleOutRequested,
                RunRequest = CreateRunRequest(),
                TenantId = "tenant-test",
                PipelineKey = "pipeline-test",
                CorrelationId = "correlation-test",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "No runtime capacity was available for admission.",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ControlPlaneId = controlPlaneId,
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            };

            return new AiRuntimeScaleOutRequest
            {
                SharedRun = sharedRun,
                SharedRunId = sharedRun.SharedRunId,
                TenantId = sharedRun.TenantId,
                PipelineKey = sharedRun.PipelineKey,
                VisibleInstanceCount = 3,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = currentInstanceCount,
                MaxInstanceCount = maxInstanceCount,
                CorrelationId = sharedRun.CorrelationId,
                RequestedBy = sharedRun.RequestedBy,
                Source = sharedRun.Source,
                Reason = sharedRun.Reason,
                Metadata = sharedRun.Metadata
            };
        }

        /// <summary>
        /// Creates a minimal runtime pipeline run request for tests.
        /// </summary>
        /// <returns>The created run request.</returns>
        /// <summary>
        /// Creates a minimal runtime pipeline run request for tests.
        /// </summary>
        /// <returns>The created run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest()
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-test",
                Input = new Dictionary<string, object?>
                {
                    ["test"] = true
                }
            };
        }

        /// <summary>
        /// Provides a test implementation of the control-plane id resolver.
        /// </summary>
        private sealed class TestControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            /// <summary>
            /// The control-plane identifier returned by the resolver.
            /// </summary>
            private readonly string? controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestControlPlaneIdResolver" /> class.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane identifier to return.</param>
            public TestControlPlaneIdResolver(string? controlPlaneId)
            {
                this.controlPlaneId = controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string?> ResolveAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(this.controlPlaneId);
            }
        }
    }
}