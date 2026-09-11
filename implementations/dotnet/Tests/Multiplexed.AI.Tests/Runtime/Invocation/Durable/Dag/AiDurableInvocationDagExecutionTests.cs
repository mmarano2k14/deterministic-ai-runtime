using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Steps;
using static Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag.DurableInvocationDagTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    /// <summary>Runs the existing local DAG runner and real external-wait resume; no worker process or network transport.</summary>
    public sealed class AiDurableInvocationDagExecutionTests
    {
        [Theory]
        [InlineData("python", true)]
        [InlineData("typescript", true)]
        [InlineData("dotnet", true)]
        [InlineData("python", false)]
        [InlineData("typescript", false)]
        [InlineData("dotnet", false)]
        public async Task Existing_Runner_Parks_Resumes_And_Persists_The_Exact_Result(string language, bool success)
        {
            using var fixture = await CreateAsync(language);
            await fixture.SetStateAsync(AiStepExecutionStatus.Ready);
            var parked = await fixture.RunNextAsync();
            Assert.Equal(AiExecutionStatus.Waiting, parked.Status);
            var waiting = (await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName];
            Assert.Equal(AiStepExecutionStatus.WaitingForExternal, waiting.Status);
            Assert.Null(waiting.ClaimToken);
            var invocation = await fixture.TerminalAsync(success);
            await fixture.ReconcileAsync();
            await fixture.Engine.ResumeExternalWaitingStepAsync(Identity.ExecutionId, Identity.StepName);
            var ready = (await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName];
            Assert.Equal(AiStepExecutionStatus.Ready, ready.Status);
            Assert.Equal(waiting.RecoveryCount, ready.RecoveryCount);
            Assert.Equal(waiting.RetryState?.RetryCount, ready.RetryState?.RetryCount);
            var terminal = await fixture.RunNextAsync();
            Assert.Equal(success ? AiExecutionStatus.Completed : AiExecutionStatus.Failed, terminal.Status);
            var persisted = (await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName];
            Assert.Equal(success ? AiStepExecutionStatus.Completed : AiStepExecutionStatus.Failed, persisted.Status);
            Assert.Equal(invocation.OperationId, persisted.Result!.InvocationReceipt!.OperationId);
            Assert.Equal(invocation.ResultSha256, persisted.Result.InvocationReceipt.ResultSha256);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Equal(1, fixture.Targets.Calls);
            Assert.Empty(await fixture.JournalStore.ListDispatchCandidatesAsync(Scope, language, fixture.Clock.GetUtcNow(), 100));
        }

        [Fact]
        public async Task Crash_After_Preparation_Before_Park_Does_Not_Reprepare_The_Business_Operation()
        {
            using var fixture = await CreateAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.Ready);
            fixture.FailPersistAfterExecution = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.RunNextAsync());
            var prepared = (await fixture.Journal.GetAsync(Scope, Identity))!;
            Assert.Equal(AiStepExecutionStatus.Ready, (await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName].Status);
            await fixture.TerminalAsync();
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Equal(AiExecutionStatus.Completed, (await fixture.RunNextAsync()).Status);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
            Assert.Equal(prepared.OperationId, (await fixture.Journal.GetAsync(Scope, Identity))!.OperationId);
            Assert.Equal(1, fixture.Targets.Calls);
        }

        [Fact]
        public async Task Duplicate_Resume_Does_Not_Repeat_The_Logical_Operation()
        {
            using var fixture = await CreateAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.Ready);
            await fixture.RunNextAsync();
            await fixture.TerminalAsync(); await fixture.ReconcileAsync();
            await fixture.Engine.ResumeExternalWaitingStepAsync(Identity.ExecutionId, Identity.StepName);
            await fixture.Engine.ResumeExternalWaitingStepAsync(Identity.ExecutionId, Identity.StepName);
            await fixture.RunNextAsync();
            await fixture.Engine.ResumeExternalWaitingStepAsync(Identity.ExecutionId, Identity.StepName);
            Assert.Equal(AiExecutionStatus.Completed, (await fixture.RunNextAsync()).Status);
            Assert.Equal(1, fixture.Targets.Calls);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, (await fixture.ReconcileAsync())!.ContinuationStatus);
        }
    }
}
