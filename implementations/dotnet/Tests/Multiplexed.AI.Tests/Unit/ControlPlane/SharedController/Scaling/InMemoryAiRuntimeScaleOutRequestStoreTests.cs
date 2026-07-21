using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="InMemoryAiRuntimeScaleOutRequestStore" />.
    /// </summary>
    public sealed class InMemoryAiRuntimeScaleOutRequestStoreTests
    {
        /// <summary>
        /// Verifies that creating a scale-out request stores it as pending and makes it retrievable.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Create_Pending_Request()
        {
            var store = CreateStore();

            var created = await store.CreateAsync(CreateRequest("request-1"));

            Assert.Equal("request-1", created.RequestId);
            Assert.Equal("cp-test", created.ControlPlaneId);
            Assert.Equal("shared-run-1", created.SharedRunId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, created.Status);
            Assert.NotNull(created.ExpiresAtUtc);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal("request-1", loaded.RequestId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, loaded.Status);
        }

        /// <summary>
        /// Verifies that creating a request without an identifier generates one.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Generate_RequestId_When_Missing()
        {
            var store = CreateStore();

            var created = await store.CreateAsync(CreateRequest(string.Empty));

            Assert.False(string.IsNullOrWhiteSpace(created.RequestId));
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, created.Status);
        }

        /// <summary>
        /// Verifies that creating a request without a control-plane identifier fails.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Throw_When_ControlPlaneId_Is_Missing()
        {
            var store = CreateStore();
            var request = CreateRequest("request-1");
            request.ControlPlaneId = string.Empty;

            await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(request));
        }

        /// <summary>
        /// Verifies that creating a request without a shared run identifier fails.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Throw_When_SharedRunId_Is_Missing()
        {
            var store = CreateStore();
            var request = CreateRequest("request-1");
            request.SharedRunId = string.Empty;

            await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(request));
        }

        /// <summary>
        /// Verifies that duplicate pending requests are deduplicated inside the configured deduplication window.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Deduplicate_Pending_Request()
        {
            var store = CreateStore();

            var first = await store.CreateAsync(CreateRequest("request-1"));
            var second = await store.CreateAsync(CreateRequest("request-2"));

            Assert.Equal(first.RequestId, second.RequestId);

            var pending = await store.ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Single(pending);
        }

        /// <summary>
        /// Verifies that an observed recovery replacement remains single-flight after the normal deduplication window.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Deduplicate_Observed_Recovery_Replacement_Beyond_Normal_Window()
        {
            var store = CreateStore(new AiRuntimeScaleOutRequestStoreOptions
            {
                EnableDeduplication = true,
                DefaultTtl = TimeSpan.FromMinutes(30),
                DeduplicationWindow = TimeSpan.FromMilliseconds(1),
                MaxListResults = 100
            });

            var first = await store.CreateAsync(CreateRecoveryReplacementRequest("request-1"));
            await store.MarkObservedAsync(first.RequestId, "scaler-test");
            await Task.Delay(TimeSpan.FromMilliseconds(20));

            var second = await store.CreateAsync(CreateRecoveryReplacementRequest("request-2"));

            Assert.Equal(first.RequestId, second.RequestId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Observed, second.Status);

            var all = await store.ListAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Single(all);
        }

        /// <summary>
        /// Verifies that a terminal recovery request releases its generation for a later retry.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Allow_New_Recovery_Replacement_After_Terminal_Transition()
        {
            var store = CreateStore();

            var first = await store.CreateAsync(CreateRecoveryReplacementRequest("request-1"));
            await store.MarkObservedAsync(first.RequestId, "scaler-test");
            await store.MarkRejectedAsync(first.RequestId, "scaler-test", "provisioning failed");

            var second = await store.CreateAsync(CreateRecoveryReplacementRequest("request-2"));

            Assert.Equal("request-2", second.RequestId);
            Assert.NotEqual(first.RequestId, second.RequestId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Pending, second.Status);
        }

        /// <summary>
        /// Verifies that different recovery forensics identities are independent generations.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Not_Deduplicate_Different_Recovery_Generation()
        {
            var store = CreateStore();

            var first = await store.CreateAsync(CreateRecoveryReplacementRequest("request-1"));
            await store.MarkObservedAsync(first.RequestId, "scaler-test");

            var nextGeneration = CreateRecoveryReplacementRequest("request-2");
            nextGeneration.Metadata["recovery.forensicsId"] = "forensics-2";

            var second = await store.CreateAsync(nextGeneration);

            Assert.Equal("request-2", second.RequestId);
            Assert.NotEqual(first.RequestId, second.RequestId);
        }

        /// <summary>
        /// Verifies that deduplication can be disabled.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Create_Duplicate_When_Deduplication_Disabled()
        {
            var store = CreateStore(new AiRuntimeScaleOutRequestStoreOptions
            {
                EnableDeduplication = false,
                DefaultTtl = TimeSpan.FromMinutes(30),
                DeduplicationWindow = TimeSpan.FromSeconds(30),
                MaxListResults = 100
            });

            await store.CreateAsync(CreateRequest("request-1"));
            await store.CreateAsync(CreateRequest("request-2"));

            var pending = await store.ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Equal(2, pending.Count);
        }

        /// <summary>
        /// Verifies that pending requests can be listed by control-plane identifier.
        /// </summary>
        [Fact]
        public async Task ListPendingAsync_Should_Filter_By_ControlPlaneId()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));

            var other = CreateRequest("request-2");
            other.ControlPlaneId = "cp-other";
            await store.CreateAsync(other);

            var pending = await store.ListPendingAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Single(pending);
            Assert.Equal("request-1", pending.First().RequestId);
        }

        /// <summary>
        /// Verifies that requests can be listed by tenant and pipeline key.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Filter_By_Tenant_And_Pipeline()
        {
            var store = CreateStore(new AiRuntimeScaleOutRequestStoreOptions
            {
                EnableDeduplication = false,
                DefaultTtl = TimeSpan.FromMinutes(30),
                DeduplicationWindow = TimeSpan.FromSeconds(30),
                MaxListResults = 100
            });

            await store.CreateAsync(CreateRequest("request-1"));

            var other = CreateRequest("request-2");
            other.TenantId = "tenant-other";
            other.PipelineKey = "pipeline-other";
            await store.CreateAsync(other);

            var results = await store.ListAsync(new AiRuntimeScaleOutRequestQuery
            {
                TenantId = "tenant-test",
                PipelineKey = "pipeline-test"
            });

            Assert.Single(results);
            Assert.Equal("request-1", results.First().RequestId);
        }

        /// <summary>
        /// Verifies that a pending request can be marked as observed.
        /// </summary>
        [Fact]
        public async Task MarkObservedAsync_Should_Transition_Pending_To_Observed()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));

            var changed = await store.MarkObservedAsync("request-1", "scaler-test");

            Assert.True(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Observed, loaded.Status);
            Assert.Equal("scaler-test", loaded.ObservedBy);
            Assert.NotNull(loaded.ObservedAtUtc);
        }

        /// <summary>
        /// Verifies that an observed request can be marked as fulfilled.
        /// </summary>
        [Fact]
        public async Task MarkFulfilledAsync_Should_Transition_Observed_To_Fulfilled()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));
            await store.MarkObservedAsync("request-1", "scaler-test");

            var changed = await store.MarkFulfilledAsync("request-1", "scaler-test", "runtime-1");

            Assert.True(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
            Assert.Equal("scaler-test", loaded.FulfilledBy);
            Assert.Equal("runtime-1", loaded.FulfilledRuntimeInstanceId);
            Assert.NotNull(loaded.FulfilledAtUtc);
        }

        /// <summary>
        /// Verifies that a pending request can be marked as rejected.
        /// </summary>
        [Fact]
        public async Task MarkRejectedAsync_Should_Transition_Pending_To_Rejected()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));

            var changed = await store.MarkRejectedAsync("request-1", "scaler-test", "max capacity reached");

            Assert.True(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, loaded.Status);
            Assert.Equal("scaler-test", loaded.RejectedBy);
            Assert.Equal("max capacity reached", loaded.RejectionReason);
            Assert.NotNull(loaded.RejectedAtUtc);
        }

        /// <summary>
        /// Verifies that a pending request can be marked as expired.
        /// </summary>
        [Fact]
        public async Task MarkExpiredAsync_Should_Transition_Pending_To_Expired()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));

            var changed = await store.MarkExpiredAsync("request-1");

            Assert.True(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Expired, loaded.Status);
            Assert.NotNull(loaded.ExpiredAtUtc);
        }

        /// <summary>
        /// Verifies that a pending request can be marked as cancelled.
        /// </summary>
        [Fact]
        public async Task MarkCancelledAsync_Should_Transition_Pending_To_Cancelled()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));

            var changed = await store.MarkCancelledAsync("request-1", "controller-test");

            Assert.True(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Cancelled, loaded.Status);
            Assert.NotNull(loaded.CancelledAtUtc);
            Assert.Equal("controller-test", loaded.Metadata["cancelledBy"]);
        }

        /// <summary>
        /// Verifies that terminal requests cannot transition to another status.
        /// </summary>
        [Fact]
        public async Task MarkObservedAsync_Should_Return_False_For_Terminal_Request()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));
            await store.MarkFulfilledAsync("request-1", "scaler-test", "runtime-1");

            var changed = await store.MarkObservedAsync("request-1", "scaler-test");

            Assert.False(changed);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
        }

        /// <summary>
        /// Verifies that expired requests are not returned by default list queries.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Exclude_Expired_By_Default()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));
            await store.MarkExpiredAsync("request-1");

            var results = await store.ListAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test"
            });

            Assert.Empty(results);
        }

        /// <summary>
        /// Verifies that expired requests can be included explicitly.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Include_Expired_When_Requested()
        {
            var store = CreateStore();

            await store.CreateAsync(CreateRequest("request-1"));
            await store.MarkExpiredAsync("request-1");

            var results = await store.ListAsync(new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = "cp-test",
                IncludeExpired = true
            });

            Assert.Single(results);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Expired, results.First().Status);
        }

        /// <summary>
        /// Verifies that automatic expiration marks pending requests as expired.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Automatically_Expire_Request()
        {
            var store = CreateStore();

            var request = CreateRequest("request-1");
            request.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(-1);

            await store.CreateAsync(request);

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Expired, loaded.Status);
            Assert.NotNull(loaded.ExpiredAtUtc);
        }

        /// <summary>
        /// Verifies that stored records are protected from external mutation.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Defensively_Copy_Request()
        {
            var store = CreateStore();
            var request = CreateRequest("request-1");

            var created = await store.CreateAsync(request);

            created.Metadata["mutated"] = "true";
            request.Metadata["external"] = "true";

            var loaded = await store.GetAsync("request-1");

            Assert.NotNull(loaded);
            Assert.False(loaded.Metadata.ContainsKey("mutated"));
            Assert.False(loaded.Metadata.ContainsKey("external"));
        }

        /// <summary>
        /// Verifies that tenant-aware runtime scale-out settings are preserved by the defensive store copy.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Preserve_Tenant_Aware_Runtime_Settings()
        {
            var store =
                CreateStore();

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

            await store
                .CreateAsync(request)
                .ConfigureAwait(false);

            var loaded =
                await store
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
        /// Creates an in-memory scale-out request store using default options.
        /// </summary>
        /// <returns>The created store.</returns>
        private static InMemoryAiRuntimeScaleOutRequestStore CreateStore()
        {
            return CreateStore(new AiRuntimeScaleOutRequestStoreOptions
            {
                EnableDeduplication = true,
                DefaultTtl = TimeSpan.FromMinutes(30),
                DeduplicationWindow = TimeSpan.FromSeconds(30),
                MaxListResults = 100
            });
        }

        /// <summary>
        /// Creates an in-memory scale-out request store using supplied options.
        /// </summary>
        /// <param name="options">The scale-out request store options.</param>
        /// <returns>The created store.</returns>
        private static InMemoryAiRuntimeScaleOutRequestStore CreateStore(
            AiRuntimeScaleOutRequestStoreOptions options)
        {
            return new InMemoryAiRuntimeScaleOutRequestStore(
                Options.Create(options));
        }

        /// <summary>
        /// Creates a recovery replacement request with an exact recovery-generation identity.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <returns>The created recovery replacement request.</returns>
        private static AiRuntimeScaleOutRequestRecord CreateRecoveryReplacementRequest(string requestId)
        {
            var request = CreateRequest(requestId);

            request.Metadata["scaleout.intent"] = "shared-queue-redispatch-replacement";
            request.Metadata["scaleout.replacementForRuntimeInstanceId"] = "runtime-failed-1";
            request.Metadata["scaleout.excludedRuntimeInstanceId"] = "runtime-failed-1";
            request.Metadata["scaleout.dedup.scope"] = "recovery-replacement";
            request.Metadata["recovery.replacement"] = "true";
            request.Metadata["recovery.failedRuntimeInstanceId"] = "runtime-failed-1";
            request.Metadata["recovery.forensicsId"] = "forensics-1";

            return request;
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
                ProviderHint = "test",
                RequestedBy = "unit-test",
                Source = "unit-test",
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