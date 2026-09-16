using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet
{
    /// <summary>Real .NET worker plus existing publication, authorization, journal and local DAG.</summary>
    [Trait("Category", "DotNetProcess")]
    public sealed class AiDotNetPublishedDagTests
    {
        [Fact]
        public async Task Actual_DotNet_Result_Is_Journaled_Before_Existing_Dag_Application()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var published = await p.PublishAsync(DotNetWorkerTestSupport.Upload(profile.Runtime));
            var parent = await p.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = DotNetWorkerTestSupport.Supervisor(fixture, await DotNetWorkerTestSupport.TransportAsync(), capacity, options);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.True(recorded.Result!.Success);
            using (var payload = JsonDocument.Parse(recorded.Result.PayloadJson))
            {
                Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
                Assert.Equal(1, payload.RootElement.GetProperty("amount").GetInt32());
            }
            Assert.Equal(AiExecutionStatus.Waiting, (await p.Store.GetRecordAsync(parent.ExecutionId))!.Status);
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first"); await p.RunNextAsync(parent.ExecutionId);
            var step = (await p.Store.GetStateAsync(parent.ExecutionId))!.Steps["first"];
            Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
            Assert.Equal(recorded.ResultSha256, step.Result!.InvocationReceipt!.ResultSha256);
        }

        [Fact]
        public async Task Unstarted_DotNet_Function_Uses_Run_Pinned_Publication_After_Republish()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var original = await p.PublishAsync(DotNetWorkerTestSupport.Upload(profile.Runtime, "1"));
            var parent = await p.CreateAsync(original);
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = DotNetWorkerTestSupport.Supervisor(fixture, await DotNetWorkerTestSupport.TransportAsync(), capacity, options);
            var engine = new AiDagExecutionEngine(p.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var first = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await supervisor.DispatchAsync(PublicationTestSupport.Scope, first)).Disposition);
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first"); await p.RunNextAsync(parent.ExecutionId);
            var replacement = await p.PublishAsync(DotNetWorkerTestSupport.Upload(profile.Runtime, "2"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);
            var second = first with { StepName = "second" };
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await supervisor.DispatchAsync(PublicationTestSupport.Scope, second)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, second))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            using var payload = JsonDocument.Parse(recorded.Result!.PayloadJson);
            Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
        }

        [Fact]
        public async Task DotNet_Exception_Remains_Technical_And_Does_Not_Create_Business_Result()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture(); var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime; p.Clock.Set(DateTimeOffset.UtcNow);
            var upload = DotNetWorkerTestSupport.Upload(profile.Runtime);
            var functions = upload.Functions.Select(f => f with { EntryPointSymbol = DotNetWorkerTestSupport.TypeName + "::Throw" }).ToArray();
            var published = await p.PublishAsync(upload with { Functions = functions });
            var parent = await p.CreateAsync(published); await p.RunNextAsync(parent.ExecutionId);
            var identity = new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, parent.ExecutionId, "first");
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = DotNetWorkerTestSupport.Supervisor(fixture, await DotNetWorkerTestSupport.TransportAsync(), capacity, options);
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            Assert.Null((await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!.Result);
        }
        [Fact]
        public async Task Packaged_Managed_Dependency_Executes_Through_Immutable_Publication_And_Existing_Dag()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var p = fixture.Publication;
            p.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            p.Clock.Set(DateTimeOffset.UtcNow);

            var published = await p.PublishAsync(DotNetWorkerTestSupport.Upload(
                profile.Runtime,
                packagedDependency: true,
                method: "UseDependency"));
            var parent = await p.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await p.RunNextAsync(parent.ExecutionId)).Status);

            var identity = new AiDurableInvocationIdentity(
                PublicationTestSupport.Scope.TenantId,
                parent.ExecutionId,
                "first");
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = DotNetWorkerTestSupport.Supervisor(
                fixture,
                await DotNetWorkerTestSupport.TransportAsync(),
                capacity,
                options);

            Assert.Equal(
                AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await p.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.True(recorded.Result!.Success);
            Assert.Equal("3", recorded.Result.PayloadJson);

            var engine = new AiDagExecutionEngine(
                p.EngineServices,
                DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first");
            await p.RunNextAsync(parent.ExecutionId);
            var step = (await p.Store.GetStateAsync(parent.ExecutionId))!.Steps["first"];
            Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
            Assert.Equal(recorded.ResultSha256, step.Result!.InvocationReceipt!.ResultSha256);
        }

    }
}
