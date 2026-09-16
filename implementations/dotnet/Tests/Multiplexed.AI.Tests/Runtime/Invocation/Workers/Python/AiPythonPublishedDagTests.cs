using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python
{
    /// <summary>Real Python and existing publication, authorization, journal and local DAG; memory stores only.</summary>
    [Trait("Category", "PythonProcess")]
    public sealed class AiPythonPublishedDagTests
    {
        [PythonWorkerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Actual_Python_Result_Is_Journaled_Before_The_Existing_Dag_Applies_It(bool success)
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var source = "def run(inputs, context):\n    return {\"success\": " + (success ? "True" : "False") +
                ", \"payload\": {\"calculated\": inputs[\"amount\"] + 41}}";
            var published = await p.PublishAsync(PythonWorkerTestSupport.Upload(profile.Runtime, source: source));
            var parent = await p.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var before = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(fixture, await PythonWorkerTestSupport.TransportAsync(), capacity, options);
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

        [PythonWorkerFact]
        public async Task Unstarted_Python_Function_Executes_Original_Code_After_Republication()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var original = await p.PublishAsync(PythonWorkerTestSupport.Upload(profile.Runtime, "1"));
            var parent = await p.CreateAsync(original);
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(fixture, await PythonWorkerTestSupport.TransportAsync(), capacity, options);
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var firstId = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await supervisor.DispatchAsync(PublicationTestSupport.Scope, firstId)).Disposition);
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first"); await p.RunNextAsync(parent.ExecutionId);
            var secondId = firstId with { StepName = "second" };
            Assert.Null(await p.Journal.GetAsync(PublicationTestSupport.Scope, secondId));
            var replacement = await p.PublishAsync(PythonWorkerTestSupport.Upload(profile.Runtime, "2"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await supervisor.DispatchAsync(PublicationTestSupport.Scope, secondId)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, secondId))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            using var payload = JsonDocument.Parse(recorded.Result!.PayloadJson);
            Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "second");
            Assert.Equal(AiExecutionStatus.Completed, (await p.RunNextAsync(parent.ExecutionId)).Status);
            Assert.Equal(0, p.LatestLookups);
        }

        [PythonWorkerFact]
        public async Task Published_Pure_Python_Wheel_Executes_From_The_Pinned_Immutable_Environment()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var source = "from wheel_rules import transform\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": transform(inputs[\"amount\"])}";
            var published = await p.PublishAsync(PythonWorkerTestSupport.Upload(
                profile.Runtime, source: source, dependencies: new[] { PythonWorkerTestSupport.WheelUpload() }));
            var parent = await p.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(
                fixture, await PythonWorkerTestSupport.TransportAsync(), capacity, options);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal("84", recorded.Result!.PayloadJson);
            Assert.Equal(published.PublicationRef, recorded.Definition.Target.PublicationRef);
            Assert.Equal(0, p.LatestLookups);
        }

        [PythonWorkerFact]
        public async Task Unstarted_Run_Keeps_Original_Wheel_After_Dependency_Republication()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var source = "from wheel_rules import transform\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": transform(inputs[\"amount\"])}";
            var original = await p.PublishAsync(PythonWorkerTestSupport.Upload(
                profile.Runtime, source: source, dependencies: new[] { PythonWorkerTestSupport.WheelUpload(4) }));
            var parent = await p.CreateAsync(original);
            var replacement = await p.PublishAsync(PythonWorkerTestSupport.Upload(
                profile.Runtime, source: source, dependencies: new[] { PythonWorkerTestSupport.WheelUpload(5) }));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(
                fixture, await PythonWorkerTestSupport.TransportAsync(), capacity, options);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal("84", recorded.Result!.PayloadJson);
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            Assert.Equal(0, p.LatestLookups);
        }

        [PythonWorkerFact]
        public async Task Python_Exception_Retains_Uncertain_Invocation_Without_Manufacturing_Business_Failure()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var published = await p.PublishAsync(PythonWorkerTestSupport.Upload(profile.Runtime,
                source: "def run(inputs, context):\n    raise ValueError('technical failure')"));
            var parent = await p.CreateAsync(published); await p.RunNextAsync(parent.ExecutionId);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(fixture, await PythonWorkerTestSupport.TransportAsync(), capacity, options);
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var retained = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Null(retained.Result); Assert.NotNull(retained.Lease);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.Store.GetRecordAsync(parent.ExecutionId))!.Status);
            p.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(AiWorkerDispatchDisposition.ReconciliationRequired,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
        }
    }
}
