using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides Redis integration tests for <see cref="AiRuntimeScaleOutRequestWatcherHostedService" />.
    /// </summary>
    public sealed class RedisAiRuntimeScaleOutRequestWatcherHostedServiceTests : IAsyncLifetime
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
        /// Redis-backed scale-out request store used by the tests.
        /// </summary>
        private RedisAiRuntimeScaleOutRequestStore? store;

        /// <summary>
        /// Initializes Redis connection and store.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync()
        {
            this.connection = await ConnectionMultiplexer
                .ConnectAsync(GetRedisConnectionString())
                .ConfigureAwait(false);

            this.store = new RedisAiRuntimeScaleOutRequestStore(
                this.connection,
                Options.Create(new RedisAiRuntimeScaleOutRequestStoreOptions
                {
                    KeyPrefix = this.keyPrefix,
                    DefaultTtl = TimeSpan.FromMinutes(5),
                    DeduplicationWindow = TimeSpan.FromSeconds(30),
                    EnableDeduplication = true,
                    MaxListResults = 100,
                    DefaultIndexScanLimit = 1_000
                }),
                new RedisAiRuntimeScaleOutRequestStoreScriptCache(this.connection));
        }

        /// <summary>
        /// Disposes Redis connection resources.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task DisposeAsync()
        {
            if (this.connection is not null)
            {
                await this.connection.CloseAsync().ConfigureAwait(false);
                await this.connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verifies that a Redis pending scale-out request is fulfilled by the watcher.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Fulfill_Redis_Pending_Request_When_Provider_Succeeds()
        {
            await this.GetStore()
                .CreateAsync(CreateRequest("request-1"))
                .ConfigureAwait(false);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    this.GetStore(),
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider(
                            Options.Create(new SimulatedAiRuntimeScaleOutProviderOptions
                            {
                                Succeed = true,
                                RuntimeInstanceIdPrefix = "simulated-runtime"
                            }))),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new TestControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "redis-watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await this.GetStore()
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
            Assert.Equal("redis-watcher-test", loaded.ObservedBy);
            Assert.Equal("redis-watcher-test", loaded.FulfilledBy);
            Assert.False(string.IsNullOrWhiteSpace(loaded.FulfilledRuntimeInstanceId));
            Assert.StartsWith("simulated-runtime-", loaded.FulfilledRuntimeInstanceId, StringComparison.Ordinal);

            var pending =
                await this.GetStore()
                    .ListPendingAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = "cp-test"
                        })
                    .ConfigureAwait(false);

            Assert.Empty(pending);
        }

        /// <summary>
        /// Verifies that a Redis pending scale-out request is rejected when the provider fails.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Reject_Redis_Pending_Request_When_Provider_Fails()
        {
            await this.GetStore()
                .CreateAsync(CreateRequest("request-1"))
                .ConfigureAwait(false);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    this.GetStore(),
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider(
                            Options.Create(new SimulatedAiRuntimeScaleOutProviderOptions
                            {
                                Succeed = false,
                                FailureReason = "redis simulated failure"
                            }))),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new TestControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "redis-watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await this.GetStore()
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, loaded.Status);
            Assert.Equal("redis-watcher-test", loaded.ObservedBy);
            Assert.Equal("redis-watcher-test", loaded.RejectedBy);
            Assert.Equal("redis simulated failure", loaded.RejectionReason);
        }

        /// <summary>
        /// Gets the initialized Redis-backed scale-out request store.
        /// </summary>
        /// <returns>The initialized store.</returns>
        private RedisAiRuntimeScaleOutRequestStore GetStore()
        {
            return this.store
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
        /// Creates a valid scale-out request record for tests.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <returns>The created request record.</returns>
        private static AiRuntimeScaleOutRequestRecord CreateRequest(
            string requestId)
        {
            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = requestId,
                ControlPlaneId = "cp-test",
                SharedRunId = "shared-run-1",
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(
                    contextKey: "unit-test:tenant-test:context",
                    project: "unit-test",
                    userId: "unit-test",
                    tenantId: "tenant-test",
                    tenantGroupId: "tenant-group-test",
                    currentNamespace: "unit-test"),
                TenantId = "tenant-test",
                TenantGroupId = "tenant-group-test",
                PipelineKey = "pipeline-test",
                Status = AiRuntimeScaleOutRequestStatus.Pending,
                Reason = "No runtime capacity was available for admission.",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedTargetInstanceCount = 1,
                ProviderHint = "simulated",
                RequestedBy = "integration-test",
                Source = "integration-test",
                CorrelationId = "correlation-test",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Provides a fixed scale-out provider selector for watcher tests.
        /// </summary>
        private sealed class TestScaleOutProviderSelector : IAiRuntimeScaleOutProviderSelector
        {
            /// <summary>
            /// The provider invoked by the selector.
            /// </summary>
            private readonly IAiRuntimeScaleOutProvider provider;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestScaleOutProviderSelector" /> class.
            /// </summary>
            /// <param name="provider">The provider to invoke.</param>
            public TestScaleOutProviderSelector(
                IAiRuntimeScaleOutProvider provider)
            {
                this.provider =
                    provider
                    ?? throw new ArgumentNullException(nameof(provider));
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                return this.provider
                    .RequestScaleOutAsync(
                        request,
                        cancellationToken);
            }
        }

        /// <summary>
        /// Provides a fixed control-plane id resolver for tests.
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
            public TestControlPlaneIdResolver(
                string? controlPlaneId)
            {
                this.controlPlaneId =
                    controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string?> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    this.controlPlaneId);
            }
        }
    }
}