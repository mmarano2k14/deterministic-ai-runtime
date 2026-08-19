using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Allocation
{
    /// <summary>
    /// Validates durable child execution identifier allocation and retry convergence against MongoDB.
    /// </summary>
    public sealed class MongoAiChildExecutionAllocatorTests : IAsyncLifetime
    {
        private readonly string databaseName;
        private readonly MongoClient client;
        private readonly MongoAiChildExecutionRelationStore relationStore;
        private readonly AiChildDagSnapshotService snapshotService;
        private readonly AiChildExecutionAllocator allocator;

        public MongoAiChildExecutionAllocatorTests()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";

            this.databaseName = $"multiplexed_child_allocation_{Guid.NewGuid():N}";
            this.client = new MongoClient(connectionString);
            var database = this.client.GetDatabase(this.databaseName);
            this.relationStore = new MongoAiChildExecutionRelationStore(
                database,
                Options.Create(
                    new AiChildExecutionRelationMongoOptions
                    {
                        CollectionName = "relations",
                        EnsureIndexes = true
                    }));
            this.snapshotService = CreateSnapshotService();
            this.allocator = new AiChildExecutionAllocator(
                this.relationStore,
                this.snapshotService);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await this.client.DropDatabaseAsync(this.databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task AllocateAsync_Should_Converge_Concurrent_Allocators_On_One_ChildExecutionId()
        {
            var relation = await CreateApprovedRelationAsync();
            var identity = relation.ToInvocationIdentity();

            var results = await Task.WhenAll(
                this.allocator.AllocateAsync(identity),
                this.allocator.AllocateAsync(identity));

            var persisted = await this.relationStore.GetAsync(identity);
            Assert.NotNull(persisted);
            Assert.Equal(AiChildExecutionRelationStatus.ChildAllocated, persisted!.Status);
            Assert.False(string.IsNullOrWhiteSpace(persisted.ChildExecutionId));
            Assert.NotNull(persisted.ChildAllocatedAtUtc);
            Assert.All(results, result => Assert.Equal(persisted.ChildExecutionId, result.ChildExecutionId));
        }

        [Fact]
        public async Task AllocateAsync_Should_Return_Same_Mapping_On_Ordinary_Retry()
        {
            var relation = await CreateApprovedRelationAsync();
            var identity = relation.ToInvocationIdentity();

            var first = await this.allocator.AllocateAsync(identity);
            var second = await this.allocator.AllocateAsync(identity);

            Assert.Equal(first.ChildExecutionId, second.ChildExecutionId);
            Assert.Equal(0, first.InvocationGeneration);
            Assert.Equal(0, second.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.ChildAllocated, second.Status);
        }

        [Fact]
        public async Task AllocateAsync_Should_Not_Allocate_When_Delegation_Is_Denied()
        {
            var relation = await CreateDecidedRelationAsync(
                AiChildExecutionRelationStatus.DelegationDenied,
                definitionVersion: "v1",
                identityVersion: "v1");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.allocator.AllocateAsync(relation.ToInvocationIdentity()));

            var persisted = await this.relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationDenied, persisted!.Status);
            Assert.Null(persisted.ChildExecutionId);
            Assert.Null(persisted.ChildAllocatedAtUtc);
        }

        [Fact]
        public async Task AllocateAsync_Should_Reject_Frozen_Definition_Version_Mismatch_Before_Mapping()
        {
            var relation = await CreateDecidedRelationAsync(
                AiChildExecutionRelationStatus.DelegationApproved,
                definitionVersion: "v2",
                identityVersion: "v1");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.allocator.AllocateAsync(relation.ToInvocationIdentity()));

            var persisted = await this.relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationApproved, persisted!.Status);
            Assert.Null(persisted.ChildExecutionId);
        }

        [Fact]
        public async Task AllocateAsync_Should_Reject_NonDag_Frozen_Definition_Before_Mapping()
        {
            var relation = await CreateDecidedRelationAsync(
                AiChildExecutionRelationStatus.DelegationApproved,
                definitionVersion: "v1",
                identityVersion: "v1",
                executionMode: AiExecutionMode.Sequential);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.allocator.AllocateAsync(relation.ToInvocationIdentity()));

            var persisted = await this.relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Null(persisted!.ChildExecutionId);
        }

        [Fact]
        public async Task Explicit_New_Generation_Should_Allocate_Exactly_One_New_ChildExecutionId()
        {
            var generationZero = await CreateApprovedRelationAsync();
            var allocatedZero = await this.allocator.AllocateAsync(generationZero.ToInvocationIdentity());

            allocatedZero.Status = AiChildExecutionRelationStatus.Completed;
            allocatedZero.ChildResult = AiStoredPayload.Inline(
                "{}",
                contentType: "application/json",
                contentHash: "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
            allocatedZero.ChildFailureReason = "child execution failed";
            allocatedZero.CompletedAtUtc = DateTimeOffset.UtcNow;
            allocatedZero.ContinuationStatus = AiChildContinuationStatus.Resumed;
            allocatedZero.ParentContinuationScheduledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            allocatedZero.ParentContinuationScheduledStepVersion = 10;
            allocatedZero.ParentResumedAtUtc = DateTimeOffset.UtcNow;

            Assert.True(await this.relationStore.TryReplaceAsync(
                allocatedZero,
                AiChildExecutionRelationStatus.ChildAllocated));

            var generationCoordinator = new AiChildInvocationGenerationCoordinator(this.relationStore);
            var generationOne = await generationCoordinator.PrepareNextGenerationAsync(
                allocatedZero.ToInvocationIdentity(),
                "explicit child retry");

            generationOne.DelegationPolicyDecisionSnapshot = await this.snapshotService
                .FreezeDelegationPolicyDecisionAsync(
                    approved: true,
                    reason: "approved retry generation",
                    results: new[] { AiPolicyResult.Success("approved retry generation") },
                    generationOne.ParentExecutionId);
            generationOne.DelegationEvaluatedAtUtc = DateTimeOffset.UtcNow;
            generationOne.Status = AiChildExecutionRelationStatus.DelegationApproved;

            Assert.True(await this.relationStore.TryReplaceAsync(
                generationOne,
                AiChildExecutionRelationStatus.DelegationPolicyPending));

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => this.allocator.AllocateAsync(generationOne.ToInvocationIdentity())));

            var allocatedOne = await this.relationStore.GetAsync(generationOne.ToInvocationIdentity());
            Assert.NotNull(allocatedOne);
            Assert.Equal(1, allocatedOne!.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.ChildAllocated, allocatedOne.Status);
            Assert.False(string.IsNullOrWhiteSpace(allocatedOne.ChildExecutionId));
            Assert.NotEqual(allocatedZero.ChildExecutionId, allocatedOne.ChildExecutionId);
            Assert.All(results, result => Assert.Equal(allocatedOne.ChildExecutionId, result.ChildExecutionId));
        }

        [Fact]
        public async Task AllocateAsync_Should_Not_Implicitly_Create_A_New_Generation()
        {
            var relation = await CreateApprovedRelationAsync();
            var allocated = await this.allocator.AllocateAsync(relation.ToInvocationIdentity());
            var nextGenerationIdentity = new AiChildInvocationIdentity
            {
                TenantId = relation.TenantId,
                ParentExecutionId = relation.ParentExecutionId,
                ParentCallSiteId = relation.ParentCallSiteId,
                ChildDagId = relation.ChildDagId,
                ChildDagDefinitionVersion = relation.ChildDagDefinitionVersion,
                CanonicalLogicalInvocationKey = relation.CanonicalLogicalInvocationKey,
                InvocationGeneration = relation.InvocationGeneration + 1
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => this.allocator.AllocateAsync(nextGenerationIdentity));

            var original = await this.relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.NotNull(original);
            Assert.Equal(allocated.ChildExecutionId, original!.ChildExecutionId);
            Assert.Equal(0, original.InvocationGeneration);
            Assert.Null(await this.relationStore.GetAsync(nextGenerationIdentity));
        }

        private Task<AiChildExecutionRelation> CreateApprovedRelationAsync()
        {
            return CreateDecidedRelationAsync(
                AiChildExecutionRelationStatus.DelegationApproved,
                definitionVersion: "v1",
                identityVersion: "v1");
        }

        private async Task<AiChildExecutionRelation> CreateDecidedRelationAsync(
            AiChildExecutionRelationStatus decisionStatus,
            string definitionVersion,
            string identityVersion,
            AiExecutionMode executionMode = AiExecutionMode.Dag)
        {
            if (decisionStatus is not AiChildExecutionRelationStatus.DelegationApproved and
                not AiChildExecutionRelationStatus.DelegationDenied)
            {
                throw new ArgumentOutOfRangeException(nameof(decisionStatus));
            }

            var definition = new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = definitionVersion,
                ExecutionMode = executionMode,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "analyze",
                        StepKey = "analysis",
                        Order = 0
                    }
                ]
            };
            var frozenDefinition = await this.snapshotService
                .FreezeDefinitionAsync(definition, "parent-execution-1");
            var frozenInput = await this.snapshotService
                .FreezeInvocationInputAsync(
                    new Dictionary<string, object?> { ["request"] = "analyze" },
                    "parent-execution-1");
            var frozenBinding = await this.snapshotService
                .FreezeDelegationPolicyBindingAsync(
                    new AiChildDelegationPolicyDefinition(),
                    "parent-execution-1");
            var identity = new AiChildInvocationIdentity
            {
                TenantId = "tenant-1",
                ParentExecutionId = "parent-execution-1",
                ParentCallSiteId = "delegate-child",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = identityVersion,
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|fundamental-research",
                InvocationGeneration = 0
            };
            var relation = new AiChildExecutionRelation
            {
                TenantId = identity.TenantId,
                ControlPlaneId = "control-plane-allocation-tests",
                ParentExecutionId = identity.ParentExecutionId,
                ParentCallSiteId = identity.ParentCallSiteId,
                ChildDagId = identity.ChildDagId,
                ChildDagDefinitionVersion = identity.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = frozenDefinition,
                CanonicalLogicalInvocationKey = identity.CanonicalLogicalInvocationKey,
                ChildInvocationKey = AiChildInvocationKeyFactory.Create(identity),
                InvocationGeneration = identity.InvocationGeneration,
                FrozenInvocationInput = frozenInput,
                DelegationPolicyBindingSnapshot = frozenBinding,
                Status = AiChildExecutionRelationStatus.DelegationPolicyPending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            await this.relationStore.GetOrCreateAsync(relation);

            relation.DelegationPolicyDecisionSnapshot = await this.snapshotService
                .FreezeDelegationPolicyDecisionAsync(
                    approved: decisionStatus == AiChildExecutionRelationStatus.DelegationApproved,
                    reason: decisionStatus == AiChildExecutionRelationStatus.DelegationApproved
                        ? "approved"
                        : "denied",
                    results: decisionStatus == AiChildExecutionRelationStatus.DelegationApproved
                        ? new[] { AiPolicyResult.Success("approved") }
                        : new[] { AiPolicyResult.Block("denied") },
                    relation.ParentExecutionId);
            relation.DelegationEvaluatedAtUtc = DateTimeOffset.UtcNow;
            relation.Status = decisionStatus;

            Assert.True(
                await this.relationStore.TryReplaceAsync(
                    relation,
                    AiChildExecutionRelationStatus.DelegationPolicyPending));

            return relation;
        }

        private static AiChildDagSnapshotService CreateSnapshotService()
        {
            var store = new InMemoryAiPayloadStore();
            return new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(store),
                Options.Create(
                    new AiPayloadStoreOptions
                    {
                        Enabled = true,
                        Provider = "inmemory",
                        RequireReplaySafePayloads = false,
                        MaxInlineSizeBytes = 64 * 1024
                    }));
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
