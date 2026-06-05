using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;
using Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing;
using Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.RuntimeInstances.Providers.Testing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Unit tests for <see cref="LocalAiRuntimeInstanceProvider"/>.
    /// </summary>
    public sealed class LocalAiRuntimeInstanceProviderTests
    {
        /// <summary>
        /// Verifies that dispatch is routed to the resolved shared runtime instance.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WithRegisteredInstance_ShouldRouteToSharedRuntimeInstance()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var sharedInstance = new TestSharedRuntimeInstance(runtimeInstanceId, queueControlPlane);
            var registry = new TestSharedRuntimeInstanceRegistry();

            await registry.RegisterAsync(sharedInstance);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.DispatchAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateDispatchRequest(runtimeInstanceId),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(1, sharedInstance.DispatchCallCount);
        }

        /// <summary>
        /// Verifies that run status is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.GetRunStatusAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetRunStatus, result.Operation);
            Assert.Equal(1, queueControlPlane.GetRunStatusCallCount);
        }

        /// <summary>
        /// Verifies that queue status is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task GetQueueStatusAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.GetQueueStatusAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetQueueStatus, result.Operation);
            Assert.Equal(1, queueControlPlane.GetQueueStatusCallCount);
        }

        /// <summary>
        /// Verifies that pause queue is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task PauseQueueAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.PauseQueueAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.PauseQueue),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.PauseQueue, result.Operation);
            Assert.Equal(1, queueControlPlane.PauseQueueCallCount);
        }

        /// <summary>
        /// Verifies that resume queue is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task ResumeQueueAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.ResumeQueueAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.ResumeQueue, result.Operation);
            Assert.Equal(1, queueControlPlane.ResumeQueueCallCount);
        }

        /// <summary>
        /// Verifies that cancel run is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task CancelRunAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.CancelRunAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.CancelRun),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelRun, result.Operation);
            Assert.Equal(1, queueControlPlane.CancelRunCallCount);
        }

        /// <summary>
        /// Verifies that cancel queued run is routed to the target instance queue control-plane.
        /// </summary>
        [Fact]
        public async Task CancelQueuedRunAsync_WithRegisteredInstance_ShouldRouteToQueueControlPlane()
        {
            var runtimeInstanceId = "mcp-runtime-1";
            var queueControlPlane = new TestRuntimeQueueControlPlane();
            var registry = await CreateRegistryWithInstanceAsync(runtimeInstanceId, queueControlPlane);

            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.CancelQueuedRunAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelQueuedRun, result.Operation);
            Assert.Equal(1, queueControlPlane.CancelQueuedRunCallCount);
        }

        /// <summary>
        /// Verifies that missing runtime instances return a failed queue result instead of throwing.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WithMissingInstance_ShouldReturnFailureResult()
        {
            var runtimeInstanceId = "mcp-runtime-missing";
            var registry = new TestSharedRuntimeInstanceRegistry();
            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.GetRunStatusAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateQueueRequest(
                    runtimeInstanceId,
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("runtime-instance-not-registered", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that missing runtime instances return a failed dispatch result instead of throwing.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WithMissingInstance_ShouldReturnFailureResult()
        {
            var runtimeInstanceId = "mcp-runtime-missing";
            var registry = new TestSharedRuntimeInstanceRegistry();
            var provider = new LocalAiRuntimeInstanceProvider(registry);

            var result = await provider.DispatchAsync(
                CreateDescriptor(runtimeInstanceId),
                CreateDispatchRequest(runtimeInstanceId),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("runtime-instance-not-registered", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that local provider rejects non-local provider metadata.
        /// </summary>
        [Fact]
        public void CanHandle_WithNonLocalProviderMetadata_ShouldReturnFalse()
        {
            var provider = new LocalAiRuntimeInstanceProvider(
                new TestSharedRuntimeInstanceRegistry());

            var descriptor = new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "mcp-runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "kubernetes"
                }
            };

            var canHandle = provider.CanHandle(descriptor);

            Assert.False(canHandle);
        }

        /// <summary>
        /// Creates a test registry with one shared runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="queueControlPlane">The queue control-plane.</param>
        /// <returns>The test registry.</returns>
        private static async Task<TestSharedRuntimeInstanceRegistry> CreateRegistryWithInstanceAsync(
            string runtimeInstanceId,
            IAiRuntimeQueueControlPlane queueControlPlane)
        {
            var registry = new TestSharedRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                new TestSharedRuntimeInstance(
                    runtimeInstanceId,
                    queueControlPlane));

            return registry;
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The runtime instance capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                }
            };
        }

        /// <summary>
        /// Creates a runtime queue control-plane request.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="operation">The queue operation.</param>
        /// <returns>The runtime queue control-plane request.</returns>
        private static AiRuntimeQueueControlPlaneRequest CreateQueueRequest(
            string runtimeInstanceId,
            AiRuntimeQueueControlPlaneOperation operation)
        {
            return new AiRuntimeQueueControlPlaneRequest
            {
                Operation = operation,
                RuntimeInstanceId = runtimeInstanceId,
                RunId = "local-run-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test"
            };
        }

        /// <summary>
        /// Creates a shared runtime instance dispatch request.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The shared runtime instance dispatch request.</returns>
        private static AiSharedRuntimeInstanceDispatchRequest CreateDispatchRequest(
            string runtimeInstanceId)
        {
            var runRequest = new AiRuntimePipelineRunRequest
            {
                PipelineName = "test-pipeline"
            };

            return new AiSharedRuntimeInstanceDispatchRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                SharedRun = new AiSharedRunRecord
                {
                    SharedRunId = "shared-run-1",
                    Status = AiSharedRunStatus.Submitted,
                    RunRequest = runRequest,
                    PipelineKey = "test-pipeline",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                RunRequest = runRequest,
                ClaimToken = "claim-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test"
            };
        }
    }
}