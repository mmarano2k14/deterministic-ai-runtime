using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides integration tests for <see cref="RedisAiRuntimeScaleOutRequestStore" />.
    /// </summary>
    public sealed class RedisAiRuntimeScaleOutRequestStoreTests : IAsyncLifetime
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
        /// Store under test.
        /// </summary>
        private RedisAiRuntimeScaleOutRequestStore? store;

        /// <summary>
        /// Initializes Redis connection and store.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync()
        {
            this.connection = await ConnectionMultiplexer.ConnectAsync(GetRedisConnectionString()).ConfigureAwait(false);

            this.store = new RedisAiRuntimeScaleOutRequestStore(
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
        /// Disposes Redis connection.
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
        /// Verifies that creating a scale-out request stores it and makes it retrievable by id.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Create_And_Get_Request()
        {
            var created = await this.GetStore().CreateAsync(CreateRequest("request-1"));

            Assert.Equal("request-1", created.RequestId);
            Assert.Equal("cp-test", created.ControlPlaneId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, created.Status);

            var loaded = await this.GetStore().GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal("request-1", loaded.RequestId);
            Assert.Equal("cp-test", loaded.ControlPlaneId);
            Assert.Equal("shared-run-1", loaded.SharedRunId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, loaded.Status);
            Assert.Equal("true", loaded.Metadata["test"]);
        }

        /// <summary>
        /// Verifies that pending requests can be listed by control-plane id.
        /// </summary>
        [Fact]
        public async Task ListPendingAsync_Should_Return_Pending_Requests()
        {
            await this.GetStore().CreateAsync(CreateRequest("request-1"));

            var request2 = CreateRequest("request-2");
            request2.SharedRunId = "shared-run-2";
            request2.PipelineKey = "pipeline-other";

            await this.GetStore().CreateAsync(request2);

            var pending = await this.GetStore().ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, request => request.RequestId == "request-1");
            Assert.Contains(pending, request => request.RequestId == "request-2");
        }

        /// <summary>
        /// Verifies that equivalent pending requests are deduplicated.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Deduplicate_Equivalent_Pending_Request()
        {
            var first = await this.GetStore().CreateAsync(CreateRequest("request-1"));
            var second = await this.GetStore().CreateAsync(CreateRequest("request-2"));

            Assert.Equal(first.RequestId, second.RequestId);

            var pending = await this.GetStore().ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Single(pending);
        }

        /// <summary>
        /// Verifies that a pending request can transition to observed and then fulfilled.
        /// </summary>
        [Fact]
        public async Task MarkFulfilledAsync_Should_Transition_Observed_Request_To_Fulfilled()
        {
            await this.GetStore().CreateAsync(CreateRequest("request-1"));

            var observed = await this.GetStore().MarkObservedAsync("request-1", "scaler-test");
            var fulfilled = await this.GetStore().MarkFulfilledAsync("request-1", "scaler-test", "runtime-1");

            Assert.True(observed);
            Assert.True(fulfilled);

            var loaded = await this.GetStore().GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
            Assert.Equal("scaler-test", loaded.ObservedBy);
            Assert.Equal("scaler-test", loaded.FulfilledBy);
            Assert.Equal("runtime-1", loaded.FulfilledRuntimeInstanceId);

            var pending = await this.GetStore().ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Empty(pending);
        }

        /// <summary>
        /// Verifies that terminal requests cannot transition again.
        /// </summary>
        [Fact]
        public async Task MarkObservedAsync_Should_Return_False_For_Terminal_Request()
        {
            await this.GetStore().CreateAsync(CreateRequest("request-1"));
            await this.GetStore().MarkRejectedAsync("request-1", "scaler-test", "max capacity reached");

            var changed = await this.GetStore().MarkObservedAsync("request-1", "scaler-test");

            Assert.False(changed);

            var loaded = await this.GetStore().GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, loaded.Status);
            Assert.Equal("max capacity reached", loaded.RejectionReason);
        }

        /// <summary>
        /// Verifies that tenant-aware runtime scale-out settings are preserved by Redis persistence.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Preserve_Tenant_Aware_Runtime_Settings()
        {
            var request =
                CreateRequest("request-tenant-settings");

            request.TenantGroupId = "tenant-group-a";
            request.IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated;
            request.PreferDedicatedCapacity = true;
            request.AllowSharedFallback = false;
            request.MaxRuntimeInstances = 5;
            request.RuntimeInstanceIdPrefix = "tenant-a-http";
            request.WorkerCountPerInstance = 7;
            request.MaxConcurrentRunsPerInstance = 3;
            request.LocalQueueCapacity = 42;

            await this.GetStore()
                .CreateAsync(request)
                .ConfigureAwait(false);

            var loaded =
                await this.GetStore()
                    .GetAsync("request-tenant-settings")
                    .ConfigureAwait(false);

            Assert.NotNull(
                loaded);

            Assert.Equal(
                "tenant-group-a",
                loaded!.TenantGroupId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                loaded.IsolationMode);

            Assert.True(
                loaded.PreferDedicatedCapacity);

            Assert.False(
                loaded.AllowSharedFallback);

            Assert.Equal(
                5,
                loaded.MaxRuntimeInstances);

            Assert.Equal(
                "tenant-a-http",
                loaded.RuntimeInstanceIdPrefix);

            Assert.Equal(
                7,
                loaded.WorkerCountPerInstance);

            Assert.Equal(
                3,
                loaded.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                42,
                loaded.LocalQueueCapacity);
        }

        /// <summary>
        /// Gets the initialized store.
        /// </summary>
        /// <returns>The initialized store.</returns>
        private RedisAiRuntimeScaleOutRequestStore GetStore()
        {
            return this.store ?? throw new InvalidOperationException("Redis store was not initialized.");
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
        private static AiRuntimeScaleOutRequestRecord CreateRequest(string requestId)
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
                ProviderHint = "redis-test",
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
    }
}