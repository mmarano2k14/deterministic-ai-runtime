using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    /// <summary>
    /// Controlled cold-service restoration after durable result acceptance. These cases
    /// use the real journal, publication, supervisor and local DAG, but a counted worker
    /// transport double. They are not OS-host-kill or distributed persistence tests.
    /// </summary>
    public sealed class AiDurableInvocationColdRestoreTests
    {
        [Theory]
        [InlineData("python", true, false)]
        [InlineData("python", false, false)]
        [InlineData("typescript", true, false)]
        [InlineData("typescript", false, false)]
        [InlineData("dotnet", true, false)]
        [InlineData("dotnet", false, false)]
        [InlineData("python", true, true)]
        [InlineData("python", false, true)]
        [InlineData("typescript", true, true)]
        [InlineData("typescript", false, true)]
        [InlineData("dotnet", true, true)]
        [InlineData("dotnet", false, true)]
        public async Task Accepted_Result_Survives_Cold_Restore_And_Duplicate_Continuation_Without_Worker_Relaunch(
            string language, bool success, bool scheduledBeforeInterruption)
        {
            var checkpoint = await DurableInvocationColdRestoreProof.CaptureAsync(language, success, scheduledBeforeInterruption);
            await DurableInvocationColdRestoreProof.RestoreAndApplyAsync(checkpoint);
        }
    }

    /// <summary>Only serialized data crosses the interruption boundary; the original service scopes are disposed.</summary>
    internal static class DurableInvocationColdRestoreProof
    {
        internal sealed record Checkpoint(string RecordJson, string StateJson, string JournalJson,
            string PayloadsJson, string EnvironmentsJson, string ExpectedInvocationJson,
            string? PriorContinuationJson, DateTimeOffset AtUtc, int InitialWorkerCalls);

        internal static async Task<Checkpoint> CaptureAsync(string language, bool success, bool scheduled,
            bool realDotNet = false)
        {
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Clock.Set(DateTimeOffset.UtcNow);
            AiPipelinePublicationUpload original;
            AiPipelinePublicationUpload replacement;
            AiWorkerProcessTransport? actualTransport = null;
            if (realDotNet)
            {
                var profile = await DotNetWorkerTestSupport.ProfileAsync();
                p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
                original = SingleStep(DotNetWorkerTestSupport.Upload(profile.Runtime));
                replacement = SingleStep(DotNetWorkerTestSupport.Upload(profile.Runtime, "2"));
                actualTransport = await DotNetWorkerTestSupport.TransportAsync();
            }
            else
            {
                original = SingleStep(PublicationTestSupport.Upload(language: language, secondLanguage: language));
                replacement = SingleStep(PublicationTestSupport.Upload("2", language, language));
            }
            var publication = await p.PublishAsync(original);
            var parent = await p.CreateAsync(publication);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var counted = new WorkerTestSupport.Transport
            {
                Body = actualTransport is null
                    ? (_, _, _) => Task.FromResult(new AiDurableInvocationResult(success, "{\"revision\":1,\"amount\":1}"))
                    : (request, heartbeat, token) => actualTransport!.InvokeAsync(request, heartbeat, token)
            };
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = new AiWorkerInvocationSupervisor(p.Journal, fixture.Preparer, counted, p.ControlPlane,
                capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, p.Clock);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            Assert.Single(counted.Requests);
            var accepted = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(success, accepted.Result!.Success);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, accepted.ContinuationStatus);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.Store.GetRecordAsync(parent.ExecutionId))!.Status);
            var waiting = (await p.Store.GetStateAsync(parent.ExecutionId))!.Steps["first"];
            Assert.Equal(AiStepExecutionStatus.WaitingForExternal, waiting.Status);
            Assert.Null(waiting.Result?.InvocationReceipt);

            string? priorContinuation = null;
            if (scheduled)
            {
                var controller = new DurableInvocationDagTestSupport.Controller();
                var coordinator = Coordinator(p, controller);
                Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled,
                    (await coordinator.ReconcileAsync(PublicationTestSupport.Scope, identity))!.ContinuationStatus);
                priorContinuation = JsonSerializer.Serialize(Assert.Single(controller.Requests));
                // The delivery is intentionally not consumed before the fixture is discarded.
            }
            var newer = await p.PublishAsync(replacement);
            Assert.NotEqual(publication.PublicationRef, newer.PublicationRef);
            var expected = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(publication.PublicationRef, expected.Definition.Target.PublicationRef);

            // No original fixture, store, context, delegate or transport is returned.
            return new(
                JsonSerializer.Serialize(await p.Store.GetRecordAsync(parent.ExecutionId)),
                JsonSerializer.Serialize(await p.Store.GetStateAsync(parent.ExecutionId)),
                p.JournalStore.Export(), p.MemoryPayloads.Export(),
                JsonSerializer.Serialize(p.Environments.Entries.Values.ToArray()),
                JsonSerializer.Serialize(expected), priorContinuation, p.Clock.GetUtcNow(), counted.Requests.Count);
        }

        internal static async Task RestoreAndApplyAsync(Checkpoint checkpoint)
        {
            Assert.Equal(1, checkpoint.InitialWorkerCalls);
            var expected = JsonSerializer.Deserialize<AiDurableInvocationRecord>(checkpoint.ExpectedInvocationJson)!;
            var identity = expected.Definition.Identity;
            using var fresh = new WorkerTestSupport.PublishedFixture();
            var p = fresh.Publication;
            // The old lease is deliberately expired. A stored terminal result does not
            // need a renewed lease, a new assignment, or the old process to return.
            p.Clock.Set(checkpoint.AtUtc.AddMinutes(10));
            p.MemoryPayloads.Restore(checkpoint.PayloadsJson);
            p.JournalStore.Restore(checkpoint.JournalJson);
            p.Environments.Entries.Clear();
            foreach (var runtime in JsonSerializer.Deserialize<AiPublicationEnvironment[]>(checkpoint.EnvironmentsJson)!)
                p.Environments.Entries.Add(runtime.Reference, runtime);
            var restoredRecord = JsonSerializer.Deserialize<AiExecutionRecord>(checkpoint.RecordJson)!;
            var restoredState = JsonSerializer.Deserialize<AiExecutionState>(checkpoint.StateJson)!;
            await p.Store.CreateAsync(restoredRecord, restoredState);
            var originalPublication = await p.ReadAsync(expected.Definition.Target.PublicationRef);
            var sameRun = await p.CreateAsync(originalPublication);
            Assert.Equal(identity.ExecutionId, sameRun.ExecutionId);
            Assert.Equal(0, p.ContextSeeds);
            Assert.Equal(0, p.LatestLookups);

            var noPreparation = new WorkerTestSupport.Preparer
            { Body = (_, _) => throw new InvalidOperationException("A terminal invocation must not prepare a new worker.") };
            var noLaunch = new WorkerTestSupport.Transport
            { Body = (_, _, _) => throw new InvalidOperationException("A terminal invocation must not relaunch its worker.") };
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = new AiWorkerInvocationSupervisor(p.Journal, noPreparation, noLaunch, p.ControlPlane,
                capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, p.Clock);
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            Assert.Equal(0, noPreparation.Calls);
            Assert.Empty(noLaunch.Requests);

            var before = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(expected.Definition, before.Definition);
            Assert.Equal(expected.OperationId, before.OperationId);
            Assert.Equal(expected.EffectIdempotencyKey, before.EffectIdempotencyKey);
            Assert.Equal(expected.InputsSha256, before.InputsSha256);
            Assert.Equal(expected.Result, before.Result);
            Assert.Equal(expected.ResultSha256, before.ResultSha256);
            Assert.Equal(expected.Lease, before.Lease);
            Assert.Equal(expected.ContinuationStatus, before.ContinuationStatus);

            var controller = new DurableInvocationDagTestSupport.Controller();
            var coordinator = Coordinator(p, controller);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled,
                (await coordinator.ReconcileAsync(PublicationTestSupport.Scope, identity))!.ContinuationStatus);
            var request = Assert.Single(controller.Requests);
            Assert.Equal(AiDurableInvocationDagContinuationScheduler.SharedRunId(expected), request.RequestedSharedRunId);
            var continuation = request.RunRequest!.ExternalWaitContinuation!;
            Assert.Equal(identity.ExecutionId, continuation.ExecutionId);
            Assert.Equal(identity.StepName, continuation.StepName);
            Assert.Null(request.RunRequest.RequestedExecutionId);
            if (checkpoint.PriorContinuationJson is not null)
            {
                var earlier = JsonSerializer.Deserialize<AiSharedRuntimeControllerRequest>(checkpoint.PriorContinuationJson)!;
                Assert.Equal(earlier.RequestedSharedRunId, request.RequestedSharedRunId);
                Assert.Equal(earlier.RunRequest!.ExternalWaitContinuation!.ContinuationId, continuation.ContinuationId);
            }

            // Simulated queue consumption uses the existing external-wait transition;
            // the test controller captures dispatch but is not a production queue host.
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(continuation.ExecutionId, continuation.StepName);
            await engine.ResumeExternalWaitingStepAsync(continuation.ExecutionId, continuation.StepName);
            var terminal = await p.RunNextAsync(identity.ExecutionId);
            Assert.Equal(expected.Result!.Success ? AiExecutionStatus.Completed : AiExecutionStatus.Failed, terminal.Status);
            var applied = (await p.Store.GetStateAsync(identity.ExecutionId))!.Steps[identity.StepName];
            Assert.Equal(expected.Result.Success ? AiStepExecutionStatus.Completed : AiStepExecutionStatus.Failed, applied.Status);
            Assert.Equal(expected.OperationId, applied.Result!.InvocationReceipt!.OperationId);
            Assert.Equal(expected.ResultSha256, applied.Result.InvocationReceipt.ResultSha256);
            Assert.Equal(expected.Result.PayloadJson, Assert.IsType<JsonElement>(applied.Result.Value).GetRawText());
            Assert.Equal(restoredState.Steps[identity.StepName].RecoveryCount, applied.RecoveryCount);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled,
                (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!.ContinuationStatus);

            Assert.Equal(AiDurableInvocationContinuationStatus.Applied,
                (await coordinator.ReconcileAsync(PublicationTestSupport.Scope, identity))!.ContinuationStatus);
            var version = (await p.Store.GetRecordAsync(identity.ExecutionId))!.Version;
            await engine.ResumeExternalWaitingStepAsync(continuation.ExecutionId, continuation.StepName);
            await coordinator.ReconcileAsync(PublicationTestSupport.Scope, identity);
            Assert.Equal(version, (await p.Store.GetRecordAsync(identity.ExecutionId))!.Version);
            Assert.Single(controller.Requests);
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            Assert.Equal(0, noPreparation.Calls);
            Assert.Empty(noLaunch.Requests);
            Assert.Equal(0, p.LatestLookups);
            var final = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(expected.Definition, final.Definition);
            Assert.Equal(expected.Result, final.Result);
            Assert.Equal(expected.ResultSha256, final.ResultSha256);
            Assert.Equal(expected.Lease, final.Lease);
        }

        private static AiPipelinePublicationUpload SingleStep(AiPipelinePublicationUpload upload) => upload with
        {
            Definition = PublicationTestSupport.Copy(upload.Definition, steps: upload.Definition.Steps.Where(s => s.Name == "first").ToArray()),
            Functions = upload.Functions.Where(function => function.Site.StepName == "first").ToArray()
        };

        private static AiDurableInvocationDagContinuationCoordinator Coordinator(PublicationTestSupport.Fixture p,
            DurableInvocationDagTestSupport.Controller controller) => new(p.Journal, p.Store,
                new DurableInvocationDagTestSupport.FullSteps(), p.Services.GetRequiredService<AiDurableInvocationDagBinding>(),
                p.ControlPlane, p.Accessor,
                new AiDurableInvocationDagContinuationScheduler(controller, new InMemoryAiSharedQueue(), p.Accessor, p.ControlPlane));
    }
}
