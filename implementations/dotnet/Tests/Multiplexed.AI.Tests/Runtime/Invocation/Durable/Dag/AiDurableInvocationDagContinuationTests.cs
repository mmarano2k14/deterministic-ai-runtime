using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using static Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag.DurableInvocationDagTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    public sealed class AiDurableInvocationDagContinuationTests
    {
        [Fact]
        public async Task Result_Before_Park_Remains_Pending_Then_Schedules_The_Exact_Continuation()
        {
            using var fixture = await CreateAsync();
            var completed = await fixture.TerminalAsync();
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Empty(fixture.Controller.Requests);
            await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal, AiExecutionStatus.Waiting);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled, (await fixture.ReconcileAsync())!.ContinuationStatus);
            var request = Assert.Single(fixture.Controller.Requests);
            Assert.Equal(AiSharedRuntimeSubmitMode.QueueFirst, request.SubmitModeOverride);
            Assert.Equal(AiDurableInvocationDagContinuationScheduler.SharedRunId(completed), request.RequestedSharedRunId);
            Assert.Equal(Identity.ExecutionId, request.RunRequest!.ExternalWaitContinuation!.ExecutionId);
            Assert.Equal(Identity.StepName, request.RunRequest.ExternalWaitContinuation.StepName);
            Assert.Null(request.RunRequest.RequestedExecutionId);
            Assert.Null(request.RunRequest.PipelineDefinition);
            Assert.Null(request.RunRequest.PipelineDefinitionSnapshot);
            Assert.Null(request.RunRequest.Input);
            Assert.DoesNotContain(request.Metadata.Keys, key => key.StartsWith("recovery", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.Running)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        public async Task Consumed_Or_Lost_Signal_Remains_Retryable_With_Stable_Identity(AiStepExecutionStatus status)
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal);
            await fixture.ReconcileAsync();
            await fixture.SetStateAsync(status);
            await fixture.ReconcileAsync();
            Assert.Equal(2, fixture.Controller.Requests.Count);
            Assert.Equal(fixture.Controller.Requests[0].RequestedSharedRunId, fixture.Controller.Requests[1].RequestedSharedRunId);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled,
                (await fixture.Journal.GetAsync(Scope, Identity))!.ContinuationStatus);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Application_Receipt_Requires_Terminal_Parent_Before_Acknowledgement(bool success)
        {
            using var fixture = await CreateAsync();
            var invocation = await fixture.TerminalAsync(success);
            var result = AiDurableInvocationResultMapper.Map(invocation);
            var status = success ? AiStepExecutionStatus.Completed : AiStepExecutionStatus.Failed;
            await fixture.SetStateAsync(status, result: result);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled, (await fixture.ReconcileAsync())!.ContinuationStatus);
            await fixture.SetStateAsync(status, success ? AiExecutionStatus.Completed : AiExecutionStatus.Failed, result);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
            var calls = fixture.Controller.Requests.Count;
            await fixture.ReconcileAsync();
            Assert.Equal(calls, fixture.Controller.Requests.Count);
        }

        [Theory]
        [InlineData(AiExecutionStatus.Completed)]
        [InlineData(AiExecutionStatus.Failed)]
        [InlineData(AiExecutionStatus.Cancelled)]
        public async Task Terminal_Parent_Without_Receipt_Suppresses_Instead_Of_Reexecuting(AiExecutionStatus status)
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal, status);
            Assert.Equal(AiDurableInvocationContinuationStatus.Suppressed, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Empty(fixture.Controller.Requests);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("operation")]
        [InlineData("digest")]
        [InlineData("outcome")]
        public async Task Terminal_Call_Site_With_Wrong_Receipt_Cannot_Be_Acknowledged(string failure)
        {
            using var fixture = await CreateAsync();
            var invocation = await fixture.TerminalAsync();
            var result = AiDurableInvocationResultMapper.Map(invocation);
            if (failure == "missing") result.InvocationReceipt = null;
            if (failure == "operation") result.InvocationReceipt = result.InvocationReceipt! with { OperationId = "wrong-operation" };
            if (failure == "digest") result.InvocationReceipt = result.InvocationReceipt! with { ResultSha256 = new string('e', 64) };
            if (failure == "outcome") result.Outcome = AiStepExecutionOutcome.Park;
            await fixture.SetStateAsync(AiStepExecutionStatus.Completed, result: result);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReconcileAsync());
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, (await fixture.Journal.GetAsync(Scope, Identity))!.ContinuationStatus);
            Assert.Empty(fixture.Controller.Requests);
        }

        [Fact]
        public async Task Archive_Result_Is_Read_Instead_Of_A_Lightweight_Terminal_Shell()
        {
            using var fixture = await CreateAsync();
            var invocation = await fixture.TerminalAsync();
            var record = (await fixture.Store.GetRecordAsync(Identity.ExecutionId))!;
            var state = (await fixture.Store.GetStateAsync(Identity.ExecutionId))!;
            state.Steps.Clear(); record.Status = AiExecutionStatus.Completed;
            fixture.Steps.Archived = new AiStepState { StepName = Identity.StepName, Status = AiStepExecutionStatus.Completed,
                Result = AiDurableInvocationResultMapper.Map(invocation) };
            await fixture.Store.CreateAsync(record, state);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Equal(1, fixture.Steps.FullReads);
        }

        [Fact]
        public async Task Crash_After_Scheduling_Recovers_From_Serialized_Journal_Without_New_Operation()
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal);
            fixture.Controller.Handler = _ => throw new IOException("Injected lost dispatch response.");
            await Assert.ThrowsAsync<IOException>(() => fixture.ReconcileAsync());
            var serialized = fixture.JournalStore.Export();
            var before = (await fixture.Journal.GetAsync(Scope, Identity))!;
            fixture.JournalStore.Restore(serialized);
            fixture.Journal = new AiDurableInvocationJournal(fixture.JournalStore, fixture.Clock);
            fixture.Controller.Handler = null; fixture.RebuildCoordinator();
            await fixture.ReconcileAsync();
            Assert.Equal(before.OperationId, (await fixture.Journal.GetAsync(Scope, Identity))!.OperationId);
            Assert.Equal(2, fixture.Controller.Requests.Count);
            Assert.Equal(fixture.Controller.Requests[0].RequestedSharedRunId, fixture.Controller.Requests[1].RequestedSharedRunId);
        }

        [Fact]
        public async Task Lost_Acknowledgement_Response_Is_Idempotent_Without_New_Dispatch()
        {
            using var fixture = await CreateAsync();
            var invocation = await fixture.TerminalAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.Completed, AiExecutionStatus.Completed, AiDurableInvocationResultMapper.Map(invocation));
            fixture.JournalStore.ThrowAfterNextWrite = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.ReconcileAsync());
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Empty(fixture.Controller.Requests);
        }

        [Fact]
        public async Task Invalid_Dispatch_Acceptance_Leaves_Scheduled_Intent_Intact()
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync(); await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal);
            fixture.Controller.Handler = request => Task.FromResult(new AiSharedRuntimeControllerResult
                { Operation = request.Operation, Success = true, SharedRunId = "other-run" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReconcileAsync());
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled, (await fixture.Journal.GetAsync(Scope, Identity))!.ContinuationStatus);
        }

        [Fact]
        public async Task Reconciliation_Restores_Ambient_Rbac_Context_After_Dispatch_Failure()
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync(); await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal);
            fixture.Controller.Handler = _ => throw new IOException("Injected dispatch error.");
            await fixture.WithIdentityAsync(async () =>
            {
                var previous = fixture.Accessor.Current;
                await Assert.ThrowsAsync<IOException>(() => fixture.ReconcileAsync());
                Assert.Same(previous, fixture.Accessor.Current);
                return true;
            });
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("control-plane")]
        [InlineData("generation")]
        public async Task Foreign_Scope_Or_Unimplemented_Generation_Cannot_Dispatch(string mismatch)
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync(); await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal);
            if (mismatch == "control-plane")
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ReconcileAsync(Scope with { ControlPlaneId = "other" }, Identity));
            else if (mismatch == "generation")
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ReconcileAsync(Scope, Identity with { Generation = 1 }));
            else
            {
                var record = (await fixture.Store.GetRecordAsync(Identity.ExecutionId))!;
                if (mismatch == "tenant") record.ExecutionContextSnapshot!.TenantId = "other";
                else record.ExecutionContextSnapshot!.TenantGroupId = "other";
                await fixture.Store.CreateAsync(record, (await fixture.Store.GetStateAsync(Identity.ExecutionId))!);
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReconcileAsync());
            }
            Assert.Empty(fixture.Controller.Requests);
        }
    }
}
