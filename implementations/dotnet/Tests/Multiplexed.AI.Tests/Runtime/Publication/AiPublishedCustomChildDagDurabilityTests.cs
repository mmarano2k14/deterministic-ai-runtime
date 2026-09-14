using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
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
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
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
    /// Verifies recovery, immutable pinning and duplicate-safety properties for published custom Child DAG execution.
    /// Existing DAG, journal, worker and continuation authorities remain unchanged by these tests.
    /// </summary>
    public sealed class AiPublishedCustomChildDagDurabilityTests
    {
        [Fact]
        public async Task Rehydrated_Child_Keeps_Original_Publication_And_Worker_Code_After_Republish()
        {
            using var first = new PublicationTestSupport.Fixture();
            var original = await first.PublishAsync(CreateNestedUpload("original"));
            var parent = await first.CreateAsync(original);
            var (childDefinition, childSnapshot) = await ReadChildAsync(first, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-rehydrate");
            await BindAsync(first, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var childState = new AiExecutionState
            {
                ExecutionId = childRecord.ExecutionId,
                PipelineName = childRecord.PipelineName!
            };
            await first.Store.CreateAsync(childRecord, childState);

            var request = await first.AsAsync(() => first.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var target = await first.AsAsync(() => first.Targets.ResolveAsync(request));
            Assert.NotNull(target);
            var identity = new AiDurableInvocationIdentity(
                PublicationTestSupport.Scope.TenantId,
                childRecord.ExecutionId,
                "work");
            var prepared = await first.Journal.PrepareAsync(
                new AiDurableInvocationDefinition(identity, PublicationTestSupport.Scope, target!, "{}"));
            var operationId = prepared.OperationId;

            var replacement = await first.PublishAsync(CreateNestedUpload("replacement"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            var payloadsJson = first.MemoryPayloads.Export();
            var journalJson = first.JournalStore.Export();
            var recordJson = JsonSerializer.Serialize(childRecord);
            var stateJson = JsonSerializer.Serialize(childState);

            using var restored = new PublicationTestSupport.Fixture();
            restored.MemoryPayloads.Restore(payloadsJson);
            restored.JournalStore.Restore(journalJson);
            await restored.Store.CreateAsync(
                JsonSerializer.Deserialize<AiExecutionRecord>(recordJson)!,
                JsonSerializer.Deserialize<AiExecutionState>(stateJson)!);

            var restoredInvocation = await restored.Journal.GetAsync(PublicationTestSupport.Scope, identity);
            Assert.NotNull(restoredInvocation);
            Assert.Equal(operationId, restoredInvocation!.OperationId);
            var preparer = CreateWorkerPreparer(restored);
            var bundle = await preparer.PrepareAsync(restoredInvocation);

            Assert.Equal(original.PublicationRef, bundle.Target.PublicationRef);
            Assert.NotEqual(replacement.PublicationRef, bundle.Target.PublicationRef);
            var source = Assert.Single(bundle.Sources);
            Assert.Equal("code-original", Encoding.UTF8.GetString(DecodeBase64Url(source.Base64Url)));
        }

        [Fact]
        public async Task Lost_Child_Binding_Write_Acknowledgement_Is_Retryable_Without_Rebinding()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("binding-ack"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-binding-ack");
            var inject = 1;
            fixture.MemoryPayloads.AfterWrite = (key, _) =>
            {
                if (key.Contains("/child-run/", StringComparison.Ordinal) && Interlocked.Exchange(ref inject, 0) == 1)
                    return Task.FromException(new IOException("Injected lost child binding acknowledgement."));
                return Task.CompletedTask;
            };

            await Assert.ThrowsAsync<IOException>(() => BindAsync(fixture, parent, relation));
            var persisted = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));
            fixture.MemoryPayloads.AfterWrite = null;

            Assert.True(await BindAsync(fixture, parent, relation));
            var afterRetry = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));
            Assert.Equal(persisted.Key, afterRetry.Key);
            Assert.Equal(persisted.Value, afterRetry.Value);
        }

        [Fact]
        public async Task Missing_Child_Binding_After_Restoration_Fails_Without_Latest_Resolution()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var original = await fixture.PublishAsync(CreateNestedUpload("missing-binding"));
            var parent = await fixture.CreateAsync(original);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-missing-binding");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var bindingKey = fixture.MemoryPayloads.Documents.Keys.Single(key =>
                key.Contains("/child-run/", StringComparison.Ordinal));
            fixture.MemoryPayloads.Documents.TryRemove(bindingKey, out _);
            _ = await fixture.PublishAsync(CreateNestedUpload("newer"));

            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => fixture.Targets.ResolveAsync(request)));

            Assert.Contains("No immutable publication binding", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.LatestLookups);
        }

        [Fact]
        public async Task Missing_Published_Implementation_Artifact_Is_Rejected_Explicitly()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("missing-artifact"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-missing-artifact");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var function = publication.Manifest.Functions.Single(item => item.Site.DefinitionPath == "/invoke-child");
            fixture.MemoryPayloads.Documents.TryRemove(function.Implementation.Key, out _);

            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => fixture.Targets.ResolveAsync(request)));

            Assert.Contains("unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Changed_Published_Implementation_Artifact_Is_Rejected_By_Digest_Verification()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("changed-artifact"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-changed-artifact");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var function = publication.Manifest.Functions.Single(item => item.Site.DefinitionPath == "/invoke-child");
            fixture.MemoryPayloads.Documents[function.Implementation.Key] += " ";

            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => fixture.Targets.ResolveAsync(request)));

            Assert.Contains("does not match its immutable digest", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Binding_Rejects_Corrupted_Frozen_Child_Content_Even_When_Descriptor_Hash_Is_Unchanged()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("child-corruption"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var corrupted = AiStoredPayload.Inline(
                "{\"corrupted\":true}",
                childSnapshot.SizeBytes,
                childSnapshot.ContentType,
                childSnapshot.ContentHash);
            var relation = CreateRelation(parent, childDefinition, corrupted, "child-corrupt-snapshot");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BindAsync(fixture, parent, relation));

            Assert.Contains("does not match its durable content hash", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Binding_Rejects_Corrupted_Parent_Snapshot_Content_Before_Child_Dispatch()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("parent-corruption"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var parentSnapshot = parent.PipelineDefinitionSnapshot!;
            var corruptedParent = CloneWithSnapshot(
                parent,
                AiStoredPayload.Inline(
                    "{\"corrupted\":true}",
                    parentSnapshot.SizeBytes,
                    parentSnapshot.ContentType,
                    parentSnapshot.ContentHash));
            var relation = CreateRelation(corruptedParent, childDefinition, childSnapshot, "child-corrupt-parent");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BindAsync(fixture, corruptedParent, relation));

            Assert.Contains("does not match its durable content hash", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                fixture.MemoryPayloads.Documents.Keys,
                key => key.Contains("/child-run/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Foreign_Tenant_Cannot_Resolve_A_Restored_Child_Publication_Binding()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("tenant"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-tenant");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var foreign = PublicationTestSupport.Identity(tenant: "tenant-b");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.AsAsync(() => fixture.Targets.ResolveAsync(request), foreign));
        }

        [Fact]
        public async Task Stale_Worker_Result_Is_Rejected_And_Accepted_Assignment_Remains_Idempotent()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("lease"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(parent, childDefinition, childSnapshot, "child-lease");
            await BindAsync(fixture, parent, relation);
            var childRecord = CreateChildRecord(parent, relation, childDefinition, childSnapshot);
            var request = await fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(childRecord, PublicationTestSupport.Scope, "work"));
            var target = await fixture.AsAsync(() => fixture.Targets.ResolveAsync(request));
            Assert.NotNull(target);
            var identity = new AiDurableInvocationIdentity(
                PublicationTestSupport.Scope.TenantId,
                childRecord.ExecutionId,
                "work");
            var prepared = await fixture.Journal.PrepareAsync(
                new AiDurableInvocationDefinition(identity, PublicationTestSupport.Scope, target!, "{}"));
            var firstLease = await fixture.Journal.TryAcquireLeaseAsync(
                PublicationTestSupport.Scope,
                identity,
                "worker-a",
                TimeSpan.FromSeconds(30));
            Assert.NotNull(firstLease?.Lease);
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            var secondLease = await fixture.Journal.TryAcquireLeaseAsync(
                PublicationTestSupport.Scope,
                identity,
                "worker-b",
                TimeSpan.FromSeconds(30));
            Assert.NotNull(secondLease?.Lease);
            Assert.True(secondLease!.Lease!.Epoch > firstLease!.Lease!.Epoch);
            var result = new AiDurableInvocationResult(true, "{\"value\":42}");

            Assert.Equal(
                AiDurableInvocationCompletionStatus.LeaseRejected,
                await fixture.Journal.CompleteAsync(
                    PublicationTestSupport.Scope,
                    identity,
                    firstLease.Lease,
                    result));
            Assert.Equal(
                AiDurableInvocationCompletionStatus.Accepted,
                await fixture.Journal.CompleteAsync(
                    PublicationTestSupport.Scope,
                    identity,
                    secondLease.Lease,
                    result));
            Assert.Equal(
                AiDurableInvocationCompletionStatus.AlreadyAccepted,
                await fixture.Journal.CompleteAsync(
                    PublicationTestSupport.Scope,
                    identity,
                    secondLease.Lease,
                    result));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Journal.CompleteAsync(
                PublicationTestSupport.Scope,
                identity,
                secondLease.Lease,
                new AiDurableInvocationResult(true, "{\"value\":99}")));

            var accepted = await fixture.Journal.GetAsync(PublicationTestSupport.Scope, identity);
            Assert.NotNull(accepted);
            Assert.Equal(prepared.OperationId, accepted!.OperationId);
            Assert.Equal(secondLease.Lease.Epoch, accepted.Lease!.Epoch);
            Assert.Equal(result, accepted.Result);
        }

        [Fact]
        public async Task Dispatch_Redrive_Reuses_The_Same_Child_Execution_And_Immutable_Binding()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("redrive"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, _) = await ReadChildAsync(fixture, parent);
            var relationStore = new InMemoryAiChildExecutionRelationStore();
            var capture = new CapturingSharedRuntimeController();
            var snapshotService = CreateSnapshotService(fixture);
            var crashingStep = CreateStep(
                fixture,
                relationStore,
                snapshotService,
                new ThrowAfterAcceptedSubmissionController(capture));

            await Assert.ThrowsAsync<IOException>(() => fixture.AsAsync(() => crashingStep.ExecuteAsync(
                CreateContext(fixture, parent, childDefinition, crashingStep))));

            var relationAfterCrash = Assert.Single(await relationStore.ListIncompleteAsync(10));
            Assert.Equal(AiChildExecutionRelationStatus.ChildAllocated, relationAfterCrash.Status);
            Assert.False(string.IsNullOrWhiteSpace(relationAfterCrash.ChildExecutionId));
            var childExecutionId = relationAfterCrash.ChildExecutionId;
            var binding = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));

            var recoveredStep = CreateStep(fixture, relationStore, snapshotService, capture);
            var recovered = await fixture.AsAsync(() => recoveredStep.ExecuteAsync(
                CreateContext(fixture, parent, childDefinition, recoveredStep)));

            Assert.Equal(AiStepExecutionOutcome.Park, recovered.EffectiveOutcome);
            var relation = Assert.Single(await relationStore.ListIncompleteAsync(10));
            Assert.Equal(AiChildExecutionRelationStatus.Waiting, relation.Status);
            Assert.Equal(childExecutionId, relation.ChildExecutionId);
            Assert.Equal(2, capture.Requests.Count);
            Assert.All(capture.Requests, request =>
                Assert.Equal(childExecutionId, request.RunRequest!.RequestedExecutionId));
            var bindingAfterRedrive = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal));
            Assert.Equal(binding.Key, bindingAfterRedrive.Key);
            Assert.Equal(binding.Value, bindingAfterRedrive.Value);
        }

        [Fact]
        public async Task Duplicate_Terminal_Child_Projection_Keeps_One_Authoritative_Result()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("completion"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(
                parent,
                childDefinition,
                childSnapshot,
                "child-completion",
                AiChildExecutionRelationStatus.Waiting);
            await BindAsync(fixture, parent, relation);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var childRecord = CreateChildRecord(
                parent,
                relation,
                childDefinition,
                childSnapshot,
                AiExecutionStatus.Completed);
            var childState = new AiExecutionState
            {
                ExecutionId = childRecord.ExecutionId,
                PipelineName = childRecord.PipelineName!,
                Data = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["result"] = "published-child-result"
                }
            };
            await fixture.Store.CreateAsync(childRecord, childState);
            var coordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                new TestAiDagExecutionEngineServices(fixture.Store),
                CreateSnapshotService(fixture));

            var first = await coordinator.CompleteIfTerminalAsync(childRecord.ExecutionId);
            var duplicate = await coordinator.CompleteIfTerminalAsync(childRecord.ExecutionId);

            Assert.NotNull(first);
            Assert.NotNull(duplicate);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, first!.Status);
            Assert.Equal(AiChildContinuationStatus.Pending, first.ContinuationStatus);
            Assert.Equal(first.ChildResult!.ContentHash, duplicate!.ChildResult!.ContentHash);
            Assert.Equal(first.CompletedAtUtc, duplicate.CompletedAtUtc);
            Assert.Single(fixture.MemoryPayloads.Documents.Where(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Duplicate_Parent_Continuation_Redrive_Uses_One_Stable_Continuation_Identity()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(CreateNestedUpload("continuation"));
            var parent = await fixture.CreateAsync(publication);
            var (childDefinition, childSnapshot) = await ReadChildAsync(fixture, parent);
            var relation = CreateRelation(
                parent,
                childDefinition,
                childSnapshot,
                "child-continuation",
                AiChildExecutionRelationStatus.Waiting);
            await BindAsync(fixture, parent, relation);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var childRecord = CreateChildRecord(
                parent,
                relation,
                childDefinition,
                childSnapshot,
                AiExecutionStatus.Completed);
            var childState = new AiExecutionState
            {
                ExecutionId = childRecord.ExecutionId,
                PipelineName = childRecord.PipelineName!,
                Data = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["result"] = "continuation-result"
                }
            };
            await fixture.Store.CreateAsync(childRecord, childState);
            var completion = new AiChildExecutionCompletionCoordinator(
                relationStore,
                new TestAiDagExecutionEngineServices(fixture.Store),
                CreateSnapshotService(fixture));
            var completed = await completion.CompleteIfTerminalAsync(childRecord.ExecutionId);
            Assert.NotNull(completed);

            var parentRecord = await fixture.Store.GetRecordAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent record is unavailable.");
            var parentState = await fixture.Store.GetStateAsync(parent.ExecutionId)
                ?? throw new InvalidOperationException("Published parent state is unavailable.");
            parentRecord.Status = AiExecutionStatus.Waiting;
            parentState.Steps["invoke-child"].Status = AiStepExecutionStatus.WaitingForExternal;
            await fixture.Store.CreateAsync(parentRecord, parentState);

            var controller = new CapturingSharedRuntimeController();
            var continuation = new AiChildContinuationCoordinator(
                relationStore,
                fixture.ControlPlane,
                new TestAiDagExecutionEngineServices(fixture.Store, accessor: fixture.Accessor),
                new AiChildContinuationScheduler(controller, new InMemoryAiSharedQueue()));

            var first = await continuation.EnqueueContinuationAsync(completed!.ToInvocationIdentity());
            var second = await continuation.EnqueueContinuationAsync(completed.ToInvocationIdentity());

            Assert.Equal(AiChildContinuationStatus.Scheduled, first.ContinuationStatus);
            Assert.Equal(AiChildContinuationStatus.Scheduled, second.ContinuationStatus);
            Assert.Equal(2, controller.Requests.Count);
            Assert.Single(controller.Requests
                .Select(request => request.RequestedSharedRunId)
                .Distinct(StringComparer.Ordinal));
            Assert.Single(controller.Requests
                .Select(request => request.RunRequest!.ExternalWaitContinuation!.ContinuationId)
                .Distinct(StringComparer.Ordinal));
            var authoritative = await relationStore.GetAsync(completed.ToInvocationIdentity());
            Assert.NotNull(authoritative);
            Assert.Equal(completed.ChildExecutionId, authoritative!.ChildExecutionId);
            Assert.Equal(completed.ChildResult!.ContentHash, authoritative.ChildResult!.ContentHash);
            Assert.Single(fixture.MemoryPayloads.Documents.Where(document =>
                document.Key.Contains("/child-run/", StringComparison.Ordinal)));
        }

        private static Task<bool> BindAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            AiChildExecutionRelation relation) => fixture.AsAsync(() => fixture.Services
                .GetRequiredService<AiPublishedChildDagBindingCoordinator>()
                .BindBeforeDispatchAsync(PublicationTestSupport.Scope, parent, relation));

        private static AiWorkerPublicationPreparer CreateWorkerPreparer(PublicationTestSupport.Fixture fixture) => new(
            fixture.Store,
            fixture.Accessor,
            fixture.ControlPlane,
            fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>(),
            fixture.Services.GetRequiredService<AiPublicationIdentity>(),
            PublicationTestSupport.Options,
            fixture.Services.GetRequiredService<AiImmutablePublicationStore>());

        private static ExecuteChildDagStep CreateStep(
            PublicationTestSupport.Fixture fixture,
            InMemoryAiChildExecutionRelationStore relationStore,
            AiChildDagSnapshotService snapshotService,
            IAiSharedRuntimeController controller) => new(
                relationStore,
                fixture.ControlPlane,
                new ThrowingPipelineDefinitionSourceSelector(),
                snapshotService,
                new AiChildDelegationPolicyCoordinator(
                    relationStore,
                    new AllowAllPolicyEngineFactory(),
                    snapshotService),
                new AiChildExecutionAllocator(relationStore, snapshotService),
                new AiChildExecutionDispatcher(relationStore, snapshotService, controller),
                new AiChildExecutionWaitingCoordinator(relationStore),
                new AiChildInvocationGenerationCoordinator(relationStore));

        private static AiStepExecutionContext CreateContext(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent,
            AiPipelineDefinition childDefinition,
            ExecuteChildDagStep step)
        {
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
            return new AiStepExecutionContext(
                fixture.Build(parent, state, CancellationToken.None),
                resolvedStep);
        }

        private static async Task<(AiPipelineDefinition Definition, AiStoredPayload Snapshot)> ReadChildAsync(
            PublicationTestSupport.Fixture fixture,
            AiExecutionRecord parent)
        {
            var json = await fixture.Services
                .GetRequiredService<Multiplexed.AI.Runtime.Execution.Payloads.Immutable.AiImmutableJsonPayloadReader>()
                .LoadAndVerifyAsync(parent.PipelineDefinitionSnapshot!);
            using var document = JsonDocument.Parse(json);
            var childElement = document.RootElement
                .GetProperty("Steps")
                .EnumerateArray()
                .Single(step => step.GetProperty("Name").GetString() == "invoke-child")
                .GetProperty("Config")
                .GetProperty(ExecuteChildDagStep.ChildDagDefinitionConfigKey);
            var child = JsonSerializer.Deserialize<AiPipelineDefinition>(childElement.GetRawText())
                ?? throw new InvalidOperationException("Published child definition could not be deserialized.");
            var snapshot = await CreateSnapshotService(fixture).FreezeDefinitionAsync(child, parent.ExecutionId);
            return (child, snapshot);
        }

        private static AiChildExecutionRelation CreateRelation(
            AiExecutionRecord parent,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot,
            string childExecutionId,
            AiChildExecutionRelationStatus status = AiChildExecutionRelationStatus.ChildAllocated) => new()
        {
            TenantId = parent.ExecutionContextSnapshot!.TenantId,
            ControlPlaneId = PublicationTestSupport.Scope.ControlPlaneId,
            ParentExecutionId = parent.ExecutionId,
            ParentCallSiteId = "invoke-child",
            ChildDagId = child.Name,
            ChildDagDefinitionVersion = child.Version!,
            FrozenChildDagDefinition = childSnapshot,
            CanonicalLogicalInvocationKey = "published-child",
            ChildInvocationKey = "published-child-key-" + childExecutionId,
            InvocationGeneration = 0,
            FrozenInvocationInput = childSnapshot,
            DelegatedExecutionContextSnapshot = parent.ExecutionContextSnapshot,
            DelegatedMetadata = new Dictionary<string, string>(StringComparer.Ordinal),
            DelegationPolicyBindingSnapshot = childSnapshot,
            Status = status,
            ChildExecutionId = childExecutionId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            ChildAllocatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            WaitingAtUtc = status == AiChildExecutionRelationStatus.Waiting
                ? DateTimeOffset.UtcNow
                : null
        };

        private static AiExecutionRecord CreateChildRecord(
            AiExecutionRecord parent,
            AiChildExecutionRelation relation,
            AiPipelineDefinition child,
            AiStoredPayload childSnapshot,
            AiExecutionStatus status = AiExecutionStatus.Running) => new()
        {
            ExecutionId = relation.ChildExecutionId!,
            PipelineName = child.Name,
            PipelineDefinitionSnapshot = childSnapshot,
            ExecutionMode = AiExecutionMode.Dag,
            Status = status,
            ContextKey = parent.ContextKey,
            ExecutionContextSnapshot = parent.ExecutionContextSnapshot,
            Steps = child.Steps.OrderBy(step => step.Order).Select(step => step.Name).ToList(),
            CompletedAtUtc = status is AiExecutionStatus.Completed or AiExecutionStatus.Failed or AiExecutionStatus.Cancelled
                ? DateTime.UtcNow
                : default
        };

        private static AiExecutionRecord CloneWithSnapshot(AiExecutionRecord source, AiStoredPayload snapshot) => new()
        {
            ExecutionId = source.ExecutionId,
            PipelineName = source.PipelineName,
            PipelineDefinitionSnapshot = snapshot,
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

        private static AiPipelinePublicationUpload CreateNestedUpload(string revision)
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

        private sealed class ThrowingPipelineDefinitionSourceSelector : IAiPipelineDefinitionSourceSelector
        {
            public IAiPipelineDefinitionProvider Select(string pipelineName) =>
                throw new InvalidOperationException("Published Child DAG recovery must use its frozen inline definition.");
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

        private sealed class ThrowAfterAcceptedSubmissionController : IAiSharedRuntimeController
        {
            private readonly CapturingSharedRuntimeController inner;
            private int hasThrown;

            public ThrowAfterAcceptedSubmissionController(CapturingSharedRuntimeController inner) =>
                this.inner = inner;

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => SubmitRunAsync(request, cancellationToken);

            public async Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                var result = await this.inner.SubmitRunAsync(request, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref this.hasThrown, 1) == 0)
                    throw new IOException("Injected interruption after accepted child submission.");
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

        private static byte[] DecodeBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
            return Convert.FromBase64String(padded);
        }
    }
}
