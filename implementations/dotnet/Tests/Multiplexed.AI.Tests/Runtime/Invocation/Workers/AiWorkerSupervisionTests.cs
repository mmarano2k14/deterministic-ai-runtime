using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Real journal transitions with controlled worker liveness, faults and ambiguous acknowledgements.</summary>
    public sealed class AiWorkerSupervisionTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Business_Result_Is_Persisted_With_Pending_Continuation(bool success)
        {
            using var f = new WorkerTestSupport.Fixture(); var prepared = await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => Task.FromResult(new AiDurableInvocationResult(success, "{\"value\":42}"));
            var result = await f.DispatchAsync(); var stored = await f.ReadAsync();
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, result.Disposition);
            Assert.Equal(success ? AiDurableInvocationStatus.Succeeded : AiDurableInvocationStatus.Failed, stored.Status);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, stored.ContinuationStatus);
            Assert.Equal(prepared.OperationId, stored.OperationId); Assert.Equal(prepared.EffectIdempotencyKey, stored.EffectIdempotencyKey);
            Assert.Equal(1, stored.Lease!.Epoch); Assert.Equal(f.Options.MaxConcurrentProcesses, f.Capacity.Available);
        }
        [Fact]
        public async Task Terminal_Invocation_Does_Not_Launch_Again()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync(); await f.DispatchAsync();
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal, (await f.DispatchAsync()).Disposition);
            Assert.Single(f.Transport.Requests);
        }
        [Fact]
        public async Task Missing_Invocation_Is_Not_Created_By_Dispatch()
        {
            using var f = new WorkerTestSupport.Fixture();
            Assert.Equal(AiWorkerDispatchDisposition.NotReady, (await f.DispatchAsync()).Disposition);
            Assert.Empty(f.Transport.Requests); Assert.Equal(0, f.Preparer.Calls);
        }
        [Theory]
        [InlineData("transport")]
        [InlineData("preparation")]
        [InlineData("invalid-result")]
        public async Task Technical_Failure_Does_Not_Fabricate_A_Business_Result(string failure)
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            if (failure == "transport") f.Transport.Body = (_, _, _) => throw new IOException("connection lost");
            if (failure == "preparation") f.Preparer.Body = (_, _) => throw new UnauthorizedAccessException("no execute capability");
            if (failure == "invalid-result") f.Transport.Body = (_, _, _) => Task.FromResult(new AiDurableInvocationResult(true, "{"));
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure, (await f.DispatchAsync()).Disposition);
            var stored = await f.ReadAsync(); Assert.Equal(AiDurableInvocationStatus.Leased, stored.Status);
            Assert.Null(stored.Result); Assert.Equal(AiDurableInvocationContinuationStatus.None, stored.ContinuationStatus);
            if (failure == "preparation") Assert.Empty(f.Transport.Requests);
        }
        [Fact]
        public async Task Alive_Lease_Is_Not_Dispatched_By_Another_Supervisor()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            await f.Journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "other", TimeSpan.FromSeconds(30));
            Assert.Equal(AiWorkerDispatchDisposition.NotReady, (await f.DispatchAsync()).Disposition); Assert.Empty(f.Transport.Requests);
        }
        [Fact]
        public async Task Expired_Work_Requires_Reconciliation_By_Default()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => throw new IOException("unknown effect outcome"); await f.DispatchAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(AiWorkerDispatchDisposition.ReconciliationRequired, (await f.DispatchAsync()).Disposition);
            Assert.Single(f.Transport.Requests); Assert.Equal(1, (await f.ReadAsync()).Lease!.Epoch);
        }
        [Fact]
        public async Task Approved_Expired_Reassignment_Keeps_Operation_Effect_And_Frozen_Inputs()
        {
            using var f = new WorkerTestSupport.Fixture(new(allowExpiredLeaseReassignment: true)); await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => throw new IOException("worker exited"); await f.DispatchAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Transport.Body = (_, _, _) => Task.FromResult(new AiDurableInvocationResult(true, "{\"value\":42}"));
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await f.DispatchAsync()).Disposition);
            var requests = f.Transport.Requests.ToArray(); Assert.Equal(2, requests.Length);
            Assert.Equal(requests[0].OperationId, requests[1].OperationId); Assert.Equal(requests[0].EffectIdempotencyKey, requests[1].EffectIdempotencyKey);
            Assert.Equal(requests[0].Inputs.GetRawText(), requests[1].Inputs.GetRawText()); Assert.Equal(requests[0].Code.Target, requests[1].Code.Target);
            Assert.NotEqual(requests[0].WorkerId, requests[1].WorkerId); Assert.NotEqual(requests[0].RequestId, requests[1].RequestId);
            Assert.Equal(2, requests[1].Epoch); Assert.Equal(0, requests[1].Generation);
        }
        [Fact]
        public async Task Epoch_Limit_Retains_Uncertain_Work_Without_A_Synthetic_Failure()
        {
            using var f = new WorkerTestSupport.Fixture(new(allowExpiredLeaseReassignment: true, maxAssignmentEpoch: 2)); await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => throw new IOException("worker exited");
            await f.DispatchAsync(); f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.DispatchAsync(); f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(AiWorkerDispatchDisposition.ReconciliationRequired, (await f.DispatchAsync()).Disposition);
            Assert.Equal(2, f.Transport.Requests.Count); Assert.Null((await f.ReadAsync()).Result);
        }
        [Fact]
        public async Task Validated_Heartbeat_Renews_The_Same_Assignment()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync(); DateTimeOffset? before = null;
            f.Transport.Body = async (_, heartbeat, token) =>
            {
                before = (await f.ReadAsync()).Lease!.ExpiresAtUtc;
                f.Clock.Advance(TimeSpan.FromSeconds(6)); await heartbeat(token);
                var renewed = await f.ReadAsync(); Assert.True(renewed.Lease!.ExpiresAtUtc > before); Assert.Equal(1, renewed.Lease.Epoch);
                return new(true, "{}");
            };
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await f.DispatchAsync()).Disposition);
        }
        [Fact]
        public async Task Heartbeats_Before_Renewal_Interval_Do_Not_Create_Write_Traffic()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Transport.Body = async (_, heartbeat, token) =>
            {
                var before = f.Store.CasCalls; for (var i = 0; i < 100; i++) await heartbeat(token);
                Assert.Equal(before, f.Store.CasCalls); return new(true, "{}");
            };
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, (await f.DispatchAsync()).Disposition);
        }
        [Fact]
        public async Task Expired_Heartbeat_Cannot_Revive_The_Assignment()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Transport.Body = async (_, heartbeat, token) => { f.Clock.Advance(TimeSpan.FromMinutes(1)); await heartbeat(token); return new(true, "{}"); };
            Assert.Equal(AiWorkerDispatchDisposition.LeaseLost, (await f.DispatchAsync()).Disposition); Assert.Null((await f.ReadAsync()).Result);
        }
        [Fact]
        public async Task Lost_Renewal_Acknowledgement_Does_Not_Extend_Trust_Locally()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Transport.Body = async (_, heartbeat, token) =>
            { f.Clock.Advance(TimeSpan.FromSeconds(6)); f.Store.ThrowAfterNextWrite = true; await heartbeat(token); return new(true, "{}"); };
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure, (await f.DispatchAsync()).Disposition);
            Assert.Null((await f.ReadAsync()).Result);
        }
        [Fact]
        public async Task Lost_Result_Write_Acknowledgement_Does_Not_Reexecute_A_Terminal_Operation()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => { f.Store.ThrowAfterNextWrite = true; return Task.FromResult(new AiDurableInvocationResult(true, "{}")); };
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure, (await f.DispatchAsync()).Disposition);
            Assert.NotNull((await f.ReadAsync()).Result);
            Assert.Equal(AiWorkerDispatchDisposition.AlreadyTerminal, (await f.DispatchAsync()).Disposition); Assert.Single(f.Transport.Requests);
        }
        [Fact]
        public async Task Competing_Dispatchers_Launch_At_Most_One_Process_For_The_Same_Operation()
        {
            using var f = new WorkerTestSupport.Fixture(new(maxConcurrentProcesses: 32)); await f.PrepareAsync();
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => f.DispatchAsync()));
            Assert.Single(f.Transport.Requests); Assert.Equal(1, outcomes.Count(o => o.Disposition == AiWorkerDispatchDisposition.Accepted));
        }
        [Fact]
        public async Task Busy_Capacity_Does_Not_Consume_Another_Lease()
        {
            using var f = new WorkerTestSupport.Fixture(new(maxConcurrentProcesses: 1)); await f.PrepareAsync();
            using var held = f.Capacity.TryEnter();
            Assert.Equal(AiWorkerDispatchDisposition.Busy, (await f.DispatchAsync()).Disposition);
            Assert.Equal(AiDurableInvocationStatus.Prepared, (await f.ReadAsync()).Status); Assert.Empty(f.Transport.Requests);
        }
        [Fact]
        public async Task Slow_Preparation_Cannot_Start_A_Process_After_Lease_Safety_Expiry()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Preparer.Body = (record, _) => { f.Clock.Advance(TimeSpan.FromSeconds(29)); return Task.FromResult(WorkerTestSupport.Bundle(record.Definition.Target)); };
            Assert.Equal(AiWorkerDispatchDisposition.LeaseLost, (await f.DispatchAsync()).Disposition); Assert.Empty(f.Transport.Requests);
        }
        [Fact]
        public async Task Parent_Host_Cancellation_Propagates_And_Does_Not_Report_A_Business_Error()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync(); using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Body = async (_, _, token) => { started.SetResult(true); await Task.Delay(Timeout.Infinite, token); return new(true, "{}"); };
            var dispatch = f.DispatchAsync(cancel.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch); Assert.Null((await f.ReadAsync()).Result);
            Assert.Equal(f.Options.MaxConcurrentProcesses, f.Capacity.Available);
        }
        [Fact]
        public async Task Unconfirmed_Process_Cleanup_Quarantines_Capacity()
        {
            using var f = new WorkerTestSupport.Fixture(new(maxConcurrentProcesses: 1)); await f.PrepareAsync();
            f.Transport.Body = (_, _, _) => throw new AiWorkerProcessCleanupException(new IOException("injected"));
            Assert.Equal(AiWorkerDispatchDisposition.CapacityQuarantined, (await f.DispatchAsync()).Disposition);
            Assert.Equal(0, f.Capacity.Available); Assert.Equal(1, f.Capacity.Quarantined); Assert.Null((await f.ReadAsync()).Result);
        }
        [Fact]
        public async Task Foreign_Control_Plane_Cannot_Dispatch()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Supervisor.DispatchAsync(
                DurableInvocationTestSupport.Scope with { ControlPlaneId = "foreign" }, DurableInvocationTestSupport.Identity));
            Assert.Empty(f.Transport.Requests);
        }
        [Fact]
        public async Task Code_Substitution_By_A_Preparer_Is_Refused_Before_Transport()
        {
            using var f = new WorkerTestSupport.Fixture(); await f.PrepareAsync();
            f.Preparer.Body = (record, _) => Task.FromResult(WorkerTestSupport.Bundle(record.Definition.Target with { ImplementationRef = "changed" }));
            Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure, (await f.DispatchAsync()).Disposition); Assert.Empty(f.Transport.Requests);
        }
    }
}
