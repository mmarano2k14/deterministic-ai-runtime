using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling.Redis;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides integration tests for shared runtime controller scale-out request persistence.
    /// </summary>
    public sealed class AiSharedRuntimeControllerRedisScaleOutRequestTests : IAsyncLifetime
    {
        /// <summary>
        /// Redis key prefix used by this test instance.
        /// </summary>
        private readonly string keyPrefix = $"ai-test-{Guid.NewGuid():N}";

        /// <summary>
        /// Redis connection used by the tests.
        /// </summary>
        private IConnectionMultiplexer? connection;

        /// <summary>
        /// Scale-out request store used by the controller publisher.
        /// </summary>
        private RedisAiRuntimeScaleOutRequestStore? scaleOutStore;

        /// <summary>
        /// Initializes Redis connection and scale-out request store.
        /// </summary>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public async Task InitializeAsync()
        {
            this.connection = await ConnectionMultiplexer
                .ConnectAsync(GetRedisConnectionString())
                .ConfigureAwait(false);

            this.scaleOutStore = new RedisAiRuntimeScaleOutRequestStore(
                this.connection,
                Options.Create(new RedisAiRuntimeScaleOutRequestStoreOptions
                {
                    KeyPrefix = this.keyPrefix,
                    DefaultTtl = TimeSpan.FromMinutes(2),
                    DeduplicationWindow = TimeSpan.FromSeconds(30),
                    EnableDeduplication = true,
                    MaxListResults = 100,
                    DefaultIndexScanLimit = 1_000
                }),
                new RedisAiRuntimeScaleOutRequestStoreScriptCache(this.connection));
        }

        /// <summary>
        /// Disposes Redis connection resources used by the test.
        /// </summary>
        /// <returns>A task representing the asynchronous dispose operation.</returns>
        public async Task DisposeAsync()
        {
            if (this.connection is not null)
            {
                await this.connection.CloseAsync().ConfigureAwait(false);
                await this.connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verifies that a shared controller scale-out admission decision creates a Redis scale-out request.
        /// </summary>
        [Fact]
        public async Task SubmitRunAsync_Should_Create_Redis_ScaleOut_Request_When_Admission_Requests_ScaleOut()
        {
            var controlPlaneIdResolver = new TestControlPlaneIdResolver("cp-test");
            var store = new InMemoryAiSharedRunStore();
            var sharedQueue = new InMemoryAiSharedQueue();
            var scaleOutPublisher = new StoreBackedAiRuntimeScaleOutRequestPublisher(
                this.GetScaleOutStore(),
                controlPlaneIdResolver);

            var controller = new AiSharedRuntimeController(
                new RequestScaleOutAdmissionController(),
                store,
                sharedQueue,
                new ThrowingSharedRunDispatcher(),
                scaleOutPublisher,
                controlPlaneIdResolver,
                Options.Create(new AiSharedRuntimeControllerOptions
                {
                    EnableSubmitRun = true,
                    EnableScaleOutRequest = true,
                    SubmitMode = AiSharedRuntimeSubmitMode.DirectDispatch
                }),
                new NoopAiControlPlaneObserver());

            var result = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = "shared-run-scaleout-1",
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline-test",
                        Input = new Dictionary<string, object?>
                        {
                            ["test"] = true
                        }
                    },
                    TenantId = "tenant-test",
                    PipelineKey = "pipeline-test",
                    CorrelationId = "correlation-test",
                    RequestedBy = "integration-test",
                    Source = "integration-test",
                    Reason = "scale-out integration test",
                    Metadata = new Dictionary<string, string>
                    {
                        ["test"] = "true"
                    }
                });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal("shared-run-scaleout-1", result.SharedRunId);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, result.Run.Status);
            Assert.Equal("cp-test", result.Run.ControlPlaneId);

            var pending = await this.GetScaleOutStore().ListPendingAsync(
                new AiRuntimeScaleOutRequestQuery
                {
                    ControlPlaneId = "cp-test"
                });

            var request = Assert.Single(pending);

            Assert.Equal("scale-out-shared-run-scaleout-1", request.RequestId);
            Assert.Equal("shared-run-scaleout-1", request.SharedRunId);
            Assert.Equal("cp-test", request.ControlPlaneId);
            Assert.Equal("tenant-test", request.TenantId);
            Assert.Equal("pipeline-test", request.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, request.Status);
            Assert.Equal(0, request.VisibleInstanceCount);
            Assert.Equal(0, request.AvailableInstanceCount);
            Assert.Equal(0, request.CurrentInstanceCount);
            Assert.Equal(3, request.MaxInstanceCount);
            Assert.Equal(1, request.RequestedTargetInstanceCount);
            Assert.Equal("correlation-test", request.CorrelationId);
            Assert.Equal("integration-test", request.RequestedBy);
            Assert.Equal("integration-test", request.Source);
            Assert.Equal("No runtime capacity was available for admission.", request.Reason);
            Assert.Equal("cp-test", request.Metadata["controlPlaneId"]);
            Assert.Equal("shared-run-scaleout-1", request.Metadata["sharedRunId"]);
        }

        /// <summary>
        /// Gets the initialized Redis scale-out request store.
        /// </summary>
        /// <returns>The initialized Redis scale-out request store.</returns>
        private RedisAiRuntimeScaleOutRequestStore GetScaleOutStore()
        {
            return this.scaleOutStore
                ?? throw new InvalidOperationException("Redis scale-out request store was not initialized.");
        }

        /// <summary>
        /// Gets the Redis connection string used by integration tests.
        /// </summary>
        /// <returns>The Redis connection string.</returns>
        private static string GetRedisConnectionString()
        {
            return Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING")
                ?? "localhost:6379";
        }

        /// <summary>
        /// Provides a fixed control-plane id resolver for tests.
        /// </summary>
        private sealed class TestControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            /// <summary>
            /// The control-plane identifier returned by the resolver.
            /// </summary>
            private readonly string controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestControlPlaneIdResolver" /> class.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane identifier to return.</param>
            public TestControlPlaneIdResolver(string controlPlaneId)
            {
                this.controlPlaneId = controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string?> ResolveAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<string?>(this.controlPlaneId);
            }
        }

        /// <summary>
        /// Provides an admission controller that always requests scale-out.
        /// </summary>
        private sealed class RequestScaleOutAdmissionController : IAiRunAdmissionController
        {
            /// <inheritdoc />
            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                    AssignedRuntimeInstanceId = null,
                    Reason = "No runtime capacity was available for admission.",
                    VisibleInstanceCount = 0,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 0,
                    MaxInstanceCount = 3,
                    Metadata = request.Metadata
                });
            }
        }

        /// <summary>
        /// Provides a dispatcher that fails the test if dispatch is invoked.
        /// </summary>
        private sealed class ThrowingSharedRunDispatcher : IAiSharedRunDispatcher
        {
            /// <inheritdoc />
            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Dispatcher should not be invoked when admission requests scale-out.");
            }
        }
    }
}