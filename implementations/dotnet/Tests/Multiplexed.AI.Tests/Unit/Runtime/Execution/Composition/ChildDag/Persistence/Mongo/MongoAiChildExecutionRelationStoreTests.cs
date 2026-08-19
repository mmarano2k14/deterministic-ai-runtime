using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Persistence.Mongo
{
    /// <summary>
    /// Validates MongoDB authority, uniqueness, and compare-and-swap semantics for child execution relations.
    /// </summary>
    public sealed class MongoAiChildExecutionRelationStoreTests : IAsyncLifetime
    {
        private readonly string connectionString;
        private readonly string databaseName;
        private readonly MongoClient client;
        private readonly IMongoDatabase database;
        private readonly string collectionName;
        private readonly MongoAiChildExecutionRelationStore store;

        public MongoAiChildExecutionRelationStoreTests()
        {
            this.connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";
            this.databaseName = $"multiplexed_child_relations_{Guid.NewGuid():N}";
            this.collectionName = "relations";
            this.client = new MongoClient(this.connectionString);
            this.database = this.client.GetDatabase(this.databaseName);
            this.store = new MongoAiChildExecutionRelationStore(
                this.database,
                Options.Create(
                    new AiChildExecutionRelationMongoOptions
                    {
                        CollectionName = this.collectionName,
                        EnsureIndexes = true
                    }));
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await this.client.DropDatabaseAsync(this.databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task GetOrCreateAsync_Should_Converge_Concurrent_Writers_On_One_Typed_Relation()
        {
            var candidates = Enumerable.Range(0, 8)
                .Select(_ => CreateRelation())
                .ToArray();

            var results = await Task.WhenAll(
                candidates.Select(candidate => this.store.GetOrCreateAsync(candidate)));

            Assert.All(results, result => Assert.Equal(candidates[0].ChildInvocationKey, result.ChildInvocationKey));

            var rawCollection = this.database.GetCollection<BsonDocument>(this.collectionName);
            Assert.Equal(1L, await rawCollection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
        }

        [Fact]
        public async Task GetOrCreateAsync_Should_Reject_Conflicting_Immutable_Creation_Data()
        {
            var first = CreateRelation();
            await this.store.GetOrCreateAsync(first);

            var conflicting = CreateRelation(
                frozenInput: AiStoredPayload.Inline(
                    "{\"request\":\"different\"}",
                    contentType: "application/json",
                    contentHash: "6eff8f4c2fba712ff12d25e6ec2421b4f37feebea29e7d83f6ff4d2b8810027c"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.store.GetOrCreateAsync(conflicting));
        }

        [Fact]
        public async Task TryReplaceAsync_Should_Allow_Only_One_Status_Cas_Winner()
        {
            var initial = CreateRelation();
            await this.store.GetOrCreateAsync(initial);

            var approved = CreateRelation(
                status: AiChildExecutionRelationStatus.DelegationApproved);

            var attempts = await Task.WhenAll(
                this.store.TryReplaceAsync(
                    approved,
                    AiChildExecutionRelationStatus.DelegationPolicyPending),
                this.store.TryReplaceAsync(
                    approved,
                    AiChildExecutionRelationStatus.DelegationPolicyPending));

            Assert.Equal(1, attempts.Count(result => result));
            Assert.Equal(1, attempts.Count(result => !result));

            var persisted = await this.store.GetAsync(approved.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationApproved, persisted!.Status);
        }

        [Fact]
        public async Task TryReplaceContinuationAsync_Should_Allow_One_Pending_To_Scheduled_Winner_And_Exclude_Resumed()
        {
            var completed = await CreatePersistedCompletedRelationAsync();
            var scheduledAtUtc = DateTimeOffset.Parse("2026-08-15T00:05:00Z");
            var firstScheduled = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Scheduled,
                parentContinuationScheduledAtUtc: scheduledAtUtc);
            var secondScheduled = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Scheduled,
                parentContinuationScheduledAtUtc: scheduledAtUtc);

            var attempts = await Task.WhenAll(
                this.store.TryReplaceContinuationAsync(
                    firstScheduled,
                    AiChildContinuationStatus.Pending),
                this.store.TryReplaceContinuationAsync(
                    secondScheduled,
                    AiChildContinuationStatus.Pending));

            Assert.Equal(1, attempts.Count(result => result));
            Assert.Equal(1, attempts.Count(result => !result));

            var scheduledCandidates = await this.store.ListContinuationCandidatesAsync(10);
            var scheduled = Assert.Single(scheduledCandidates);
            Assert.Equal(AiChildContinuationStatus.Scheduled, scheduled.ContinuationStatus);
            Assert.Equal(completed.ChildExecutionId, scheduled.ChildExecutionId);

            var resumed = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Resumed,
                parentContinuationScheduledAtUtc: scheduledAtUtc,
                parentResumedAtUtc: DateTimeOffset.Parse("2026-08-15T00:06:00Z"));

            Assert.True(await this.store.TryReplaceContinuationAsync(
                resumed,
                AiChildContinuationStatus.Scheduled));

            Assert.Empty(await this.store.ListContinuationCandidatesAsync(10));
        }

        [Fact]
        public async Task TryCommitNextInvocationGenerationAsync_Should_Allow_Only_One_Durable_Retry_Decision_Winner()
        {
            var retryable = await CreatePersistedRetryableFailedRelationAsync();
            var first = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Resumed,
                childFailureReason: "child execution failed",
                nextInvocationGeneration: 1,
                nextInvocationGenerationDecidedAtUtc: DateTimeOffset.Parse("2026-08-15T00:07:00Z"),
                nextInvocationGenerationDecisionReason: "explicit retry");
            var second = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Resumed,
                childFailureReason: "child execution failed",
                nextInvocationGeneration: 1,
                nextInvocationGenerationDecidedAtUtc: DateTimeOffset.Parse("2026-08-15T00:07:00Z"),
                nextInvocationGenerationDecisionReason: "explicit retry");

            var attempts = await Task.WhenAll(
                this.store.TryCommitNextInvocationGenerationAsync(first),
                this.store.TryCommitNextInvocationGenerationAsync(second));

            Assert.Equal(1, attempts.Count(result => result));
            Assert.Equal(1, attempts.Count(result => !result));

            var persisted = await this.store.GetAsync(retryable.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(1, persisted!.NextInvocationGeneration);
            Assert.Equal("explicit retry", persisted.NextInvocationGenerationDecisionReason);
        }

        [Fact]
        public async Task Generation_Coordinator_Should_Recreate_Next_Relation_After_Durable_Decision_Crash_Window()
        {
            var retryable = await CreatePersistedRetryableFailedRelationAsync();
            retryable.NextInvocationGeneration = 1;
            retryable.NextInvocationGenerationDecidedAtUtc = DateTimeOffset.Parse("2026-08-15T00:07:00Z");
            retryable.NextInvocationGenerationDecisionReason = "durable retry decision";
            Assert.True(await this.store.TryCommitNextInvocationGenerationAsync(retryable));

            // Simulates process loss after the generation decision was committed but before generation 1 existed.
            var coordinatorAfterRecovery = new AiChildInvocationGenerationCoordinator(this.store);
            var next = await coordinatorAfterRecovery.PrepareNextGenerationAsync(
                retryable.ToInvocationIdentity(),
                "recovery re-drive");

            Assert.Equal(1, next.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, next.Status);
            Assert.Null(next.ChildExecutionId);
            Assert.Equal(
                next.ChildInvocationKey,
                (await this.store.GetAsync(next.ToInvocationIdentity()))!.ChildInvocationKey);
        }

        [Fact]
        public async Task TryReplaceContinuationAsync_Should_Exclude_Suppressed_Terminal_Parent_From_Reconciliation()
        {
            await CreatePersistedCompletedRelationAsync();
            var suppressed = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Suppressed,
                parentContinuationSuppressedAtUtc: DateTimeOffset.Parse("2026-08-15T00:05:30Z"),
                parentContinuationSuppressionReason: "parent cancelled");

            Assert.True(await this.store.TryReplaceContinuationAsync(
                suppressed,
                AiChildContinuationStatus.Pending));

            var persisted = await this.store.GetAsync(suppressed.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(AiChildContinuationStatus.Suppressed, persisted!.ContinuationStatus);
            Assert.Equal("parent cancelled", persisted.ParentContinuationSuppressionReason);
            Assert.Empty(await this.store.ListContinuationCandidatesAsync(10));
        }

        [Fact]
        public async Task ListContinuationCandidatesAsync_Should_Filter_By_Durable_ControlPlane_Authority()
        {
            var current = await CreatePersistedCompletedRelationAsync(
                controlPlaneId: "control-plane-current",
                parentExecutionId: "parent-current",
                childExecutionId: "child-current");
            await CreatePersistedCompletedRelationAsync(
                controlPlaneId: "control-plane-previous",
                parentExecutionId: "parent-previous",
                childExecutionId: "child-previous");

            var candidates = await this.store.ListContinuationCandidatesAsync(
                10,
                CancellationToken.None,
                "control-plane-current");

            var candidate = Assert.Single(candidates);
            Assert.Equal(current.ChildInvocationKey, candidate.ChildInvocationKey);
            Assert.Equal("control-plane-current", candidate.ControlPlaneId);
        }

        [Fact]
        public async Task GetOrCreateAsync_Should_Create_Typed_Unique_Index_Without_Hash_Uniqueness()
        {
            await this.store.GetOrCreateAsync(CreateRelation());

            var rawCollection = this.database.GetCollection<BsonDocument>(this.collectionName);
            using var cursor = await rawCollection.Indexes.ListAsync();
            var indexes = await cursor.ToListAsync();

            var typedIndex = Assert.Single(
                indexes.Where(index => index["name"].AsString == "ux_child_relation_typed_invocation"));
            Assert.True(typedIndex["unique"].AsBoolean);

            var keyIndex = Assert.Single(
                indexes.Where(index => index["name"].AsString == "ix_child_relation_invocation_key"));
            Assert.False(keyIndex.TryGetValue("unique", out var unique) && unique.AsBoolean);
        }

        private async Task<AiChildExecutionRelation> CreatePersistedCompletedRelationAsync(
            string controlPlaneId = "control-plane-relation-store-tests",
            string parentExecutionId = "parent-execution-1",
            string childExecutionId = "child-execution-1")
        {
            var initial = CreateRelation(
                controlPlaneId: controlPlaneId,
                parentExecutionId: parentExecutionId,
                childExecutionId: childExecutionId);
            await this.store.GetOrCreateAsync(initial);

            var approved = CreateRelation(
                status: AiChildExecutionRelationStatus.DelegationApproved,
                controlPlaneId: controlPlaneId,
                parentExecutionId: parentExecutionId,
                childExecutionId: childExecutionId);
            Assert.True(await this.store.TryReplaceAsync(
                approved,
                AiChildExecutionRelationStatus.DelegationPolicyPending));

            var allocated = CreateRelation(
                status: AiChildExecutionRelationStatus.ChildAllocated,
                controlPlaneId: controlPlaneId,
                parentExecutionId: parentExecutionId,
                childExecutionId: childExecutionId);
            Assert.True(await this.store.TryReplaceAsync(
                allocated,
                AiChildExecutionRelationStatus.DelegationApproved));

            var completed = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Pending,
                controlPlaneId: controlPlaneId,
                parentExecutionId: parentExecutionId,
                childExecutionId: childExecutionId);
            Assert.True(await this.store.TryReplaceAsync(
                completed,
                AiChildExecutionRelationStatus.ChildAllocated));

            return completed;
        }

        private async Task<AiChildExecutionRelation> CreatePersistedRetryableFailedRelationAsync()
        {
            var initial = CreateRelation();
            await this.store.GetOrCreateAsync(initial);

            var approved = CreateRelation(status: AiChildExecutionRelationStatus.DelegationApproved);
            Assert.True(await this.store.TryReplaceAsync(
                approved,
                AiChildExecutionRelationStatus.DelegationPolicyPending));

            var allocated = CreateRelation(status: AiChildExecutionRelationStatus.ChildAllocated);
            Assert.True(await this.store.TryReplaceAsync(
                allocated,
                AiChildExecutionRelationStatus.DelegationApproved));

            var completed = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Pending,
                childFailureReason: "child execution failed");
            Assert.True(await this.store.TryReplaceAsync(
                completed,
                AiChildExecutionRelationStatus.ChildAllocated));

            var scheduled = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Scheduled,
                childFailureReason: "child execution failed");
            Assert.True(await this.store.TryReplaceContinuationAsync(
                scheduled,
                AiChildContinuationStatus.Pending));

            var resumed = CreateRelation(
                status: AiChildExecutionRelationStatus.Completed,
                continuationStatus: AiChildContinuationStatus.Resumed,
                childFailureReason: "child execution failed");
            Assert.True(await this.store.TryReplaceContinuationAsync(
                resumed,
                AiChildContinuationStatus.Scheduled));

            return resumed;
        }

        private static AiChildExecutionRelation CreateRelation(
            AiStoredPayload? frozenInput = null,
            AiChildExecutionRelationStatus status = AiChildExecutionRelationStatus.DelegationPolicyPending,
            AiChildContinuationStatus continuationStatus = AiChildContinuationStatus.Pending,
            DateTimeOffset? parentContinuationScheduledAtUtc = null,
            DateTimeOffset? parentResumedAtUtc = null,
            string? childFailureReason = null,
            int? nextInvocationGeneration = null,
            DateTimeOffset? nextInvocationGenerationDecidedAtUtc = null,
            string? nextInvocationGenerationDecisionReason = null,
            DateTimeOffset? parentContinuationSuppressedAtUtc = null,
            string? parentContinuationSuppressionReason = null,
            string controlPlaneId = "control-plane-relation-store-tests",
            string parentExecutionId = "parent-execution-1",
            string childExecutionId = "child-execution-1")
        {
            var identity = new Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity.AiChildInvocationIdentity
            {
                TenantId = "tenant-1",
                ParentExecutionId = parentExecutionId,
                ParentCallSiteId = "research-call-site",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|fundamental-research",
                InvocationGeneration = 0
            };

            return new AiChildExecutionRelation
            {
                TenantId = identity.TenantId,
                ControlPlaneId = controlPlaneId,
                ParentExecutionId = identity.ParentExecutionId,
                ParentCallSiteId = identity.ParentCallSiteId,
                ChildDagId = identity.ChildDagId,
                ChildDagDefinitionVersion = identity.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = AiStoredPayload.Inline(
                    "{\"Name\":\"child-analysis\",\"Version\":\"v1\"}",
                    contentType: "application/json",
                    contentHash: "27ec1cbd981590d12a75366c40f8d55c5fcaf23976af2532dfa1f9fc0e345976"),
                CanonicalLogicalInvocationKey = identity.CanonicalLogicalInvocationKey,
                ChildInvocationKey = AiChildInvocationKeyFactory.Create(identity),
                InvocationGeneration = identity.InvocationGeneration,
                NextInvocationGeneration = nextInvocationGeneration,
                NextInvocationGenerationDecidedAtUtc = nextInvocationGenerationDecidedAtUtc,
                NextInvocationGenerationDecisionReason = nextInvocationGenerationDecisionReason,
                FrozenInvocationInput = frozenInput ?? AiStoredPayload.Inline(
                    "{\"request\":\"analyze\"}",
                    contentType: "application/json",
                    contentHash: "cfc29c357a196c272725084630dcad95c62b9d4987f814b5eb7be3f7f0821023"),
                DelegationPolicyBindingSnapshot = AiStoredPayload.Inline(
                    "{\"Policies\":[]}",
                    contentType: "application/json",
                    contentHash: "cd71903c1d684844145b54533d5934759b0a11e821a01b89c62fe9516e94366f"),
                DelegationPolicyDecisionSnapshot = status == AiChildExecutionRelationStatus.DelegationPolicyPending
                    ? null
                    : AiStoredPayload.Inline(
                        "{\"Approved\":true,\"Reason\":\"approved\",\"Results\":[]}",
                        contentType: "application/json",
                        contentHash: "1ffc76af93422de9f8e0985c77ff7793d726f49fd9d7e7b46794c58e4a6a280a"),
                Status = status,
                ChildExecutionId = status is AiChildExecutionRelationStatus.ChildAllocated or
                    AiChildExecutionRelationStatus.Waiting or
                    AiChildExecutionRelationStatus.Completed
                        ? childExecutionId
                        : null,
                ChildAllocatedAtUtc = status is AiChildExecutionRelationStatus.ChildAllocated or
                    AiChildExecutionRelationStatus.Waiting or
                    AiChildExecutionRelationStatus.Completed
                        ? DateTimeOffset.Parse("2026-08-14T00:02:00Z")
                        : null,
                WaitingAtUtc = status == AiChildExecutionRelationStatus.Waiting
                    ? DateTimeOffset.Parse("2026-08-14T00:03:00Z")
                    : null,
                ChildResult = status == AiChildExecutionRelationStatus.Completed
                    ? AiStoredPayload.Inline(
                        "{}",
                        contentType: "application/json",
                        contentHash: "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")
                    : null,
                ChildFailureReason = status == AiChildExecutionRelationStatus.Completed
                    ? childFailureReason
                    : null,
                CompletedAtUtc = status == AiChildExecutionRelationStatus.Completed
                    ? DateTimeOffset.Parse("2026-08-14T00:04:00Z")
                    : null,
                ContinuationStatus = status == AiChildExecutionRelationStatus.Completed
                    ? continuationStatus
                    : AiChildContinuationStatus.None,
                ParentContinuationScheduledAtUtc = status == AiChildExecutionRelationStatus.Completed &&
                    (continuationStatus is AiChildContinuationStatus.Scheduled or AiChildContinuationStatus.Resumed)
                        ? parentContinuationScheduledAtUtc ?? DateTimeOffset.Parse("2026-08-14T00:05:00Z")
                        : null,
                ParentContinuationScheduledStepVersion = status == AiChildExecutionRelationStatus.Completed &&
                    (continuationStatus is AiChildContinuationStatus.Scheduled or AiChildContinuationStatus.Resumed)
                        ? 10
                        : null,
                ParentResumedAtUtc = status == AiChildExecutionRelationStatus.Completed &&
                    continuationStatus == AiChildContinuationStatus.Resumed
                        ? parentResumedAtUtc ?? DateTimeOffset.Parse("2026-08-14T00:06:00Z")
                        : null,
                ParentContinuationSuppressedAtUtc = status == AiChildExecutionRelationStatus.Completed &&
                    continuationStatus == AiChildContinuationStatus.Suppressed
                        ? parentContinuationSuppressedAtUtc ?? DateTimeOffset.Parse("2026-08-14T00:06:00Z")
                        : null,
                ParentContinuationSuppressionReason = status == AiChildExecutionRelationStatus.Completed &&
                    continuationStatus == AiChildContinuationStatus.Suppressed
                        ? parentContinuationSuppressionReason ?? "parent terminal"
                        : null,
                DelegationEvaluatedAtUtc = status == AiChildExecutionRelationStatus.DelegationPolicyPending
                    ? null
                    : DateTimeOffset.Parse("2026-08-14T00:01:00Z"),
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-14T00:00:00Z")
            };
        }
    }
}
