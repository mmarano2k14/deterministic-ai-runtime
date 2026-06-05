using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.RuntimeInstances.Providers
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

        /// <summary>
        /// Test shared runtime instance registry.
        /// </summary>
        private sealed class TestSharedRuntimeInstanceRegistry : IAiSharedRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, IAiSharedRuntimeInstance> instances =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task RegisterAsync(
                IAiSharedRuntimeInstance instance,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(instance);

                instances[instance.RuntimeInstanceId] = instance;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<IAiSharedRuntimeInstance?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                instances.TryGetValue(
                    runtimeInstanceId,
                    out var instance);

                return Task.FromResult(instance);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<IAiSharedRuntimeInstance>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                IReadOnlyCollection<IAiSharedRuntimeInstance> result =
                    instances.Values.ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<bool> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                return Task.FromResult(
                    instances.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Test shared runtime instance.
        /// </summary>
        private sealed class TestSharedRuntimeInstance : IAiSharedRuntimeInstance
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestSharedRuntimeInstance"/> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <param name="queueControlPlane">The queue control-plane.</param>
            public TestSharedRuntimeInstance(
                string runtimeInstanceId,
                IAiRuntimeQueueControlPlane queueControlPlane)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                ArgumentNullException.ThrowIfNull(queueControlPlane);

                RuntimeInstanceId = runtimeInstanceId;
                QueueControlPlane = queueControlPlane;
            }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

            /// <summary>
            /// Gets the number of dispatch calls received by this instance.
            /// </summary>
            public int DispatchCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                DispatchCallCount++;

                var now =
                    DateTimeOffset.UtcNow;

                return Task.FromResult(
                    new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = true,
                        RuntimeInstanceId = RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        LocalRunId = "local-run-1",
                        ExecutionId = "execution-1",
                        ClaimToken = request.ClaimToken,
                        StartedAtUtc = now,
                        CompletedAtUtc = now,
                        DurationMs = 0
                    });
            }
        }

        /// <summary>
        /// Test runtime queue control-plane.
        /// </summary>
        private sealed class TestRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            /// <summary>
            /// Gets the number of get-run-status calls.
            /// </summary>
            public int GetRunStatusCallCount { get; private set; }

            /// <summary>
            /// Gets the number of get-queue-status calls.
            /// </summary>
            public int GetQueueStatusCallCount { get; private set; }

            /// <summary>
            /// Gets the number of pause-queue calls.
            /// </summary>
            public int PauseQueueCallCount { get; private set; }

            /// <summary>
            /// Gets the number of resume-queue calls.
            /// </summary>
            public int ResumeQueueCallCount { get; private set; }

            /// <summary>
            /// Gets the number of cancel-run calls.
            /// </summary>
            public int CancelRunCallCount { get; private set; }

            /// <summary>
            /// Gets the number of cancel-queued-run calls.
            /// </summary>
            public int CancelQueuedRunCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                return request.Operation switch
                {
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus =>
                        GetRunStatusAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus =>
                        GetQueueStatusAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.PauseQueue =>
                        PauseQueueAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.ResumeQueue =>
                        ResumeQueueAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.CancelRun =>
                        CancelRunAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun =>
                        CancelQueuedRunAsync(request, cancellationToken),

                    AiRuntimeQueueControlPlaneOperation.EnqueueRun =>
                        EnqueueRunAsync(request, cancellationToken),

                    _ => throw new NotSupportedException(
                        $"Operation '{request.Operation}' is not supported by this test control-plane.")
                };
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                CancelRunCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                CancelQueuedRunCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                PauseQueueCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                ResumeQueueCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                GetRunStatusCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                GetQueueStatusCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <summary>
            /// Creates a successful runtime queue control-plane result.
            /// </summary>
            /// <param name="request">The runtime queue request.</param>
            /// <returns>The runtime queue result.</returns>
            private static AiRuntimeQueueControlPlaneResult CreateResult(
                AiRuntimeQueueControlPlaneRequest request)
            {
                var now =
                    DateTimeOffset.UtcNow;

                return new AiRuntimeQueueControlPlaneResult
                {
                    Operation = request.Operation,
                    Success = true,
                    Message = "Test operation completed.",
                    RunId = request.RunId,
                    CorrelationId = request.CorrelationId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0
                };
            }
        }
    }
}