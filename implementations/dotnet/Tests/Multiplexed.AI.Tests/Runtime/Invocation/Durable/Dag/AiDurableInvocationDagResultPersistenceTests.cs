using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using static Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag.DurableInvocationDagTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    public sealed class AiDurableInvocationDagResultPersistenceTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Result_Receipt_Roundtrips_With_Json_And_Defensive_Memory_Clones(bool success)
        {
            using var fixture = await CreateAsync();
            var invocation = await fixture.TerminalAsync(success);
            var result = AiDurableInvocationResultMapper.Map(invocation);
            result.Payload = AiStoredPayload.Artifact("value-artifact");
            result.DataPayloads = new() { ["report"] = AiStoredPayload.Artifact("data-artifact") };
            var decoded = JsonSerializer.Deserialize<AiStepResult>(JsonSerializer.Serialize(result))!;
            Assert.Equal(result.InvocationReceipt, decoded.InvocationReceipt);
            await fixture.SetStateAsync(success ? AiStepExecutionStatus.Completed : AiStepExecutionStatus.Failed, result: result);
            var loaded = (await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName].Result!;
            Assert.Equal(result.Outcome, loaded.Outcome);
            Assert.Equal(result.InvocationReceipt, loaded.InvocationReceipt);
            Assert.NotNull(loaded.Value);
            Assert.Equal("value-artifact", loaded.Payload!.ArtifactId);
            Assert.Equal("data-artifact", loaded.DataPayloads!["report"].ArtifactId);
            loaded.InvocationReceipt = null;
            Assert.NotNull((await fixture.Store.GetStateAsync(Identity.ExecutionId))!.Steps[Identity.StepName].Result!.InvocationReceipt);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Historical_Native_Results_Do_Not_Emit_Invocation_Receipt(bool success)
        {
            var result = success ? AiStepResult.Ok(42) : AiStepResult.Fail("native error");
            Assert.DoesNotContain("InvocationReceipt", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }
}
