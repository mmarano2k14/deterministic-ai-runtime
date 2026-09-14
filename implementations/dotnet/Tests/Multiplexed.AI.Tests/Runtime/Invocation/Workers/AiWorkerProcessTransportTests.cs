using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;
using System.Text.Json;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Actual dotnet child processes and redirected pipes, not fake network transports or language execution.</summary>
    [Trait("Category", "WorkerProcess")]
    public sealed class AiWorkerProcessTransportTests
    {
        [Theory]
        [InlineData("success", true)]
        [InlineData("business-failure", false)]
        public async Task Real_Process_Emits_A_Typed_Business_Result(string mode, bool success)
        {
            var transport = WorkerTestSupport.ProbeTransport(mode); var heartbeats = 0;
            var result = await transport.InvokeAsync(WorkerTestSupport.Request(), _ => { heartbeats++; return Task.CompletedTask; });
            Assert.Equal(success, result.Success); Assert.Equal("{\"value\":42}", result.PayloadJson); Assert.Equal(1, heartbeats);
        }
        [Fact]
        public async Task Real_Process_Receives_Pinned_Bytes_And_Only_Explicit_Environment()
        {
            var request = WorkerTestSupport.Request();
            var result = await WorkerTestSupport.ProbeTransport("inspect").InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson); var root = json.RootElement;
            Assert.Equal(request.OperationId, root.GetProperty("operationId").GetString());
            Assert.Equal(request.Code.Target.PublicationRef, root.GetProperty("publicationRef").GetString());
            Assert.Equal("Y29kZS0x", root.GetProperty("source").GetString());
            Assert.Equal(1, root.GetProperty("input").GetProperty("amount").GetInt32());
            Assert.Equal("explicit-only", root.GetProperty("explicitValue").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("unexpectedPath").ValueKind);
            var pid = root.GetProperty("processId").GetInt32(); Assert.NotEqual(Environment.ProcessId, pid);
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); } catch (ArgumentException) { }
        }
        [Fact]
        public async Task Heartbeat_Callbacks_Require_Actual_Validated_Frames()
        {
            var count = 0;
            await WorkerTestSupport.ProbeTransport("heartbeats").InvokeAsync(WorkerTestSupport.Request(), _ => { count++; return Task.CompletedTask; });
            Assert.Equal(6, count);
        }
        [Theory]
        [InlineData("wrong-request")]
        [InlineData("wrong-version")]
        [InlineData("wrong-epoch")]
        [InlineData("early-result")]
        [InlineData("duplicate-ready")]
        [InlineData("duplicate-result")]
        [InlineData("invalid-json")]
        [InlineData("missing-result")]
        [InlineData("nonzero-exit")]
        [InlineData("stdout-flood")]
        [InlineData("stderr-flood")]
        public async Task Invalid_Process_Exchange_Is_Not_A_Business_Result(string mode)
        {
            var failure = await Record.ExceptionAsync(() => WorkerTestSupport.ProbeTransport(mode)
                .InvokeAsync(WorkerTestSupport.Request(), _ => Task.CompletedTask));
            Assert.NotNull(failure); Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }
        [Theory]
        [InlineData("hang-start")]
        [InlineData("hang-heartbeat")]
        [InlineData("result-no-exit")]
        public async Task Silence_And_Nonexit_Are_Bounded_And_The_Root_Is_Reaped(string mode)
        {
            var options = new AiWorkerProcessTransportOptions(startupTimeout: TimeSpan.FromSeconds(2),
                heartbeatTimeout: TimeSpan.FromSeconds(1), shutdownTimeout: TimeSpan.FromSeconds(1));
            var operation = WorkerTestSupport.ProbeTransport(mode, options).InvokeAsync(WorkerTestSupport.Request(), _ => Task.CompletedTask);
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.IsType<TimeoutException>(failure); Assert.True(operation.IsCompleted);
        }
        [Fact]
        public async Task Cancellation_Stops_A_Ready_Real_Worker()
        {
            using var cancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = WorkerTestSupport.ProbeTransport("hang-heartbeat").InvokeAsync(WorkerTestSupport.Request(),
                _ => { ready.TrySetResult(true); return Task.CompletedTask; }, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        [Fact]
        public async Task Preexpired_Deadline_Does_Not_Start_A_Process()
        {
            var request = WorkerTestSupport.Request() with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            await Assert.ThrowsAsync<TimeoutException>(() => WorkerTestSupport.ProbeTransport().InvokeAsync(request, _ => Task.CompletedTask));
        }
        [Fact]
        public async Task Host_File_Digest_Mismatch_Is_Refused_Before_Start()
        {
            var profile = WorkerTestSupport.ProbeProfile();
            var changed = new AiWorkerProcessProfile(profile.Runtime, profile.ExecutablePath, new string('0', 64),
                profile.Arguments, profile.WorkingDirectory, profile.Environment, profile.VerifiedHostFiles);
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { changed }), new());
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.InvokeAsync(WorkerTestSupport.Request(), _ => Task.CompletedTask));
        }
        [Fact]
        public async Task An_Uninstalled_Profile_Cannot_Fall_Back_To_Local_Code()
        {
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            await Assert.ThrowsAsync<NotSupportedException>(() => transport.InvokeAsync(WorkerTestSupport.Request(), _ => Task.CompletedTask));
        }

        [Fact]
        public async Task Supervisor_Uses_The_Real_Process_And_Leaves_Dag_Application_To_The_Existing_Runner()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (parent, invocation) = await f.PrepareAsync();
            var options = new AiWorkerSupervisionOptions(); using var capacity = new AiWorkerProcessCapacity(options);
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { WorkerTestSupport.ProbeProfile() }),
                new(), f.Publication.Clock);
            var supervisor = new AiWorkerInvocationSupervisor(f.Publication.Journal, f.Preparer, transport,
                f.Publication.ControlPlane, capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, f.Publication.Clock);
            var dispatched = await supervisor.DispatchAsync(invocation.Definition.Scope, invocation.Definition.Identity);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, dispatched.Disposition);
            var stored = (await f.Publication.Journal.GetAsync(invocation.Definition.Scope, invocation.Definition.Identity))!;
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, stored.ContinuationStatus);
            Assert.Equal(invocation.OperationId, stored.OperationId);
            // A completed private-pipe exchange does not directly resume or finalize the DAG.
            var afterDispatch = (await f.Publication.Store.GetRecordAsync(parent.ExecutionId))!;
            Assert.Equal(parent.Status, afterDispatch.Status);
            Assert.False(afterDispatch.IsTerminal);
            var engine = new AiDagExecutionEngine(f.Publication.EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(parent.ExecutionId, "first");
            await f.Publication.RunNextAsync(parent.ExecutionId);
            var state = (await f.Publication.Store.GetStateAsync(parent.ExecutionId))!;
            Assert.Equal(Multiplexed.Abstractions.AI.Execution.AiStepExecutionStatus.Completed, state.Steps["first"].Status);
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal,
                (await supervisor.DispatchAsync(invocation.Definition.Scope, invocation.Definition.Identity)).Disposition);
            Assert.Equal(options.MaxConcurrentProcesses, capacity.Available);
        }
        [Fact]
        public async Task Real_Process_Result_Can_Be_Accepted_By_The_Actual_Durable_Journal()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            var leased = (await f.Publication.Journal.TryAcquireWorkerLeaseAsync(invocation.Definition.Scope, invocation.Definition.Identity,
                "probe-worker", TimeSpan.FromMinutes(1), false, 1))!;
            var bundle = await f.Preparer.PrepareAsync(leased);
            var request = WorkerTestSupport.Request() with
            {
                OperationId = leased.OperationId, EffectIdempotencyKey = leased.EffectIdempotencyKey,
                WorkerId = leased.Lease!.WorkerId, Epoch = leased.Lease.Epoch,
                ExecutionId = leased.Definition.Identity.ExecutionId, StepName = leased.Definition.Identity.StepName, Code = bundle
            };
            var result = await WorkerTestSupport.ProbeTransport().InvokeAsync(request, _ => Task.CompletedTask);
            var status = await f.Publication.Journal.CompleteAsync(leased.Definition.Scope, leased.Definition.Identity, leased.Lease, result);
            Assert.Equal(Multiplexed.Abstractions.AI.Invocation.Durable.AiDurableInvocationCompletionStatus.Accepted, status);
            Assert.Equal(Multiplexed.Abstractions.AI.Invocation.Durable.AiDurableInvocationContinuationStatus.Pending,
                (await f.Publication.Journal.GetAsync(leased.Definition.Scope, leased.Definition.Identity))!.ContinuationStatus);
        }
    }
}
