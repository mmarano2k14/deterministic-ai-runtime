using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension;
using Multiplexed.AI.Runtime.Pipeline.Definition;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Adversarial
{
    /// <summary>
    /// Exercises the native child-DAG step across durable crash windows by reconstructing transient coordinators.
    /// </summary>
    public sealed class ExecuteChildDagStepAdversarialTests
    {
        [Fact]
        public async Task CrashMatrix_P0_Should_Recover_Durable_Preparation_Without_Live_Definition_Refetch()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var identity = CreateIdentity(invocationGeneration: 0);
            var executionContextSnapshot = new ExecutionContextSnapshot
            {
                ContextKey = "parent-context",
                Project = "tests",
                UserId = "user-1",
                TenantId = ChildDagCompositionTestData.TenantId,
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = [],
                TtlSeconds = 300
            };
            var definition = await CreateDefinitionSelector()
                .Select(identity.ChildDagId)
                .GetDefinitionAsync(identity.ChildDagId);
            var frozenDefinition = await snapshotService.FreezeDefinitionAsync(
                definition,
                identity.ParentExecutionId);
            var frozenInput = await snapshotService.FreezeInvocationInputAsync(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["symbol"] = "MSFT",
                    ["portfolioId"] = "portfolio-42"
                },
                identity.ParentExecutionId);
            var frozenPolicyBinding = await snapshotService.FreezeDelegationPolicyBindingAsync(
                new AiChildDelegationPolicyDefinition(),
                identity.ParentExecutionId);
            var preparation = await snapshotService.FreezeInvocationPreparationAsync(
                identity,
                ChildDagCompositionTestData.ControlPlaneId,
                frozenDefinition,
                frozenInput,
                executionContextSnapshot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["parent-proof"] = "durable"
                },
                frozenPolicyBinding);

            Assert.Equal(0, relationStore.Count);

            var recoveredStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                new ThrowingPipelineDefinitionSourceSelector());
            var result = await recoveredStep.ExecuteAsync(CreateParentContext(recoveredStep));

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            var relation = await relationStore.GetAsync(identity);
            Assert.NotNull(relation);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, relation!.Status);
            Assert.Equal(ChildDagCompositionTestData.ControlPlaneId, relation.ControlPlaneId);
            Assert.Equal(preparation.ChildInvocationKey, relation.ChildInvocationKey);
            Assert.Equal(preparation.FrozenChildDagDefinition.ContentHash, relation.FrozenChildDagDefinition.ContentHash);
            Assert.Equal(preparation.FrozenInvocationInput.ContentHash, relation.FrozenInvocationInput.ContentHash);
            Assert.Equal(preparation.DelegationPolicyBindingSnapshot.ContentHash, relation.DelegationPolicyBindingSnapshot.ContentHash);
            Assert.Equal("durable", relation.DelegatedMetadata["parent-proof"]);
        }

        [Fact]
        public async Task NativeComposition_Should_Create_One_Logical_Child_And_Park_Parent()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var step = CreateStep(
                relationStore,
                snapshotService,
                controller,
                CreateDefinitionSelector());
            var context = CreateParentContext(step);

            var result = await step.ExecuteAsync(context);

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            Assert.Equal(1, relationStore.Count);

            var relation = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(relation);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, relation!.Status);
            Assert.Equal(AiChildContinuationStatus.None, relation.ContinuationStatus);
            Assert.False(string.IsNullOrWhiteSpace(relation.ChildExecutionId));
            Assert.NotNull(relation.ChildAllocatedAtUtc);
            Assert.NotNull(relation.WaitingAtUtc);
            Assert.NotNull(relation.DelegationPolicyDecisionSnapshot);

            var request = Assert.Single(controller.Requests);
            Assert.Equal(relation.ChildExecutionId, request.RunRequest!.RequestedExecutionId);
            Assert.Equal($"child-execution-{relation.ChildExecutionId}", request.RequestedSharedRunId);
            Assert.Equal(relation.FrozenChildDagDefinition.ContentHash, request.RunRequest.PipelineDefinitionSnapshot?.ContentHash);
        }

        [Fact]
        public async Task CrashMatrix_B_D_Should_Redrive_Same_Physical_Identity_After_Dispatch_Before_Wait()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var capture = new CapturingSharedRuntimeController();
            var crashController = new ThrowAfterAcceptedSubmissionController(capture);
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var crashingStep = CreateStep(
                relationStore,
                snapshotService,
                crashController,
                CreateDefinitionSelector());

            await Assert.ThrowsAsync<SimulatedProcessCrashException>(
                () => crashingStep.ExecuteAsync(CreateParentContext(crashingStep)));

            var afterCrash = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(afterCrash);
            Assert.Equal(AiChildExecutionRelationStatus.ChildAllocated, afterCrash!.Status);
            Assert.False(string.IsNullOrWhiteSpace(afterCrash.ChildExecutionId));
            Assert.Null(afterCrash.WaitingAtUtc);

            var firstRequest = Assert.Single(capture.Requests);
            Assert.Equal(afterCrash.ChildExecutionId, firstRequest.RunRequest!.RequestedExecutionId);

            var recoveredStep = CreateStep(
                relationStore,
                snapshotService,
                capture,
                new ThrowingPipelineDefinitionSourceSelector());
            var recoveredResult = await recoveredStep.ExecuteAsync(CreateParentContext(recoveredStep));

            Assert.Equal(AiStepExecutionOutcome.Park, recoveredResult.EffectiveOutcome);
            var recovered = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(recovered);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, recovered!.Status);
            Assert.Equal(afterCrash.ChildExecutionId, recovered.ChildExecutionId);

            Assert.Equal(2, capture.Requests.Count);
            Assert.All(
                capture.Requests,
                request => Assert.Equal(afterCrash.ChildExecutionId, request.RunRequest!.RequestedExecutionId));
            Assert.Single(
                capture.Requests
                    .Select(request => request.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal));
        }

        [Fact]
        public async Task CrashMatrix_F_Should_Redrive_Park_From_Durable_Waiting_Relation_Without_Redispatch()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var firstStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                CreateDefinitionSelector());

            var firstResult = await firstStep.ExecuteAsync(CreateParentContext(firstStep));
            Assert.Equal(AiStepExecutionOutcome.Park, firstResult.EffectiveOutcome);

            var beforeCrash = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(beforeCrash);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, beforeCrash!.Status);
            var dispatchCount = controller.Requests.Count;

            var recoveredStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                new ThrowingPipelineDefinitionSourceSelector());
            var recoveredResult = await recoveredStep.ExecuteAsync(CreateParentContext(recoveredStep));

            Assert.Equal(AiStepExecutionOutcome.Park, recoveredResult.EffectiveOutcome);
            Assert.Equal(dispatchCount, controller.Requests.Count);
            var recovered = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(recovered);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, recovered!.Status);
            Assert.Equal(beforeCrash.ChildExecutionId, recovered.ChildExecutionId);
        }

        [Fact]
        public async Task CrashMatrix_E_Should_Consume_Child_Result_When_Child_Completes_Before_Parent_Park()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var capture = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var completionController = new CompleteChildDuringDispatchController(
                capture,
                relationStore,
                CreateIdentity(invocationGeneration: 0));
            var step = CreateStep(
                relationStore,
                snapshotService,
                completionController,
                CreateDefinitionSelector());

            var result = await step.ExecuteAsync(CreateParentContext(step));

            Assert.Equal(AiStepExecutionOutcome.Complete, result.EffectiveOutcome);
            Assert.NotNull(result.Payload);
            var relation = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(relation);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, relation!.Status);
            Assert.Null(relation.WaitingAtUtc);
            Assert.Equal(relation.ChildResult!.ContentHash, result.Payload!.ContentHash);
            Assert.Single(capture.Requests);
        }

        [Fact]
        public async Task CrashMatrix_N_Should_Consume_Authoritative_Completed_Result_Without_New_Child()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var firstStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                CreateDefinitionSelector());

            var parked = await firstStep.ExecuteAsync(CreateParentContext(firstStep));
            Assert.Equal(AiStepExecutionOutcome.Park, parked.EffectiveOutcome);

            var relation = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(relation);
            var childExecutionId = relation!.ChildExecutionId;
            var dispatchCount = controller.Requests.Count;

            relation.Status = AiChildExecutionRelationStatus.Completed;
            relation.ChildResult = AiStoredPayload.Inline(
                "{\"Data\":{\"answer\":42},\"DataPayloads\":null}",
                contentType: "application/json",
                contentHash: "f845fcc8d0cbfe9ca31e831f11bafed76e27f6136b9bb83cc046686aade15a5a");
            relation.CompletedAtUtc = DateTimeOffset.UtcNow;
            relation.ContinuationStatus = AiChildContinuationStatus.Pending;
            Assert.True(await relationStore.TryReplaceAsync(
                relation,
                AiChildExecutionRelationStatus.Waiting));

            var recoveredStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                new ThrowingPipelineDefinitionSourceSelector());
            var result = await recoveredStep.ExecuteAsync(CreateParentContext(recoveredStep));

            Assert.Equal(AiStepExecutionOutcome.Complete, result.EffectiveOutcome);
            Assert.NotNull(result.Payload);
            Assert.Equal(relation.ChildResult.ContentHash, result.Payload!.ContentHash);
            Assert.Equal(childExecutionId, result.Data["childExecutionId"]);
            Assert.Equal(dispatchCount, controller.Requests.Count);

            var persisted = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(persisted);
            Assert.Equal(childExecutionId, persisted!.ChildExecutionId);
            Assert.Equal(0, persisted.InvocationGeneration);
        }

        [Fact]
        public async Task CrashMatrix_S_Should_Follow_Durable_Next_Generation_Without_Latest_Definition_Refetch()
        {
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = ChildDagCompositionTestData.CreateSnapshotService();
            var firstStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                CreateDefinitionSelector());

            Assert.Equal(
                AiStepExecutionOutcome.Park,
                (await firstStep.ExecuteAsync(CreateParentContext(firstStep))).EffectiveOutcome);

            var generationZero = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 0));
            Assert.NotNull(generationZero);
            var generationZeroExecutionId = generationZero!.ChildExecutionId;

            generationZero.Status = AiChildExecutionRelationStatus.Completed;
            generationZero.ChildResult = ChildDagCompositionTestData.Snapshot();
            generationZero.ChildFailureReason = "child execution failed";
            generationZero.CompletedAtUtc = DateTimeOffset.UtcNow;
            generationZero.ContinuationStatus = AiChildContinuationStatus.Resumed;
            generationZero.ParentContinuationScheduledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2);
            generationZero.ParentContinuationScheduledStepVersion = 10;
            generationZero.ParentResumedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            Assert.True(await relationStore.TryReplaceAsync(
                generationZero,
                AiChildExecutionRelationStatus.Waiting));

            var generationCoordinator = new AiChildInvocationGenerationCoordinator(relationStore);
            var generationOne = await generationCoordinator.PrepareNextGenerationAsync(
                generationZero.ToInvocationIdentity(),
                "explicit retry after durable child failure");

            Assert.Equal(1, generationOne.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, generationOne.Status);

            var recoveredStep = CreateStep(
                relationStore,
                snapshotService,
                controller,
                new ThrowingPipelineDefinitionSourceSelector());
            var result = await recoveredStep.ExecuteAsync(CreateParentContext(recoveredStep));

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            Assert.Equal(2, relationStore.Count);

            var persistedGenerationOne = await relationStore.GetAsync(CreateIdentity(invocationGeneration: 1));
            Assert.NotNull(persistedGenerationOne);
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, persistedGenerationOne!.Status);
            Assert.False(string.IsNullOrWhiteSpace(persistedGenerationOne.ChildExecutionId));
            Assert.NotEqual(generationZeroExecutionId, persistedGenerationOne.ChildExecutionId);
            Assert.Equal(
                generationZero.FrozenChildDagDefinition.ContentHash,
                persistedGenerationOne.FrozenChildDagDefinition.ContentHash);
            Assert.Equal(
                generationZero.FrozenInvocationInput.ContentHash,
                persistedGenerationOne.FrozenInvocationInput.ContentHash);
        }

        private static ExecuteChildDagStep CreateStep(
            InMemoryAiChildExecutionRelationStore relationStore,
            AiChildDagSnapshotService snapshotService,
            IAiSharedRuntimeController controller,
            IAiPipelineDefinitionSourceSelector definitionSourceSelector)
        {
            var policyCoordinator = new AiChildDelegationPolicyCoordinator(
                relationStore,
                new AllowAllPolicyEngineFactory(),
                snapshotService);

            return new ExecuteChildDagStep(
                relationStore,
                new StaticAiControlPlaneIdResolver(ChildDagCompositionTestData.ControlPlaneId),
                definitionSourceSelector,
                snapshotService,
                policyCoordinator,
                new AiChildExecutionAllocator(relationStore, snapshotService),
                new AiChildExecutionDispatcher(relationStore, snapshotService, controller),
                new AiChildExecutionWaitingCoordinator(relationStore),
                new AiChildInvocationGenerationCoordinator(relationStore));
        }

        private static AiStepExecutionContext CreateParentContext(ExecuteChildDagStep step)
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = ChildDagCompositionTestData.ParentExecutionId,
                PipelineName = "parent-pipeline",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running,
                ExecutionContextSnapshot = new ExecutionContextSnapshot
                {
                    ContextKey = "parent-context",
                    Project = "tests",
                    UserId = "user-1",
                    TenantId = ChildDagCompositionTestData.TenantId,
                    TenantGroupId = "tenant-group-1",
                    CurrentNamespace = "default",
                    Namespaces = [],
                    TtlSeconds = 300
                }
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName!,
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["parent-proof"] = "durable"
                }
            };
            var execution = ChildDagCompositionTestData.CreateExecutionContext(record, state);
            var resolvedStep = new ResolvedAiPipelineStep
            {
                Name = ChildDagCompositionTestData.ParentCallSiteId,
                StepKey = ExecuteChildDagStep.StepKey,
                Step = step,
                Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["childDagId"] = "child-analysis",
                    ["childDagVersion"] = "v1",
                    ["logicalInvocationKey"] = "portfolio-42|MSFT|analysis"
                },
                Input = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["symbol"] = "MSFT",
                    ["portfolioId"] = "portfolio-42"
                }
            };

            return new AiStepExecutionContext(execution, resolvedStep);
        }

        private static IAiPipelineDefinitionSourceSelector CreateDefinitionSelector()
        {
            return new FixedPipelineDefinitionSourceSelector(
                new InMemoryAiPipelineDefinitionProvider(
                [
                    new AiPipelineDefinition
                    {
                        Name = "child-analysis",
                        Version = "v1",
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
                    }
                ]));
        }

        private static AiChildInvocationIdentity CreateIdentity(int invocationGeneration)
        {
            return new AiChildInvocationIdentity
            {
                TenantId = ChildDagCompositionTestData.TenantId,
                ParentExecutionId = ChildDagCompositionTestData.ParentExecutionId,
                ParentCallSiteId = ChildDagCompositionTestData.ParentCallSiteId,
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                InvocationGeneration = invocationGeneration
            };
        }

        private sealed class FixedPipelineDefinitionSourceSelector : IAiPipelineDefinitionSourceSelector
        {
            private readonly IAiPipelineDefinitionProvider provider;

            public FixedPipelineDefinitionSourceSelector(IAiPipelineDefinitionProvider provider)
            {
                this.provider = provider;
            }

            public IAiPipelineDefinitionProvider Select(string pipelineName) => this.provider;
        }

        private sealed class ThrowingPipelineDefinitionSourceSelector : IAiPipelineDefinitionSourceSelector
        {
            public IAiPipelineDefinitionProvider Select(string pipelineName)
            {
                throw new InvalidOperationException(
                    "Recovery attempted to resolve a mutable live child definition instead of using the durable relation.");
            }
        }

        private sealed class AllowAllPolicyEngineFactory : IAiPolicyEngineFactory
        {
            public IAiPolicyEngine Create(AiPolicyKind kind, AiStepExecutionContext stepContext)
            {
                return new AllowAllChildDelegationPolicyEngine(stepContext);
            }

            public TPolicyEngine Create<TPolicyEngine>(
                AiPolicyKind kind,
                AiStepExecutionContext stepContext)
                where TPolicyEngine : class, IAiPolicyEngine
            {
                return (TPolicyEngine)(IAiPolicyEngine)new AllowAllChildDelegationPolicyEngine(stepContext);
            }
        }

        private sealed class AllowAllChildDelegationPolicyEngine : IAiChildDelegationPolicyEngine
        {
            public AllowAllChildDelegationPolicyEngine(AiStepExecutionContext stepContext)
            {
                StepContext = stepContext;
            }

            public AiPolicyKind Kind => AiPolicyKind.Delegation;

            public AiStepExecutionContext StepContext { get; }

            public Task<AiChildDelegationPolicyDefinition> ResolveDefinitionAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AiChildDelegationPolicyDefinition());
            }

            public Task<IReadOnlyCollection<AiPolicyResult>> EvaluateAsync(
                AiChildExecutionRelation relation,
                AiChildDelegationPolicyDefinition definition,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyCollection<AiPolicyResult>>(Array.Empty<AiPolicyResult>());
            }
        }

        private sealed class CompleteChildDuringDispatchController : IAiSharedRuntimeController
        {
            private readonly CapturingSharedRuntimeController inner;
            private readonly InMemoryAiChildExecutionRelationStore relationStore;
            private readonly AiChildInvocationIdentity identity;

            public CompleteChildDuringDispatchController(
                CapturingSharedRuntimeController inner,
                InMemoryAiChildExecutionRelationStore relationStore,
                AiChildInvocationIdentity identity)
            {
                this.inner = inner;
                this.relationStore = relationStore;
                this.identity = identity;
            }

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => SubmitRunAsync(request, cancellationToken);

            public async Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                var result = await this.inner.SubmitRunAsync(request, cancellationToken).ConfigureAwait(false);
                var relation = await this.relationStore
                    .GetAsync(this.identity, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Allocated child relation disappeared during completion race simulation.");

                relation.Status = AiChildExecutionRelationStatus.Completed;
                relation.ChildResult = ChildDagCompositionTestData.Snapshot();
                relation.CompletedAtUtc = DateTimeOffset.UtcNow;
                relation.ContinuationStatus = AiChildContinuationStatus.Pending;

                if (!await this.relationStore
                        .TryReplaceAsync(
                            relation,
                            AiChildExecutionRelationStatus.ChildAllocated,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Completion race simulation could not commit the terminal child relation.");
                }

                return result;
            }

            public Task<AiSharedRuntimeControllerResult> GetRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.GetRunAsync(request, cancellationToken);

            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.ListRunsAsync(request, cancellationToken);

            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.CancelRunAsync(request, cancellationToken);
        }

        private sealed class ThrowAfterAcceptedSubmissionController : IAiSharedRuntimeController
        {
            private readonly CapturingSharedRuntimeController inner;
            private int hasThrown;

            public ThrowAfterAcceptedSubmissionController(CapturingSharedRuntimeController inner)
            {
                this.inner = inner;
            }

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => SubmitRunAsync(request, cancellationToken);

            public async Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                var result = await this.inner.SubmitRunAsync(request, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref this.hasThrown, 1) == 0)
                {
                    throw new SimulatedProcessCrashException();
                }

                return result;
            }

            public Task<AiSharedRuntimeControllerResult> GetRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.GetRunAsync(request, cancellationToken);

            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.ListRunsAsync(request, cancellationToken);

            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => this.inner.CancelRunAsync(request, cancellationToken);
        }

        private sealed class SimulatedProcessCrashException : Exception
        {
        }

    }
}
