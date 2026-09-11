using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using static Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag.DurableInvocationDagTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    public sealed class AiDurableInvocationDagAdapterTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Resolution_Is_Pure_And_Preparation_Precedes_Park(string language)
        {
            using var fixture = await CreateAsync(language);
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
            Assert.Equal(0, fixture.Targets.Calls);
            var result = await fixture.InvokeAsync();
            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            var prepared = (await fixture.Journal.GetAsync(Scope, Identity))!;
            Assert.Equal(AiDurableInvocationStatus.Prepared, prepared.Status);
            Assert.Equal(language, prepared.Definition.Target.ExecutionLanguage);
            Assert.Equal("{\"amount\":1}", prepared.Definition.InputsJson);
            Assert.Null(result.InvocationReceipt);
            Assert.Empty(fixture.Controller.Requests);
        }

        [Fact]
        public async Task Reentry_Reads_Frozen_Preparation_Without_Rebinding_Or_Reevaluating_Inputs()
        {
            using var fixture = await CreateAsync();
            await fixture.InvokeAsync();
            var before = (await fixture.Journal.GetAsync(Scope, Identity))!;
            fixture.Targets.Handler = _ => throw new InvalidOperationException("Latest publication must not be resolved.");
            fixture.Context.StepState.Inputs["amount"] = 900;
            var after = await fixture.InvokeAsync();
            Assert.Equal(AiStepExecutionOutcome.Park, after.EffectiveOutcome);
            Assert.Equal(before, await fixture.Journal.GetAsync(Scope, Identity));
            Assert.Equal(1, fixture.Targets.Calls);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Terminal_Result_Returns_Business_Value_And_Server_Receipt_Without_Acknowledgement(bool success)
        {
            using var fixture = await CreateAsync();
            var saved = await fixture.TerminalAsync(success);
            var result = await fixture.InvokeAsync();
            Assert.Equal(success, result.Success);
            Assert.Equal(success ? AiStepExecutionOutcome.Complete : AiStepExecutionOutcome.Fail, result.EffectiveOutcome);
            Assert.Equal(42, Assert.IsType<JsonElement>(result.Value).GetProperty("value").GetInt32());
            Assert.Equal(saved.OperationId, result.InvocationReceipt!.OperationId);
            Assert.Equal(saved.ResultSha256, result.InvocationReceipt.ResultSha256);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending,
                (await fixture.Journal.GetAsync(Scope, Identity))!.ContinuationStatus);
        }

        [Fact]
        public async Task Worker_Payload_Cannot_Select_Park_Or_Forge_The_Server_Receipt()
        {
            using var fixture = await CreateAsync();
            var record = await fixture.TerminalAsync(json:
                "{\"Outcome\":\"Park\",\"InvocationReceipt\":{\"OperationId\":\"forged\"},\"permissions\":[\"*\"]}");
            var result = await fixture.InvokeAsync();
            Assert.Equal(AiStepExecutionOutcome.Complete, result.EffectiveOutcome);
            Assert.Equal(record.OperationId, result.InvocationReceipt!.OperationId);
            Assert.Empty(result.Data);
            Assert.Null(result.DataPayloads);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("corrupted")]
        [InlineData("changed-version")]
        public async Task Invalid_Pinned_Definition_Is_Rejected_Before_Preparation(string failure)
        {
            using var fixture = await CreateAsync();
            fixture.Context.Record.PipelineDefinitionSnapshot = failure switch
            {
                "missing" => null,
                "corrupted" => Multiplexed.Abstractions.AI.Execution.Payloads.Models.AiStoredPayload.Inline("{}", contentHash: new string('a', 64)),
                _ => Snapshot(new Multiplexed.Abstractions.AI.Pipeline.AiPipelineDefinition
                { Name = "analysis", Version = "2", ExecutionMode = AiExecutionMode.Dag, ExecutionLanguage = "python", Steps = Definition().Steps })
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
            Assert.Equal(0, fixture.Targets.Calls);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("definition")]
        [InlineData("language")]
        [InlineData("implementation")]
        public async Task Missing_Or_Incompatible_Publication_Material_Is_Fail_Closed(string failure)
        {
            using var fixture = await CreateAsync();
            fixture.Targets.Handler = request => failure switch
            {
                "missing" => null,
                "definition" => TargetResolver.Target(request) with { DefinitionSha256 = new string('e', 64) },
                "language" => TargetResolver.Target(request) with { ExecutionLanguage = "typescript" },
                _ => TargetResolver.Target(request) with { ImplementationRef = "other-code" }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("user")]
        [InlineData("namespace")]
        public async Task Restored_Rbac_Identity_Must_Match_The_Persisted_Execution(string mismatch)
        {
            using var fixture = await CreateAsync();
            var live = ExecutionContextSnapshotMapper.ToExecutionContext(fixture.Context.Record.ExecutionContextSnapshot!);
            switch (mismatch)
            {
                case "tenant": live.TenantId = "tenant-b"; break;
                case "group": live.TenantGroupId = "group-b"; break;
                case "user": live.UserId = "user-b"; break;
                default: live.CurrentNamespace = "other"; break;
            }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.WithIdentityAsync(
                () => fixture.Context.Step.Step.ExecuteAsync(fixture.Context), live));
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
        }

        [Theory]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        public async Task Unclaimed_Contexts_Cannot_Prepare_Work(AiStepExecutionStatus status)
        {
            using var fixture = await CreateAsync();
            fixture.Context.StepState.Status = status;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Equal(0, fixture.Targets.Calls);
        }

        [Fact]
        public async Task Admission_Cannot_Prepare_Work()
        {
            using var fixture = await CreateAsync();
            var admission = new AiStepExecutionContext(fixture.Context.Execution, fixture.Context.Step)
                { ConcurrencyAdmissionDefinition = new AiConcurrencyDefinition() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WithIdentityAsync(
                () => fixture.Context.Step.Step.ExecuteAsync(admission)));
            Assert.Equal(0, fixture.Targets.Calls);
        }

        [Fact]
        public async Task Sequential_Invocation_Is_Refused_Before_Journal_Write()
        {
            using var fixture = await CreateAsync();
            fixture.Context.Record.ExecutionMode = AiExecutionMode.Sequential;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
        }

        [Fact]
        public async Task Cancellation_Does_Not_Create_A_New_Operation()
        {
            using var fixture = await CreateAsync();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.InvokeAsync(cancellation.Token));
            Assert.Null(await fixture.Journal.GetAsync(Scope, Identity));
        }

        [Fact]
        public async Task Lost_Preparation_Response_Reenters_The_Same_Operation()
        {
            using var fixture = await CreateAsync();
            fixture.JournalStore.ThrowAfterNextWrite = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.InvokeAsync());
            var saved = (await fixture.Journal.GetAsync(Scope, Identity))!;
            Assert.Equal(AiStepExecutionOutcome.Park, (await fixture.InvokeAsync()).EffectiveOutcome);
            Assert.Equal(saved, await fixture.Journal.GetAsync(Scope, Identity));
            Assert.Equal(1, fixture.Targets.Calls);
        }
    }
}
