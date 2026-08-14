using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
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

        private static AiChildExecutionRelation CreateRelation(
            AiStoredPayload? frozenInput = null,
            AiChildExecutionRelationStatus status = AiChildExecutionRelationStatus.DelegationPolicyPending)
        {
            var identity = new Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity.AiChildInvocationIdentity
            {
                TenantId = "tenant-1",
                ParentExecutionId = "parent-execution-1",
                ParentCallSiteId = "research-call-site",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|fundamental-research",
                InvocationGeneration = 0
            };

            return new AiChildExecutionRelation
            {
                TenantId = identity.TenantId,
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
                DelegationEvaluatedAtUtc = status == AiChildExecutionRelationStatus.DelegationPolicyPending
                    ? null
                    : DateTimeOffset.Parse("2026-08-14T00:01:00Z"),
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-14T00:00:00Z")
            };
        }
    }
}
