using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>
    /// Verifies that an allocated published Child DAG remains bound to its parent's immutable publication
    /// while the existing durable invocation and worker materialization paths retain execution authority.
    /// </summary>
    public sealed class AiPublishedCustomChildDagExecutionBindingTests
    {
        [Fact]
        public async Task Allocated_Child_Is_Bound_To_The_Exact_Parent_Publication()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("1"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-execution-a");

            var bound = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation));

            Assert.True(bound);
            var persisted = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));
            using var bindingJson = System.Text.Json.JsonDocument.Parse(persisted.Value);
            var root = bindingJson.RootElement;
            Assert.Equal(parent.ExecutionId, root.GetProperty("ParentExecutionId").GetString());
            Assert.Equal(publication.PublicationRef, root.GetProperty("PublicationRef").GetString());
            Assert.Equal(publication.PublicationSha256, root.GetProperty("PublicationSha256").GetString());
            Assert.Equal("/invoke-child", root.GetProperty("DefinitionPath").GetString());
            Assert.Equal(childSnapshot.ContentHash, root.GetProperty("DefinitionSha256").GetString());
            Assert.Equal(childDefinition.Name, root.GetProperty("PipelineName").GetString());
            Assert.Equal(childDefinition.Version, root.GetProperty("PipelineVersion").GetString());
        }

        [Fact]
        public async Task ExecuteChildDag_Binds_Published_Child_Before_Existing_Dispatch()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("dispatch"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, _) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var controller = new CapturingSharedRuntimeController();
            var snapshotService = CreateSnapshotService(fixture);
            var step = new ExecuteChildDagStep(
                relationStore,
                fixture.ControlPlane,
                new ThrowingPipelineDefinitionSourceSelector(),
                snapshotService,
                new AiChildDelegationPolicyCoordinator(relationStore, new AllowAllPolicyEngineFactory(), snapshotService),
                new AiChildExecutionAllocator(relationStore, snapshotService),
                new AiChildExecutionDispatcher(relationStore, snapshotService, controller),
                new AiChildExecutionWaitingCoordinator(relationStore),
                new AiChildInvocationGenerationCoordinator(relationStore));
            var state = new AiExecutionState
            {
                ExecutionId = parent.ExecutionId,
                PipelineName = parent.PipelineName!,
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
            var resolvedStep = new ResolvedAiPipelineStep
            {
                Name = "invoke-child",
                StepKey = ExecuteChildDagStep.StepKey,
                Step = step,
                Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ExecuteChildDagStep.ChildDagIdConfigKey] = childDefinition.Name,
                    [ExecuteChildDagStep.ChildDagVersionConfigKey] = childDefinition.Version,
                    [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = "published-child",
                    [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = childDefinition
                },
                Input = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
            var context = new AiStepExecutionContext(
                fixture.Build(parent, state, CancellationToken.None),
                resolvedStep);

            var result = await fixture.AsAsync(() => step.ExecuteAsync(context));

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            var request = Assert.Single(controller.Requests);
            Assert.NotNull(request.RunRequest);
            Assert.False(string.IsNullOrWhiteSpace(request.RunRequest!.RequestedExecutionId));
            var persisted = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));
            using var bindingJson = System.Text.Json.JsonDocument.Parse(persisted.Value);
            Assert.Equal(request.RunRequest.RequestedExecutionId,
                bindingJson.RootElement.GetProperty("ExecutionId").GetString());
            Assert.Equal(parent.ExecutionId,
                bindingJson.RootElement.GetProperty("ParentExecutionId").GetString());
            Assert.Equal("/invoke-child",
                bindingJson.RootElement.GetProperty("DefinitionPath").GetString());
        }

        [Fact]
        public async Task Nested_Target_Resolver_Uses_The_Child_Definition_Path()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("1", duplicateParentStepName: true));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-execution-b");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);

            var target = await fixture.AsAsync(async () =>
            {
                var request = await fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>()
                    .ReadAsync(childRecord, PublicationTestSupport.Scope, "work");
                return await fixture.Targets.ResolveAsync(request);
            });

            Assert.NotNull(target);
            var expected = publication.Manifest.Functions.Single(function =>
                function.Site.Kind == AiPublicationFunctionKind.Step &&
                function.Site.DefinitionPath == "/invoke-child" &&
                function.Site.StepName == "work");
            Assert.Equal(publication.PublicationRef, target!.PublicationRef);
            Assert.Equal(childSnapshot.ContentHash, target.DefinitionSha256);
            Assert.Equal(expected.ImplementationRef, target.ImplementationRef);
            Assert.Equal(expected.Implementation.Sha256, target.ImplementationSha256);
            Assert.Equal("python", target.ExecutionLanguage);
        }

        [Fact]
        public async Task Worker_Materialization_Uses_The_Child_Binding_And_Nested_Code()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("worker-original"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-execution-c");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            await fixture.Store.CreateAsync(
                childRecord,
                new AiExecutionState { ExecutionId = childRecord.ExecutionId, PipelineName = childRecord.PipelineName! });

            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var target = await fixture.AsAsync(() => fixture.Targets.ResolveAsync(request));
            Assert.NotNull(target);
            var invocation = await fixture.Journal.PrepareAsync(
                new AiDurableInvocationDefinition(
                    new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, childRecord.ExecutionId, "work"),
                    PublicationTestSupport.Scope,
                    target!,
                    "{}"));

            var preparer = new AiWorkerPublicationPreparer(
                fixture.Store,
                fixture.Accessor,
                fixture.ControlPlane,
                fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>(),
                fixture.Services.GetRequiredService<AiPublicationIdentity>(),
                PublicationTestSupport.Options,
                fixture.Services.GetRequiredService<AiImmutablePublicationStore>());
            var bundle = await preparer.PrepareAsync(invocation);

            Assert.Equal(target, bundle.Target);
            var source = Assert.Single(bundle.Sources);
            var bytes = DecodeBase64Url(source.Base64Url);
            Assert.Equal("code-worker-original", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public async Task Republishing_Does_Not_Change_An_Existing_Child_Binding()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var original = await fixture.PublishAsync(CreateNestedUpload("1"));
            var parent = await fixture.CreateAsync(original);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-execution-d");
            await BindAsync(fixture, parent, relation);
            var replacement = await fixture.PublishAsync(CreateNestedUpload("2"));
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);

            var target = await fixture.AsAsync(async () =>
            {
                var request = await fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>()
                    .ReadAsync(childRecord, PublicationTestSupport.Scope, "work");
                return await fixture.Targets.ResolveAsync(request);
            });

            Assert.NotNull(target);
            Assert.Equal(original.PublicationRef, target!.PublicationRef);
            Assert.NotEqual(replacement.PublicationRef, target.PublicationRef);
            Assert.Equal(
                original.Manifest.Functions.Single(function => function.Site.DefinitionPath == "/invoke-child").Implementation.Sha256,
                target.ImplementationSha256);
        }

        [Fact]
        public async Task Native_Only_Child_Does_Not_Create_A_Publication_Binding()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNativeChildUpload());
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-execution-native");

            var bound = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation));

            Assert.False(bound);
            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Unpublished_Child_Does_Not_Require_Publication_Execute_Authorization()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNativeChildUpload());
            var publishedParent = await fixture.CreateAsync(publication);
            var parent = CloneWithExecutionId(publishedParent, "unpublished-parent");
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, publishedParent, "/invoke-child");
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "unpublished-child");
            var readOnlyIdentity = PublicationTestSupport.Identity(actions: ["read"]);

            var bound = await fixture.AsAsync(
                () => fixture.Services
                    .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                    .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation),
                readOnlyIdentity);

            Assert.False(bound);
            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Binding_Rejects_A_Frozen_Child_Definition_Different_From_The_Publication()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("1"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var conflicting = AiStoredPayload.Inline(
                childSnapshot.InlineValue,
                childSnapshot.SizeBytes,
                childSnapshot.ContentType,
                new string('f', 64));
            var relation = CreateRelation(parent, childDefinition, conflicting, "child-execution-conflict");

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation)));

            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Binding_Rejects_A_Delegated_Owner_Different_From_The_Parent()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("1"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent, "/invoke-child");
            var relation = CloneWithDelegatedTenant(
                CreateRelation(parent, childDefinition, childSnapshot, "child-execution-owner"),
                "tenant-b");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation)));
        }

        private static Task<bool> BindAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            AiChildExecutionRelation relation) => fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation));

        private static async Task<(AiPipelineDefinition Definition, AiStoredPayload Snapshot)> ReadChildAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            string path)
        {
            Assert.Equal("/invoke-child", path);
            var json = await fixture.Services
                .GetRequiredService<Multiplexed.AI.Runtime.Execution.Payloads.Immutable.AiImmutableJsonPayloadReader>()
                .LoadAndVerifyAsync(parent.PipelineDefinitionSnapshot!);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var childElement = document.RootElement
                .GetProperty("Steps")
                .EnumerateArray()
                .Single(step => step.GetProperty("Name").GetString() == "invoke-child")
                .GetProperty("Config")
                .GetProperty(ExecuteChildDagStep.ChildDagDefinitionConfigKey);
            var child = System.Text.Json.JsonSerializer.Deserialize<AiPipelineDefinition>(childElement.GetRawText())
                ?? throw new InvalidOperationException("Published child definition could not be deserialized.");
            var snapshot = await CreateSnapshotService(fixture).FreezeDefinitionAsync(child, parent.ExecutionId);
            return (child, snapshot);
        }

        private static AiChildExecutionRelation CreateRelation(
            AiExecutionRecord parent,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot,
            string childExecutionId) => new()
        {
            TenantId = parent.ExecutionContextSnapshot!.TenantId,
            ControlPlaneId = PublicationTestSupport.Scope.ControlPlaneId,
            ParentExecutionId = parent.ExecutionId,
            ParentCallSiteId = "invoke-child",
            ChildDagId = child.Name,
            ChildDagDefinitionVersion = child.Version!,
            FrozenChildDagDefinition = childSnapshot,
            CanonicalLogicalInvocationKey = "published-child",
            ChildInvocationKey = "published-child-key",
            InvocationGeneration = 0,
            FrozenInvocationInput = childSnapshot,
            DelegatedExecutionContextSnapshot = parent.ExecutionContextSnapshot,
            DelegatedMetadata = new Dictionary<string, string>(StringComparer.Ordinal),
            DelegationPolicyBindingSnapshot = childSnapshot,
            Status = AiChildExecutionRelationStatus.ChildAllocated,
            ChildExecutionId = childExecutionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ChildAllocatedAtUtc = DateTimeOffset.UtcNow
        };

        private static AiExecutionRecord CloneWithExecutionId(AiExecutionRecord source, string executionId) => new()
        {
            ExecutionId = executionId,
            PipelineName = source.PipelineName,
            PipelineDefinitionSnapshot = source.PipelineDefinitionSnapshot,
            ExecutionMode = source.ExecutionMode,
            ContextKey = source.ContextKey,
            CurrentStepIndex = source.CurrentStepIndex,
            Steps = source.Steps.ToList(),
            CompletedSteps = source.CompletedSteps.ToList(),
            ExecutionContextSnapshot = source.ExecutionContextSnapshot,
            Status = source.Status,
            Version = source.Version,
            CurrentStep = source.CurrentStep,
            ExecutionStepKey = source.ExecutionStepKey,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc
        };

        private static AiChildExecutionRelation CloneWithDelegatedTenant(AiChildExecutionRelation source, string tenantId)
        {
            var owner = source.DelegatedExecutionContextSnapshot!;
            return new AiChildExecutionRelation
            {
                TenantId = source.TenantId,
                ControlPlaneId = source.ControlPlaneId,
                ParentExecutionId = source.ParentExecutionId,
                ParentCallSiteId = source.ParentCallSiteId,
                ChildDagId = source.ChildDagId,
                ChildDagDefinitionVersion = source.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = source.FrozenChildDagDefinition,
                CanonicalLogicalInvocationKey = source.CanonicalLogicalInvocationKey,
                ChildInvocationKey = source.ChildInvocationKey,
                InvocationGeneration = source.InvocationGeneration,
                FrozenInvocationInput = source.FrozenInvocationInput,
                DelegatedExecutionContextSnapshot = new Multiplexed.Abstractions.Core.ExecutionContext.ExecutionContextSnapshot
                {
                    ContextKey = owner.ContextKey,
                    Project = owner.Project,
                    UserId = owner.UserId,
                    TenantId = tenantId,
                    TenantGroupId = owner.TenantGroupId,
                    CurrentNamespace = owner.CurrentNamespace,
                    Namespaces = owner.Namespaces,
                    TtlSeconds = owner.TtlSeconds
                },
                DelegatedMetadata = source.DelegatedMetadata,
                DelegationPolicyBindingSnapshot = source.DelegationPolicyBindingSnapshot,
                Status = source.Status,
                ChildExecutionId = source.ChildExecutionId,
                CreatedAtUtc = source.CreatedAtUtc,
                ChildAllocatedAtUtc = source.ChildAllocatedAtUtc
            };
        }

        private static AiExecutionRecord CreateChildRecord(
            AiExecutionRecord parent,
            AiChildExecutionRelation relation,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot) => new()
        {
            ExecutionId = relation.ChildExecutionId!,
            PipelineName = child.Name,
            PipelineDefinitionSnapshot = childSnapshot,
            ExecutionMode = AiExecutionMode.Dag,
            Status = AiExecutionStatus.Running,
            ContextKey = parent.ContextKey,
            ExecutionContextSnapshot = parent.ExecutionContextSnapshot,
            Steps = child.Steps.OrderBy(step => step.Order).Select(step => step.Name).ToList()
        };


        private static AiPipelinePublicationUpload CreateNestedUpload(string revision, bool duplicateParentStepName = false)
        {
            var child = new AiPipelineDefinition
            {
                Name = "published-child",
                Version = "child-v1",
                ExecutionLanguage = "python",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    PublicationTestSupport.Step("work", 0, language: null)
                ]
            };
            var steps = new List<AiPipelineStepDefinition>();
            if (duplicateParentStepName)
            {
                steps.Add(new AiPipelineStepDefinition
                {
                    Name = "work",
                    StepKey = "native",
                    Order = 0
                });
            }
            steps.Add(new AiPipelineStepDefinition
            {
                Name = "invoke-child",
                StepKey = ExecuteChildDagStep.StepKey,
                Order = steps.Count,
                Config = new Dictionary<string, object?>
                {
                    [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                    [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                    [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = "published-child",
                    [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child
                }
            });
            var root = new AiPipelineDefinition
            {
                Name = "published-parent",
                Version = "parent-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };
            return new AiPipelinePublicationUpload(
                root,
                [
                    PublicationTestSupport.Function(
                        new AiPublicationCallSite(AiPublicationFunctionKind.Step, "work")
                        {
                            DefinitionPath = "/invoke-child"
                        },
                        "python",
                        revision)
                ]);
        }

        private static AiPipelinePublicationUpload CreateNativeChildUpload()
        {
            var child = new AiPipelineDefinition
            {
                Name = "published-child",
                Version = "child-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition { Name = "native-work", StepKey = "native", Order = 0 }
                ]
            };
            var root = new AiPipelineDefinition
            {
                Name = "published-parent",
                Version = "parent-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "invoke-child",
                        StepKey = ExecuteChildDagStep.StepKey,
                        Order = 0,
                        Config = new Dictionary<string, object?>
                        {
                            [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                            [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                            [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = "published-child",
                            [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child
                        }
                    }
                ]
            };
            return new AiPipelinePublicationUpload(root, Array.Empty<AiPublicationFunctionUpload>());
        }

        private static AiChildDagSnapshotService CreateSnapshotService(PublicationTestSupport.Fixture fixture) => new(
            fixture.Payloads,
            Microsoft.Extensions.Options.Options.Create(new Multiplexed.Abstractions.AI.Execution.Payloads.Stores.AiPayloadStoreOptions
            {
                Enabled = true,
                Provider = "inmemory",
                RequireReplaySafePayloads = false,
                MaxInlineSizeBytes = 1024 * 1024
            }));

        private sealed class ThrowingPipelineDefinitionSourceSelector : IAiPipelineDefinitionSourceSelector
        {
            public IAiPipelineDefinitionProvider Select(string pipelineName) =>
                throw new InvalidOperationException("Published Child DAG execution must use its exact inline definition.");
        }

        private sealed class AllowAllPolicyEngineFactory : IAiPolicyEngineFactory
        {
            public IAiPolicyEngine Create(AiPolicyKind kind, AiStepExecutionContext stepContext) =>
                new AllowAllChildDelegationPolicyEngine(stepContext);

            public TPolicyEngine Create<TPolicyEngine>(AiPolicyKind kind, AiStepExecutionContext stepContext)
                where TPolicyEngine : class, IAiPolicyEngine =>
                (TPolicyEngine)(IAiPolicyEngine)new AllowAllChildDelegationPolicyEngine(stepContext);
        }

        private sealed class AllowAllChildDelegationPolicyEngine : IAiChildDelegationPolicyEngine
        {
            public AllowAllChildDelegationPolicyEngine(AiStepExecutionContext stepContext) => StepContext = stepContext;

            public AiPolicyKind Kind => AiPolicyKind.Delegation;
            public AiStepExecutionContext StepContext { get; }

            public Task<Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation.AiChildDelegationPolicyDefinition>
                ResolveDefinitionAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    new Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation.AiChildDelegationPolicyDefinition());
            }

            public Task<IReadOnlyCollection<AiPolicyResult>> EvaluateAsync(
                AiChildExecutionRelation relation,
                Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation.AiChildDelegationPolicyDefinition definition,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyCollection<AiPolicyResult>>(Array.Empty<AiPolicyResult>());
            }
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
            return Convert.FromBase64String(padded);
        }
    }
}
