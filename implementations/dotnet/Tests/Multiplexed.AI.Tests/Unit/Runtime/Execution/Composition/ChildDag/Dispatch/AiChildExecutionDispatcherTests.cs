using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Dispatch
{
    public sealed class AiChildExecutionDispatcherTests
    {
        [Fact]
        public async Task DispatchAsync_Should_ReDrive_The_Same_Shared_Run_And_Exact_Execution_Id()
        {
            var payloadStore = new InMemoryAiPayloadStore();
            var snapshotService = new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(payloadStore),
                Options.Create(
                    new AiPayloadStoreOptions
                    {
                        Enabled = true,
                        Provider = "inmemory",
                        MaxInlineSizeBytes = 64 * 1024
                    }));
            var definition = new AiPipelineDefinition
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
            };
            var frozenDefinition = await snapshotService.FreezeDefinitionAsync(definition, "parent-1");
            var frozenInput = await snapshotService.FreezeInvocationInputAsync(
                new Dictionary<string, object?> { ["ticker"] = "MSFT" },
                "parent-1");
            var frozenPolicy = await snapshotService.FreezeDelegationPolicyBindingAsync(
                new AiChildDelegationPolicyDefinition(),
                "parent-1");
            var identity = new AiChildInvocationIdentity
            {
                TenantId = "tenant-1",
                ParentExecutionId = "parent-1",
                ParentCallSiteId = "research",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                InvocationGeneration = 0
            };
            var relation = new AiChildExecutionRelation
            {
                TenantId = identity.TenantId,
                ControlPlaneId = "control-plane-dispatch-tests",
                ParentExecutionId = identity.ParentExecutionId,
                ParentCallSiteId = identity.ParentCallSiteId,
                ChildDagId = identity.ChildDagId,
                ChildDagDefinitionVersion = identity.ChildDagDefinitionVersion,
                CanonicalLogicalInvocationKey = identity.CanonicalLogicalInvocationKey,
                InvocationGeneration = identity.InvocationGeneration,
                ChildInvocationKey = "child-invocation-key",
                FrozenChildDagDefinition = frozenDefinition,
                FrozenInvocationInput = frozenInput,
                DelegationPolicyBindingSnapshot = frozenPolicy,
                DelegatedExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                Status = AiChildExecutionRelationStatus.ChildAllocated,
                ChildExecutionId = "child-execution-123",
                ChildAllocatedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var controller = new CapturingSharedRuntimeController();
            var dispatcher = new AiChildExecutionDispatcher(
                new FixedRelationStore(relation),
                snapshotService,
                controller);

            var first = await dispatcher.DispatchAsync(identity);
            Assert.NotNull(controller.LastRequest);
            var firstRequest = controller.LastRequest!;
            var second = await dispatcher.DispatchAsync(identity);
            Assert.NotNull(controller.LastRequest);
            var secondRequest = controller.LastRequest!;

            Assert.Equal(first.SharedRunId, second.SharedRunId);
            Assert.Equal("child-execution-child-execution-123", second.SharedRunId);
            Assert.Equal(firstRequest.RequestedSharedRunId, secondRequest.RequestedSharedRunId);
            Assert.Equal(AiSharedRuntimeSubmitMode.QueueFirst, secondRequest.SubmitModeOverride);
            Assert.Equal("child-execution-123", secondRequest.RunRequest?.RequestedExecutionId);
            Assert.Equal(frozenDefinition.ContentHash, secondRequest.RunRequest?.PipelineDefinitionSnapshot?.ContentHash);
            Assert.Equal("child-analysis", secondRequest.RunRequest?.PipelineName);
            Assert.False(string.IsNullOrWhiteSpace(secondRequest.RunRequest?.PipelineJson));
            Assert.Equal("MSFT", Assert.IsType<JsonElement>(
                Assert.IsAssignableFrom<IDictionary<string, object?>>(secondRequest.RunRequest?.Input)["ticker"]).GetString());
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "parent-context",
                Project = "tests",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = [],
                TtlSeconds = 300
            };
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

        private sealed class FixedRelationStore : IAiChildExecutionRelationStore
        {
            private readonly AiChildExecutionRelation relation;

            public FixedRelationStore(AiChildExecutionRelation relation)
            {
                this.relation = relation;
            }

            public Task<AiChildExecutionRelation?> GetAsync(
                AiChildInvocationIdentity identity,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiChildExecutionRelation?>(this.relation);
            }

            public Task<AiChildExecutionRelation?> GetByChildExecutionIdAsync(
                string childExecutionId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiChildExecutionRelation?>(
                    string.Equals(this.relation.ChildExecutionId, childExecutionId, StringComparison.Ordinal)
                        ? this.relation
                        : null);
            }

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListIncompleteAsync(
                int maxCount,
                CancellationToken cancellationToken = default,
                string? controlPlaneId = null) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListContinuationCandidatesAsync(
                int maxCount,
                CancellationToken cancellationToken = default,
                string? controlPlaneId = null) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListParkConsistencyCandidatesAsync(
                DateTimeOffset allocatedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default,
                string? controlPlaneId = null) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<AiChildExecutionRelation> GetOrCreateAsync(
                AiChildExecutionRelation relation,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> TryReplaceAsync(
                AiChildExecutionRelation relation,
                AiChildExecutionRelationStatus expectedStatus,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
            public Task<bool> TryReplaceContinuationAsync(
                AiChildExecutionRelation relation,
                AiChildContinuationStatus expectedContinuationStatus,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> TryCommitNextInvocationGenerationAsync(
                AiChildExecutionRelation relation,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class CapturingSharedRuntimeController : IAiSharedRuntimeController
        {
            public AiSharedRuntimeControllerRequest? LastRequest { get; private set; }

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                return SubmitRunAsync(request, cancellationToken);
            }

            public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                this.LastRequest = request;
                var now = DateTimeOffset.UtcNow;
                var run = new AiSharedRunRecord
                {
                    SharedRunId = request.RequestedSharedRunId!,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = request.RunRequest!,
                    ExecutionContextSnapshot = request.RunRequest!.ExecutionContextSnapshot!,
                    PipelineKey = request.PipelineKey,
                    SubmittedAtUtc = now,
                    UpdatedAtUtc = now,
                    Metadata = request.Metadata
                };

                return Task.FromResult(
                    new AiSharedRuntimeControllerResult
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        Success = true,
                        SharedRunId = run.SharedRunId,
                        Run = run,
                        StartedAtUtc = now,
                        CompletedAtUtc = now
                    });
            }

            public Task<AiSharedRuntimeControllerResult> GetRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
