using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Payloads.Mongo;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads.Mongo.Stores;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Snapshots
{
    /// <summary>
    /// Validates that external child DAG snapshots survive service recreation and remain idempotently addressable.
    /// </summary>
    public sealed class MongoAiChildDagSnapshotServiceTests : IAsyncLifetime
    {
        private readonly string connectionString;
        private readonly string databaseName;
        private readonly MongoClient client;
        private readonly IOptions<AiPayloadStoreOptions> options;

        public MongoAiChildDagSnapshotServiceTests()
        {
            this.connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";
            this.databaseName = $"multiplexed_child_snapshot_{Guid.NewGuid():N}";
            this.client = new MongoClient(this.connectionString);
            this.options = Options.Create(
                new AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "mongo",
                    RequireReplaySafePayloads = true,
                    MaxInlineSizeBytes = 1,
                    Mongo = new MongoAiPayloadStoreOptions
                    {
                        Enabled = true,
                        ConnectionString = this.connectionString,
                        DatabaseName = this.databaseName,
                        CollectionName = "payloads"
                    }
                });
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await this.client.DropDatabaseAsync(this.databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task External_Snapshot_Should_Remain_Recoverable_Before_Relation_Creation()
        {
            var firstStore = new MongoAiPayloadStore(this.options);
            var firstService = new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(firstStore),
                this.options);

            var snapshot = await firstService.FreezeInvocationInputAsync(
                new { PortfolioId = "portfolio-42", Ticker = "MSFT" },
                "parent-1");

            Assert.False(snapshot.IsInline);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.ArtifactId));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.ContentHash));

            var secondStore = new MongoAiPayloadStore(this.options);
            var secondService = new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(secondStore),
                this.options);

            var recovered = await secondService.LoadAndVerifyAsync(snapshot);
            var repeated = await secondService.FreezeInvocationInputAsync(
                new { PortfolioId = "portfolio-42", Ticker = "MSFT" },
                "parent-1");

            Assert.False(string.IsNullOrWhiteSpace(recovered));
            Assert.Equal(snapshot.ArtifactId, repeated.ArtifactId);
            Assert.Equal(snapshot.ContentHash, repeated.ContentHash);
        }

        [Fact]
        public async Task Invocation_Preparation_Should_Remain_Addressable_By_Typed_Identity_Before_Relation_Creation()
        {
            var firstStore = new MongoAiPayloadStore(this.options);
            var firstService = new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(firstStore),
                this.options);
            var identity = new AiChildInvocationIdentity
            {
                TenantId = "tenant-1",
                ParentExecutionId = "parent-1",
                ParentCallSiteId = "research-child",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                InvocationGeneration = 0
            };
            var frozenDefinition = await firstService.FreezeDefinitionAsync(
                new AiPipelineDefinition
                {
                    Name = identity.ChildDagId,
                    Version = identity.ChildDagDefinitionVersion,
                    ExecutionMode = AiExecutionMode.Dag,
                    Steps =
                    [
                        new AiPipelineStepDefinition
                        {
                            Name = "analyze",
                            StepKey = "analysis",
                            Order = 0
                        }
                    ]
                },
                identity.ParentExecutionId);
            var frozenInput = await firstService.FreezeInvocationInputAsync(
                new { PortfolioId = "portfolio-42", Ticker = "MSFT" },
                identity.ParentExecutionId);
            var frozenPolicy = await firstService.FreezeDelegationPolicyBindingAsync(
                new AiChildDelegationPolicyDefinition(),
                identity.ParentExecutionId);
            var persisted = await firstService.FreezeInvocationPreparationAsync(
                identity,
                frozenDefinition,
                frozenInput,
                new ExecutionContextSnapshot
                {
                    ContextKey = "parent-context",
                    TenantGroupId = "group-1",
                    Project = "tests",
                    UserId = "user-1",
                    TenantId = identity.TenantId,
                    CurrentNamespace = "default",
                    Namespaces = [],
                    TtlSeconds = 300
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["proof"] = "pre-relation"
                },
                frozenPolicy);

            var secondStore = new MongoAiPayloadStore(this.options);
            var secondService = new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(secondStore),
                this.options);
            var recovered = await secondService.TryLoadInvocationPreparationAsync(identity);

            Assert.NotNull(recovered);
            Assert.Equal(persisted.ChildInvocationKey, recovered!.ChildInvocationKey);
            Assert.Equal(persisted.FrozenChildDagDefinition.ContentHash, recovered.FrozenChildDagDefinition.ContentHash);
            Assert.Equal(persisted.FrozenInvocationInput.ContentHash, recovered.FrozenInvocationInput.ContentHash);
            Assert.Equal(persisted.DelegationPolicyBindingSnapshot.ContentHash, recovered.DelegationPolicyBindingSnapshot.ContentHash);
            Assert.Equal("pre-relation", recovered.DelegatedMetadata["proof"]);

            var conflictingInput = await secondService.FreezeInvocationInputAsync(
                new { PortfolioId = "portfolio-42", Ticker = "AAPL" },
                identity.ParentExecutionId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => secondService.FreezeInvocationPreparationAsync(
                    identity,
                    recovered.FrozenChildDagDefinition,
                    conflictingInput,
                    recovered.DelegatedExecutionContextSnapshot,
                    recovered.DelegatedMetadata,
                    recovered.DelegationPolicyBindingSnapshot));
        }

        private sealed class FixedPayloadStoreResolver : IAiPayloadStoreResolver
        {
            private readonly IAiPayloadStore store;

            public FixedPayloadStoreResolver(IAiPayloadStore store)
            {
                this.store = store;
            }

            public IAiPayloadStore Resolve() => this.store;
        }
    }
}
