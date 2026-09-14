using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Creation;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>
    /// Closes the published custom Child DAG branch against the existing DAG, hosted-worker and continuation contracts.
    /// The tests exercise an explicit two-level Child DAG nesting depth and real hosted execution for every supported language binding.
    /// </summary>
    [Trait("Category", "PublishedCustomChildDagClosure")]
    public sealed class AiPublishedCustomChildDagCompatibilityClosureTests
    {
        [Fact]
        public async Task Two_Level_Child_Nesting_Propagates_One_Immutable_Publication_Without_New_Authority()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = CreateTwoLevelNestedUpload();
            var publication = await fixture.PublishAsync(upload);
            var root = await fixture.CreateAsync(publication);
            var snapshots = CreateSnapshotService(fixture);

            var childDefinition = await ReadPublishedChildAsync(fixture, root, "invoke-child");
            var childSnapshot = await snapshots.FreezeDefinitionAsync(childDefinition, root.ExecutionId);
            var childRelation = await CreateRelationAsync(
                fixture,
                snapshots,
                root,
                childDefinition,
                childSnapshot,
                "invoke-child",
                "child-depth-1");

            Assert.True(await BindAsync(fixture, root, childRelation));
            var childRecord = await CreateExactChildAsync(fixture, childRelation, childDefinition, childSnapshot);

            var grandchildDefinition = await ReadPublishedChildAsync(fixture, childRecord, "invoke-grandchild");
            var grandchildSnapshot = await snapshots.FreezeDefinitionAsync(grandchildDefinition, childRecord.ExecutionId);
            var grandchildRelation = await CreateRelationAsync(
                fixture,
                snapshots,
                childRecord,
                grandchildDefinition,
                grandchildSnapshot,
                "invoke-grandchild",
                "child-depth-2");

            Assert.True(await BindAsync(fixture, childRecord, grandchildRelation));
            var grandchildRecord = await CreateExactChildAsync(
                fixture,
                grandchildRelation,
                grandchildDefinition,
                grandchildSnapshot);

            var target = await fixture.AsAsync(async () =>
            {
                var request = await fixture.Services
                    .GetRequiredService<AiDurableInvocationDagBinding>()
                    .ReadAsync(grandchildRecord, PublicationTestSupport.Scope, "leaf");
                return await fixture.Targets.ResolveAsync(request);
            });

            Assert.NotNull(target);
            Assert.Equal(publication.PublicationRef, target!.PublicationRef);
            Assert.Equal("dotnet", target.ExecutionLanguage);
            Assert.Equal(
                publication.Manifest.Functions.Single(function =>
                    function.Site.DefinitionPath == "/invoke-child/invoke-grandchild" &&
                    function.Site.StepName == "leaf").ImplementationRef,
                target.ImplementationRef);

            var bindings = fixture.MemoryPayloads.Documents
                .Where(document => document.Key.Contains("/child-run/", StringComparison.Ordinal))
                .Select(document => JsonDocument.Parse(document.Value))
                .ToArray();
            try
            {
                Assert.Equal(2, bindings.Length);
                Assert.Contains(bindings, binding =>
                    binding.RootElement.GetProperty("ExecutionId").GetString() == childRecord.ExecutionId &&
                    binding.RootElement.GetProperty("DefinitionPath").GetString() == "/invoke-child");
                Assert.Contains(bindings, binding =>
                    binding.RootElement.GetProperty("ExecutionId").GetString() == grandchildRecord.ExecutionId &&
                    binding.RootElement.GetProperty("DefinitionPath").GetString() == "/invoke-child/invoke-grandchild");
                Assert.All(bindings, binding =>
                    Assert.Equal(publication.PublicationRef, binding.RootElement.GetProperty("PublicationRef").GetString()));
            }
            finally
            {
                foreach (var binding in bindings)
                    binding.Dispose();
            }
        }

        [Fact]
        public async Task Native_Only_Child_Remains_Unbound_And_Executes_Through_The_Existing_Native_Path()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNativeOnlyChildUpload());
            var parent = await fixture.CreateAsync(publication);
            var snapshots = CreateSnapshotService(fixture);
            var childDefinition = await ReadPublishedChildAsync(fixture, parent, "invoke-child");
            var childSnapshot = await snapshots.FreezeDefinitionAsync(childDefinition, parent.ExecutionId);
            var relation = await CreateRelationAsync(
                fixture,
                snapshots,
                parent,
                childDefinition,
                childSnapshot,
                "invoke-child",
                "native-child-closure");

            Assert.False(await BindAsync(fixture, parent, relation));
            var child = await CreateExactChildAsync(fixture, relation, childDefinition, childSnapshot);

            await fixture.RunNextAsync(child.ExecutionId);
            var terminal = await fixture.RunNextAsync(child.ExecutionId);
            var state = await fixture.Store.GetStateAsync(child.ExecutionId);

            Assert.Equal(AiExecutionStatus.Completed, terminal.Status);
            Assert.NotNull(state);
            Assert.Equal(AiStepExecutionStatus.Completed, state!.Steps["native-only"].Status);
            Assert.True(fixture.Registry.Native.Calls > 0);
            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [PythonWorkerFact]
        [Trait("Category", "PythonProcess")]
        public async Task Mixed_Native_And_Python_Child_Executes_Through_Existing_Worker_And_Parent_Continuation()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            await ExecuteMixedHostedChildAsync(
                profile,
                PythonWorkerTestSupport.Upload(profile.Runtime),
                await PythonWorkerTestSupport.TransportAsync(),
                "python");
        }

        [TypeScriptWorkerFact]
        [Trait("Category", "TypeScriptProcess")]
        public async Task Mixed_Native_And_TypeScript_Child_Executes_Through_Existing_Worker_And_Parent_Continuation()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            await ExecuteMixedHostedChildAsync(
                profile,
                TypeScriptWorkerTestSupport.Upload(profile.Runtime),
                await TypeScriptWorkerTestSupport.TransportAsync(),
                "typescript");
        }

        [Fact]
        [Trait("Category", "DotNetProcess")]
        public async Task Mixed_Native_And_DotNet_Child_Executes_Through_Existing_Worker_And_Parent_Continuation()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            await ExecuteMixedHostedChildAsync(
                profile,
                DotNetWorkerTestSupport.Upload(profile.Runtime),
                await DotNetWorkerTestSupport.TransportAsync(),
                "dotnet");
        }

        private static async Task ExecuteMixedHostedChildAsync(
            AiWorkerProcessProfile profile,
            AiPipelinePublicationUpload workerUpload,
            AiWorkerProcessTransport transport,
            string language)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            fixture.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            fixture.Clock.Set(DateTimeOffset.UtcNow);

            var publication = await fixture.PublishAsync(CreateMixedHostedChildUpload(workerUpload, language));
            var parent = await fixture.CreateAsync(publication, key: "mixed-" + language);
            var snapshots = CreateSnapshotService(fixture);
            var childDefinition = await ReadPublishedChildAsync(fixture, parent, "invoke-child");
            var childSnapshot = await snapshots.FreezeDefinitionAsync(childDefinition, parent.ExecutionId);
            var relation = await CreateRelationAsync(
                fixture,
                snapshots,
                parent,
                childDefinition,
                childSnapshot,
                "invoke-child",
                "mixed-child-" + language);

            Assert.True(await BindAsync(fixture, parent, relation));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var child = await CreateExactChildAsync(fixture, relation, childDefinition, childSnapshot);

            var afterNative = await fixture.RunNextAsync(child.ExecutionId);
            var stateAfterNative = await fixture.Store.GetStateAsync(child.ExecutionId);
            Assert.NotNull(stateAfterNative);
            Assert.Equal(AiStepExecutionStatus.Completed, stateAfterNative!.Steps["native-before"].Status);
            Assert.False(afterNative.IsTerminal);

            var parked = await fixture.RunNextAsync(child.ExecutionId);
            Assert.Equal(AiExecutionStatus.Waiting, parked.Status);
            var identity = new AiDurableInvocationIdentity(
                PublicationTestSupport.Scope.TenantId,
                child.ExecutionId,
                "work");
            var prepared = await fixture.Journal.GetAsync(PublicationTestSupport.Scope, identity);
            Assert.NotNull(prepared);
            Assert.Equal(language, prepared!.Definition.Target.ExecutionLanguage);
            Assert.Equal(publication.PublicationRef, prepared.Definition.Target.PublicationRef);

            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = new AiWorkerInvocationSupervisor(
                fixture.Journal,
                CreateWorkerPreparer(fixture),
                transport,
                fixture.ControlPlane,
                capacity,
                options,
                NullLogger<AiWorkerInvocationSupervisor>.Instance,
                fixture.Clock);

            var dispatch = await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, dispatch.Disposition);
            var recorded = await fixture.Journal.GetAsync(PublicationTestSupport.Scope, identity);
            Assert.NotNull(recorded?.Result);
            Assert.True(recorded!.Result!.Success);

            await new AiDagExecutionEngine(
                    fixture.EngineServices,
                    DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>())
                .ResumeExternalWaitingStepAsync(child.ExecutionId, "work");
            var completedChild = await fixture.RunNextAsync(child.ExecutionId);
            Assert.Equal(AiExecutionStatus.Completed, completedChild.Status);

            var childState = await fixture.Store.GetStateAsync(child.ExecutionId);
            Assert.NotNull(childState);
            Assert.Equal(AiStepExecutionStatus.Completed, childState!.Steps["work"].Status);
            Assert.Equal(recorded.OperationId, childState.Steps["work"].Result!.InvocationReceipt!.OperationId);
            Assert.Equal(recorded.ResultSha256, childState.Steps["work"].Result!.InvocationReceipt!.ResultSha256);

            var completion = new AiChildExecutionCompletionCoordinator(
                relationStore,
                new TestAiDagExecutionEngineServices(fixture.Store),
                snapshots);
            var completedRelation = await completion.CompleteIfTerminalAsync(child.ExecutionId);
            Assert.NotNull(completedRelation);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, completedRelation!.Status);
            Assert.Equal(AiChildContinuationStatus.Pending, completedRelation.ContinuationStatus);

            var parentRecord = await fixture.Store.GetRecordAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent record is unavailable.");
            var parentState = await fixture.Store.GetStateAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent state is unavailable.");
            parentRecord.Status = AiExecutionStatus.Waiting;
            var parentStep = parentState.Steps["invoke-child"];
            parentStep.Status = AiStepExecutionStatus.WaitingForExternal;
            await fixture.Store.CreateAsync(parentRecord, parentState);

            var controller = new CapturingSharedRuntimeController();
            var continuation = new AiChildContinuationCoordinator(
                relationStore,
                fixture.ControlPlane,
                new TestAiDagExecutionEngineServices(fixture.Store, accessor: fixture.Accessor),
                new AiChildContinuationScheduler(controller, new InMemoryAiSharedQueue()));

            var scheduled = await continuation.EnqueueContinuationAsync(completedRelation.ToInvocationIdentity());
            Assert.Equal(AiChildContinuationStatus.Scheduled, scheduled.ContinuationStatus);
            Assert.Single(controller.Requests);

            parentRecord = await fixture.Store.GetRecordAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent record is unavailable after continuation scheduling.");
            parentState = await fixture.Store.GetStateAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent state is unavailable after continuation scheduling.");
            parentRecord.Status = AiExecutionStatus.Completed;
            parentStep = parentState.Steps["invoke-child"];
            parentStep.Status = AiStepExecutionStatus.Completed;
            parentStep.Version = (scheduled.ParentContinuationScheduledStepVersion ?? parentStep.Version) + 1;
            await fixture.Store.CreateAsync(parentRecord, parentState);

            var resumed = await continuation.ReconcileScheduledAsync(scheduled);
            Assert.Equal(AiChildContinuationStatus.Resumed, resumed.ContinuationStatus);
            Assert.Equal(child.ExecutionId, resumed.ChildExecutionId);
            Assert.NotNull(resumed.ChildResult);
        }

        private static AiPipelinePublicationUpload CreateMixedHostedChildUpload(
            AiPipelinePublicationUpload workerUpload,
            string language)
        {
            var template = workerUpload.Functions.First();
            var templateStep = PublicationTestSupport.Step("work");
            var custom = new AiPipelineStepDefinition
            {
                Name = "work",
                StepKey = "code-work",
                Order = 1,
                DependsOn = ["native-before"],
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom },
                Input = templateStep.Input,
                Config = templateStep.Config,
                Execution = templateStep.Execution
            };
            var child = new AiPipelineDefinition
            {
                Name = "published-child-" + language,
                Version = "child-v1",
                ExecutionLanguage = language,
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "native-before",
                        StepKey = "native",
                        Order = 0
                    },
                    custom
                ]
            };
            var root = new AiPipelineDefinition
            {
                Name = "published-parent-" + language,
                Version = "parent-v1",
                ExecutionLanguage = string.Equals(language, "python", StringComparison.Ordinal) ? "typescript" : "python",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = [ChildStep("invoke-child", child)]
            };
            var nestedFunction = template with
            {
                Site = new AiPublicationCallSite(AiPublicationFunctionKind.Step, "work")
                {
                    DefinitionPath = "/invoke-child"
                }
            };
            return new AiPipelinePublicationUpload(root, [nestedFunction]);
        }

        private static AiPipelinePublicationUpload CreateTwoLevelNestedUpload()
        {
            var grandchild = new AiPipelineDefinition
            {
                Name = "published-grandchild",
                Version = "grandchild-v1",
                ExecutionLanguage = "dotnet",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = [PublicationTestSupport.Step("leaf")]
            };
            var child = new AiPipelineDefinition
            {
                Name = "published-child",
                Version = "child-v1",
                ExecutionLanguage = "typescript",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition { Name = "native-child", StepKey = "native", Order = 0 },
                    ChildStep("invoke-grandchild", grandchild, order: 1, dependsOn: ["native-child"])
                ]
            };
            var root = new AiPipelineDefinition
            {
                Name = "published-root",
                Version = "root-v1",
                ExecutionLanguage = "python",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = [ChildStep("invoke-child", child)]
            };
            return new AiPipelinePublicationUpload(
                root,
                [
                    PublicationTestSupport.Function(
                        new AiPublicationCallSite(AiPublicationFunctionKind.Step, "leaf")
                        {
                            DefinitionPath = "/invoke-child/invoke-grandchild"
                        },
                        "dotnet",
                        "depth-2")
                ]);
        }

        private static AiPipelinePublicationUpload CreateNativeOnlyChildUpload()
        {
            var child = new AiPipelineDefinition
            {
                Name = "native-child",
                Version = "child-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "native-only",
                        StepKey = "native",
                        Order = 0
                    }
                ]
            };
            var root = new AiPipelineDefinition
            {
                Name = "native-parent",
                Version = "parent-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = [ChildStep("invoke-child", child)]
            };
            return new AiPipelinePublicationUpload(root, Array.Empty<AiPublicationFunctionUpload>());
        }

        private static AiPipelineStepDefinition ChildStep(
            string name,
            AiPipelineDefinition child,
            int order = 0,
            IReadOnlyCollection<string>? dependsOn = null) => new()
        {
            Name = name,
            StepKey = ExecuteChildDagStep.StepKey,
            Order = order,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            Config = new Dictionary<string, object?>
            {
                [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = name,
                [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child
            }
        };

        private static async Task<AiPipelineDefinition> ReadPublishedChildAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            string childStepName)
        {
            var snapshot = parent.PipelineDefinitionSnapshot
                ?? throw new InvalidOperationException("Published execution is missing its immutable definition snapshot.");
            var json = await fixture.Services
                .GetRequiredService<Multiplexed.AI.Runtime.Execution.Payloads.Immutable.AiImmutableJsonPayloadReader>()
                .LoadAndVerifyAsync(snapshot);
            using var document = JsonDocument.Parse(json);
            var childElement = document.RootElement
                .GetProperty("Steps")
                .EnumerateArray()
                .Single(step => step.GetProperty("Name").GetString() == childStepName)
                .GetProperty("Config")
                .GetProperty(ExecuteChildDagStep.ChildDagDefinitionConfigKey);
            return JsonSerializer.Deserialize<AiPipelineDefinition>(childElement.GetRawText())
                ?? throw new InvalidOperationException("Published child definition could not be deserialized.");
        }

        private static async Task<AiChildExecutionRelation> CreateRelationAsync(
            PublicationTestSupport.Fixture fixture,
            AiChildDagSnapshotService snapshots,
            AiExecutionRecord parent,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot,
            string parentCallSiteId,
            string childExecutionId)
        {
            var invocationInput = await snapshots.FreezeInvocationInputAsync(
                new Dictionary<string, object?> { ["amount"] = 1 },
                parent.ExecutionId);
            var delegationBinding = await snapshots.FreezeDelegationPolicyBindingAsync(
                new AiChildDelegationPolicyDefinition(),
                parent.ExecutionId);
            var owner = parent.ExecutionContextSnapshot
                ?? throw new InvalidOperationException("Published parent execution is missing its durable owner.");

            return new AiChildExecutionRelation
            {
                TenantId = owner.TenantId,
                ControlPlaneId = PublicationTestSupport.Scope.ControlPlaneId,
                ParentExecutionId = parent.ExecutionId,
                ParentCallSiteId = parentCallSiteId,
                ChildDagId = child.Name,
                ChildDagDefinitionVersion = child.Version!,
                FrozenChildDagDefinition = childSnapshot,
                CanonicalLogicalInvocationKey = "published-child:" + parentCallSiteId,
                ChildInvocationKey = "published-child:" + parent.ExecutionId + ":" + parentCallSiteId,
                InvocationGeneration = 0,
                FrozenInvocationInput = invocationInput,
                DelegatedExecutionContextSnapshot = owner,
                DelegatedMetadata = new Dictionary<string, string>(StringComparer.Ordinal),
                DelegationPolicyBindingSnapshot = delegationBinding,
                Status = AiChildExecutionRelationStatus.Waiting,
                ChildExecutionId = childExecutionId,
                ContinuationStatus = AiChildContinuationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ChildAllocatedAtUtc = DateTimeOffset.UtcNow,
                WaitingAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static Task<bool> BindAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            AiChildExecutionRelation relation) => fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation));

        private static Task<AiExecutionRecord> CreateExactChildAsync(
            PublicationTestSupport.Fixture fixture,
            AiChildExecutionRelation relation,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot) => fixture.AsAsync(() => new AiDagExecutionCreator(fixture.EngineServices)
                .CreateIfAbsentAsync(
                    relation.ChildExecutionId!,
                    child,
                    childSnapshot,
                    new Dictionary<string, object?> { ["amount"] = 1 }));

        private static AiWorkerPublicationPreparer CreateWorkerPreparer(PublicationTestSupport.Fixture fixture) => new(
            fixture.Store,
            fixture.Accessor,
            fixture.ControlPlane,
            fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>(),
            fixture.Services.GetRequiredService<AiPublicationIdentity>(),
            PublicationTestSupport.Options,
            fixture.Services.GetRequiredService<AiImmutablePublicationStore>());

        private static AiChildDagSnapshotService CreateSnapshotService(PublicationTestSupport.Fixture fixture) => new(
            fixture.Payloads,
            Microsoft.Extensions.Options.Options.Create(
                new Multiplexed.Abstractions.AI.Execution.Payloads.Stores.AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "inmemory",
                    RequireReplaySafePayloads = false,
                    MaxInlineSizeBytes = 1024 * 1024
                }));
    }
}
