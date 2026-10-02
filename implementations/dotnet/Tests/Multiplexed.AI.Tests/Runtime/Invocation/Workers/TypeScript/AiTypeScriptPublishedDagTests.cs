using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>Real TypeScript and existing publication, authorization, journal and local DAG; memory stores only.</summary>
    [Trait("Category", "TypeScriptProcess")]
    public sealed class AiTypeScriptPublishedDagTests
    {
        [TypeScriptWorkerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Actual_TypeScript_Result_Is_Journaled_Before_The_Existing_Dag_Applies_It(bool success)
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            p.Clock.Set(DateTimeOffset.UtcNow);
            var source = "export function run(inputs: { amount: number }, context: unknown) { return { success: " +
                (success ? "true" : "false") + ", payload: { calculated: inputs.amount + 41 } }; }\n";
            var published = await p.PublishAsync(TypeScriptWorkerTestSupport.Upload(profile.Runtime, source: source));
            var parent = await p.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var before = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = TypeScriptWorkerTestSupport.Supervisor(fixture, await TypeScriptWorkerTestSupport.TransportAsync(), capacity, options);
            var dispatch = await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, dispatch.Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(before.OperationId, recorded.OperationId);
            Assert.Equal(success, recorded.Result!.Success);
            Assert.Equal("{\"calculated\":42}", recorded.Result.PayloadJson);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, recorded.ContinuationStatus);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.Store.GetRecordAsync(parent.ExecutionId))!.Status);
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first");
            await p.RunNextAsync(parent.ExecutionId);
            var step = (await p.Store.GetStateAsync(parent.ExecutionId))!.Steps["first"];
            Assert.Equal(success ? AiStepExecutionStatus.Completed : AiStepExecutionStatus.Failed, step.Status);
            Assert.Equal(recorded.OperationId, step.Result!.InvocationReceipt!.OperationId);
            Assert.Equal(recorded.ResultSha256, step.Result.InvocationReceipt.ResultSha256);
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
        }

        [TypeScriptWorkerFact]
        public async Task Unstarted_TypeScript_Function_Executes_Original_Code_After_Republication()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            p.Clock.Set(DateTimeOffset.UtcNow);
            var original = await p.PublishAsync(TypeScriptWorkerTestSupport.Upload(profile.Runtime, "1"));
            var parent = await p.CreateAsync(original);
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = TypeScriptWorkerTestSupport.Supervisor(fixture, await TypeScriptWorkerTestSupport.TransportAsync(), capacity, options);
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var firstId = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, firstId)).Disposition);
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first");
            await p.RunNextAsync(parent.ExecutionId);
            var secondId = firstId with { StepName = "second" };
            Assert.Null(await p.Journal.GetAsync(PublicationTestSupport.Scope, secondId));
            var replacement = await p.PublishAsync(TypeScriptWorkerTestSupport.Upload(profile.Runtime, "2"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, secondId)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, secondId))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            using var payload = JsonDocument.Parse(recorded.Result!.PayloadJson);
            Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "second");
            Assert.Equal(AiExecutionStatus.Completed, (await p.RunNextAsync(parent.ExecutionId)).Status);
            Assert.Equal(0, p.LatestLookups);
        }

        [TypeScriptWorkerFact]
        public async Task Locked_Node_Dependency_Executes_From_Immutable_Publication_Without_Registry_Resolution()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            p.Clock.Set(DateTimeOffset.UtcNow);
            var original = await p.PublishAsync(TypeScriptWorkerTestSupport.UploadWithLockedDependency(
                profile.Runtime, revision: "1", factor: "4"));
            var parent = await p.CreateAsync(original);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);

            var replacement = await p.PublishAsync(TypeScriptWorkerTestSupport.UploadWithLockedDependency(
                profile.Runtime, revision: "2", factor: "9"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            var identity = new AiDurableInvocationIdentity(
                PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = TypeScriptWorkerTestSupport.Supervisor(
                fixture, await TypeScriptWorkerTestSupport.TransportAsync(), capacity, options);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            using var payload = JsonDocument.Parse(recorded.Result!.PayloadJson);
            Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(4, payload.RootElement.GetProperty("amount").GetInt32());

            var engine = new AiDagExecutionEngine(
                p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first");
            await p.RunNextAsync(parent.ExecutionId);
            Assert.Equal(0, p.LatestLookups);
        }

        [TypeScriptWorkerFact]
        public async Task TypeScript_Exception_Retains_Uncertain_Invocation_Without_Manufacturing_Business_Failure()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            p.Clock.Set(DateTimeOffset.UtcNow);
            var published = await p.PublishAsync(TypeScriptWorkerTestSupport.Upload(profile.Runtime,
                source: "export function run(inputs: unknown, context: unknown) { throw new Error('technical failure'); }\n"));
            var parent = await p.CreateAsync(published);
            await p.RunNextAsync(parent.ExecutionId);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = TypeScriptWorkerTestSupport.Supervisor(fixture, await TypeScriptWorkerTestSupport.TransportAsync(), capacity, options);
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var retained = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Null(retained.Result);
            Assert.NotNull(retained.Lease);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.Store.GetRecordAsync(parent.ExecutionId))!.Status);
            p.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(AiWorkerDispatchDisposition.ReconciliationRequired,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
        }
    }
}
