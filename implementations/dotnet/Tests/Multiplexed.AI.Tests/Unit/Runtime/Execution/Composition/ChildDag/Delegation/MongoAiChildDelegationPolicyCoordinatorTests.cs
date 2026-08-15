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
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Validates durable child delegation policy convergence against the MongoDB relation authority.
    /// </summary>
    public sealed class MongoAiChildDelegationPolicyCoordinatorTests : IAsyncLifetime
    {
        private readonly string databaseName;
        private readonly MongoClient client;
        private readonly IMongoDatabase database;
        private readonly MongoAiChildExecutionRelationStore relationStore;
        private readonly AiChildDagSnapshotService snapshotService;

        public MongoAiChildDelegationPolicyCoordinatorTests()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";

            this.databaseName = $"multiplexed_child_delegation_{Guid.NewGuid():N}";
            this.client = new MongoClient(connectionString);
            this.database = this.client.GetDatabase(this.databaseName);
            this.relationStore = new MongoAiChildExecutionRelationStore(
                this.database,
                Options.Create(
                    new AiChildExecutionRelationMongoOptions
                    {
                        CollectionName = "relations",
                        EnsureIndexes = true
                    }));
            this.snapshotService = CreateSnapshotService();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await this.client.DropDatabaseAsync(this.databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Converge_Concurrent_Evaluators_On_One_Committed_Decision()
        {
            var relation = await CreateRelationAsync();
            await this.relationStore.GetOrCreateAsync(relation);

            var barrier = new AsyncEvaluationBarrier(2);
            var factory = new SequencedPolicyEngineFactory(
                index => index == 0
                    ? new[] { AiPolicyResult.Success("approved") }
                    : new[] { AiPolicyResult.Block("denied") },
                barrier);
            var coordinator = new AiChildDelegationPolicyCoordinator(
                this.relationStore,
                factory,
                this.snapshotService);
            var stepContext = CreateStepContext();
            var identity = relation.ToInvocationIdentity();

            var results = await Task.WhenAll(
                coordinator.EvaluateAsync(identity, stepContext),
                coordinator.EvaluateAsync(identity, stepContext));

            var persisted = await this.relationStore.GetAsync(identity);
            Assert.NotNull(persisted);
            Assert.Equal(2, factory.EvaluationCount);
            Assert.All(results, result => Assert.Equal(persisted!.Status, result.Status));
            Assert.All(
                results,
                result => Assert.Equal(
                    persisted!.DelegationPolicyDecisionSnapshot!.ContentHash,
                    result.DelegationPolicyDecisionSnapshot!.ContentHash));
            Assert.True(
                persisted!.Status is AiChildExecutionRelationStatus.DelegationApproved or
                    AiChildExecutionRelationStatus.DelegationDenied);
            Assert.NotNull(persisted.DelegationPolicyDecisionSnapshot);
            Assert.NotNull(persisted.DelegationEvaluatedAtUtc);
            Assert.Null(persisted.ChildExecutionId);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Commit_Approved_Without_Allocating_ChildExecutionId()
        {
            var relation = await CreateRelationAsync();
            await this.relationStore.GetOrCreateAsync(relation);
            var factory = new SequencedPolicyEngineFactory(
                _ => new[] { AiPolicyResult.Success("tenant delegation approved") });
            var coordinator = new AiChildDelegationPolicyCoordinator(
                this.relationStore,
                factory,
                this.snapshotService);

            var result = await coordinator.EvaluateAsync(
                relation.ToInvocationIdentity(),
                CreateStepContext());

            Assert.Equal(AiChildExecutionRelationStatus.DelegationApproved, result.Status);
            Assert.Null(result.ChildExecutionId);
            Assert.NotNull(result.DelegationPolicyDecisionSnapshot);
            Assert.NotNull(result.DelegationEvaluatedAtUtc);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Commit_Denied_Without_Allocating_ChildExecutionId()
        {
            var relation = await CreateRelationAsync();
            await this.relationStore.GetOrCreateAsync(relation);
            var factory = new SequencedPolicyEngineFactory(
                _ => new[] { AiPolicyResult.Block("tenant delegation denied") });
            var coordinator = new AiChildDelegationPolicyCoordinator(
                this.relationStore,
                factory,
                this.snapshotService);

            var result = await coordinator.EvaluateAsync(
                relation.ToInvocationIdentity(),
                CreateStepContext());

            Assert.Equal(AiChildExecutionRelationStatus.DelegationDenied, result.Status);
            Assert.Null(result.ChildExecutionId);
            Assert.NotNull(result.DelegationPolicyDecisionSnapshot);
            Assert.NotNull(result.DelegationEvaluatedAtUtc);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Leave_Relation_Pending_When_Policy_Result_Is_Not_Approval_Or_Block()
        {
            var relation = await CreateRelationAsync();
            await this.relationStore.GetOrCreateAsync(relation);
            var factory = new SequencedPolicyEngineFactory(
                _ => new[] { AiPolicyResult.Retry("policy dependency unavailable") });
            var coordinator = new AiChildDelegationPolicyCoordinator(
                this.relationStore,
                factory,
                this.snapshotService);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.EvaluateAsync(
                    relation.ToInvocationIdentity(),
                    CreateStepContext()));

            var persisted = await this.relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.NotNull(persisted);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, persisted!.Status);
            Assert.Null(persisted.DelegationPolicyDecisionSnapshot);
            Assert.Null(persisted.DelegationEvaluatedAtUtc);
            Assert.Null(persisted.ChildExecutionId);
        }

        [Fact]
        public async Task ResolveAndFreezeBindingAsync_Should_Persist_Exact_Resolved_Definition_Before_Relation_Creation()
        {
            var definition = new AiChildDelegationPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = "delegation.frozen"
                    }
                ]
            };
            var factory = new SequencedPolicyEngineFactory(
                _ => Array.Empty<AiPolicyResult>(),
                resolvedDefinition: definition);
            var coordinator = new AiChildDelegationPolicyCoordinator(
                this.relationStore,
                factory,
                this.snapshotService);

            var snapshot = await coordinator.ResolveAndFreezeBindingAsync(CreateStepContext());
            var restored = await this.snapshotService.LoadDelegationPolicyBindingAsync(snapshot);

            Assert.Equal("delegation.frozen", Assert.Single(restored.Policies).Name);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.ContentHash));
        }

        private async Task<AiChildExecutionRelation> CreateRelationAsync()
        {
            var definition = new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = "v1",
                Steps = Array.Empty<AiPipelineStepDefinition>()
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
                FrozenChildDagDefinition = frozenDefinition,
                CanonicalLogicalInvocationKey = identity.CanonicalLogicalInvocationKey,
                ChildInvocationKey = AiChildInvocationKeyFactory.Create(identity),
                InvocationGeneration = identity.InvocationGeneration,
                FrozenInvocationInput = frozenInput,
                DelegationPolicyBindingSnapshot = frozenBinding,
                Status = AiChildExecutionRelationStatus.DelegationPolicyPending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
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

        private static AiStepExecutionContext CreateStepContext()
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = "parent-execution-1",
                PipelineName = "parent-pipeline"
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName
            };
            var resolvedStep = new ResolvedAiPipelineStep
            {
                Name = "delegate-child",
                StepKey = "delegate-child",
                Step = new NoOpStep()
            };
            var executionContext = ChildDagCompositionTestData.CreateExecutionContext(record, state);

            return new AiStepExecutionContext(executionContext, resolvedStep);
        }

        private sealed class SequencedPolicyEngineFactory : IAiPolicyEngineFactory
        {
            private readonly Func<int, IReadOnlyCollection<AiPolicyResult>> resultFactory;
            private readonly AsyncEvaluationBarrier? barrier;
            private readonly AiChildDelegationPolicyDefinition resolvedDefinition;
            private int createdCount;
            private int evaluationCount;

            public SequencedPolicyEngineFactory(
                Func<int, IReadOnlyCollection<AiPolicyResult>> resultFactory,
                AsyncEvaluationBarrier? barrier = null,
                AiChildDelegationPolicyDefinition? resolvedDefinition = null)
            {
                this.resultFactory = resultFactory;
                this.barrier = barrier;
                this.resolvedDefinition = resolvedDefinition ?? new AiChildDelegationPolicyDefinition();
            }

            public int EvaluationCount => Volatile.Read(ref this.evaluationCount);

            public IAiPolicyEngine Create(AiPolicyKind kind, AiStepExecutionContext stepContext)
            {
                if (kind != AiPolicyKind.Delegation)
                {
                    throw new InvalidOperationException($"Unexpected policy kind '{kind}'.");
                }

                var index = Interlocked.Increment(ref this.createdCount) - 1;
                return new StubDelegationPolicyEngine(
                    stepContext,
                    this.resolvedDefinition,
                    this.resultFactory(index),
                    this.barrier,
                    () => Interlocked.Increment(ref this.evaluationCount));
            }

            public TPolicyEngine Create<TPolicyEngine>(
                AiPolicyKind kind,
                AiStepExecutionContext stepContext)
                where TPolicyEngine : class, IAiPolicyEngine
            {
                return (TPolicyEngine)Create(kind, stepContext);
            }
        }

        private sealed class StubDelegationPolicyEngine : IAiChildDelegationPolicyEngine
        {
            private readonly AiChildDelegationPolicyDefinition resolvedDefinition;
            private readonly IReadOnlyCollection<AiPolicyResult> results;
            private readonly AsyncEvaluationBarrier? barrier;
            private readonly Action recordEvaluation;

            public StubDelegationPolicyEngine(
                AiStepExecutionContext stepContext,
                AiChildDelegationPolicyDefinition resolvedDefinition,
                IReadOnlyCollection<AiPolicyResult> results,
                AsyncEvaluationBarrier? barrier,
                Action recordEvaluation)
            {
                StepContext = stepContext;
                this.resolvedDefinition = resolvedDefinition;
                this.results = results;
                this.barrier = barrier;
                this.recordEvaluation = recordEvaluation;
            }

            public AiPolicyKind Kind => AiPolicyKind.Delegation;

            public AiStepExecutionContext StepContext { get; }

            public Task<AiChildDelegationPolicyDefinition> ResolveDefinitionAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.resolvedDefinition);
            }

            public async Task<IReadOnlyCollection<AiPolicyResult>> EvaluateAsync(
                AiChildExecutionRelation relation,
                AiChildDelegationPolicyDefinition definition,
                CancellationToken cancellationToken = default)
            {
                this.recordEvaluation();
                if (this.barrier is not null)
                {
                    await this.barrier.SignalAndWaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return this.results;
            }
        }

        private sealed class AsyncEvaluationBarrier
        {
            private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int remaining;

            public AsyncEvaluationBarrier(int participantCount)
            {
                if (participantCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(participantCount));
                }

                this.remaining = participantCount;
            }

            public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Decrement(ref this.remaining) == 0)
                {
                    this.completion.TrySetResult(true);
                }

                await this.completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
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

        private sealed class NoOpStep : IAiStep
        {
            public string Name => "delegate-child";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiStepResult.Ok());
            }
        }

    }
}
