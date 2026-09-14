using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Existing adapter, restored identity and RBAC with detached effect metadata.</summary>
    public sealed class AiMcpEffectAdapterTests
    {
        [Fact]
        public async Task Physical_Claim_Replacement_Preserves_Effect_But_Not_Attempt_Id()
        {
            using var fixture = await McpStepTestSupport.CreateAsync();
            var stepState = fixture.Context.StepState;
            stepState.ClaimTimeoutSeconds = 30;
            stepState.RetryState = new() { RetryCount = 2 };
            var retryState = stepState.RetryState;
            var retryCount = retryState.RetryCount;
            var recoveryCount = stepState.RecoveryCount;

            stepState.MarkReady();
            stepState.MarkRunning("runtime-a", "claim-a");
            await fixture.InvokeAsync();
            var firstExecutionStepKey = fixture.Context.Record.ExecutionStepKey;
            var firstStartedAtUtc = stepState.StartedAtUtc;

            // The fixture calls the adapter, not the runner that applies its result.
            Assert.Equal(AiStepExecutionStatus.Running, stepState.Status);
            Assert.Null(stepState.Result);
            Assert.NotNull(stepState.LeaseExpiresAtUtc);

            // Model an already-authorized infrastructure recovery before a new claim.
            // This transition-level test does not decide real lease expiry or replay safety.
            stepState.MarkRequeuedAfterTimeout();
            Assert.Equal(AiStepExecutionStatus.Ready, stepState.Status);
            Assert.Null(stepState.ClaimedBy);
            Assert.Null(stepState.ClaimToken);
            Assert.Null(stepState.ClaimedAtUtc);
            Assert.Null(stepState.LeaseExpiresAtUtc);
            Assert.Equal(recoveryCount + 1, stepState.RecoveryCount);
            Assert.Same(retryState, stepState.RetryState);
            Assert.Equal(retryCount, stepState.RetryState!.RetryCount);
            Assert.Equal(firstStartedAtUtc, stepState.StartedAtUtc);

            fixture.Context.Record.RenewExecutionStepKey();
            Assert.NotEqual(firstExecutionStepKey, fixture.Context.Record.ExecutionStepKey);
            stepState.MarkRunning("runtime-b", "claim-b");
            Assert.Equal(AiStepExecutionStatus.Running, stepState.Status);
            Assert.Equal("runtime-b", stepState.ClaimedBy);
            Assert.Equal("claim-b", stepState.ClaimToken);
            Assert.Equal(firstStartedAtUtc, stepState.StartedAtUtc);
            Assert.Same(retryState, stepState.RetryState);
            Assert.Equal(retryCount, stepState.RetryState!.RetryCount);
            await fixture.InvokeAsync();

            var calls = fixture.Transport.Calls.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(2, calls[0].SchemaVersion);
            Assert.NotNull(calls[0].Effect);
            Assert.Equal(calls[0].Effect, calls[1].Effect);
            Assert.NotEqual(calls[0].RequestId, calls[1].RequestId);
            foreach (var call in calls) AiMcpEffectIdentities.ValidateRequest(call);
        }

        [Fact]
        public async Task Execution_Key_Renewal_Does_Not_Replace_A_Running_Claim()
        {
            using var fixture = await McpStepTestSupport.CreateAsync();
            var stepState = fixture.Context.StepState;
            stepState.ClaimTimeoutSeconds = 30;
            stepState.MarkReady();
            stepState.MarkRunning("runtime-a", "claim-a");
            await fixture.InvokeAsync();

            var previousExecutionStepKey = fixture.Context.Record.ExecutionStepKey;
            var previousVersion = stepState.Version;
            var previousClaimedAtUtc = stepState.ClaimedAtUtc;
            var previousLeaseExpiresAtUtc = stepState.LeaseExpiresAtUtc;
            var previousRecoveryCount = stepState.RecoveryCount;
            fixture.Context.Record.RenewExecutionStepKey();
            Assert.NotEqual(previousExecutionStepKey, fixture.Context.Record.ExecutionStepKey);

            Assert.Throws<InvalidOperationException>(() => stepState.MarkRunning("runtime-b", "claim-b"));

            Assert.Equal(AiStepExecutionStatus.Running, stepState.Status);
            Assert.Equal("runtime-a", stepState.ClaimedBy);
            Assert.Equal("claim-a", stepState.ClaimToken);
            Assert.Equal(previousVersion, stepState.Version);
            Assert.Equal(previousClaimedAtUtc, stepState.ClaimedAtUtc);
            Assert.Equal(previousLeaseExpiresAtUtc, stepState.LeaseExpiresAtUtc);
            Assert.Equal(previousRecoveryCount, stepState.RecoveryCount);
            Assert.Single(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Reconstructed_Adapter_Derives_The_Same_Effect_Without_A_Cache()
        {
            string saved;
            using (var original = await McpStepTestSupport.CreateAsync())
            { await original.InvokeAsync(); saved = JsonSerializer.Serialize(Assert.Single(original.Transport.Calls)); }
            using var restored = await McpStepTestSupport.CreateAsync();
            await restored.InvokeAsync();
            var before = JsonSerializer.Deserialize<AiMcpToolRequest>(saved)!;
            var after = Assert.Single(restored.Transport.Calls);
            Assert.Equal(before.Effect, after.Effect);
            Assert.NotEqual(before.RequestId, after.RequestId);
        }

        [Fact]
        public async Task Changed_Resolved_Inputs_Change_Intent_Not_The_Logical_Effect()
        {
            using var fixture = await McpStepTestSupport.CreateAsync(McpStepTestSupport.Pipeline(
                McpStepTestSupport.Step(input: new Dictionary<string, object?> { ["value"] = "state.amount" })));
            fixture.Context.State.Data["amount"] = 1;
            await fixture.InvokeAsync();
            fixture.Context.State.Data["amount"] = 2;
            await fixture.InvokeAsync();
            var calls = fixture.Transport.Calls.ToArray();
            Assert.Equal(calls[0].Effect!.EffectId, calls[1].Effect!.EffectId);
            Assert.NotEqual(calls[0].Effect!.RequestDigest, calls[1].Effect!.RequestDigest);
            Assert.Equal(2, calls.Length); // Metadata alone deliberately does not claim durable deduplication.
        }

        [Fact]
        public async Task Connection_Revision_Participates_In_Intent_Without_A_Second_Revision_Field()
        {
            var revision = "connection/v1";
            var resolver = new McpStepTestSupport.Resolver
            {
                Handler = (request, _) => Task.FromResult<AiMcpToolBinding?>(
                    McpStepTestSupport.Target(request) with { ConnectionRevision = revision })
            };
            using var fixture = await McpStepTestSupport.CreateAsync(resolver: resolver);
            await fixture.InvokeAsync(); revision = "connection/v2"; await fixture.InvokeAsync();
            var calls = fixture.Transport.Calls.ToArray();
            Assert.Equal(calls[0].Effect!.EffectId, calls[1].Effect!.EffectId);
            Assert.NotEqual(calls[0].Effect!.RequestDigest, calls[1].Effect!.RequestDigest);
            Assert.Equal("connection/v2", calls[1].ConnectionRevision);
            using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(calls[1].Effect));
            Assert.Equal(3, metadata.RootElement.EnumerateObject().Count());
        }

        [Fact]
        public async Task Tenant_Arguments_Cannot_Replace_Server_Effect_Fields()
        {
            using var fixture = await McpStepTestSupport.CreateAsync(McpStepTestSupport.Pipeline(
                McpStepTestSupport.Step(input: new Dictionary<string, object?>
                { ["effectId"] = "forged-effect", ["requestDigest"] = "forged-digest", ["tenantId"] = "admin" })));
            await fixture.InvokeAsync();
            var call = Assert.Single(fixture.Transport.Calls);
            Assert.Equal("tenant-1", call.Context.TenantId);
            Assert.NotEqual("forged-effect", call.Effect!.EffectId);
            Assert.NotEqual("forged-digest", call.Effect.RequestDigest);
            AiMcpEffectIdentities.ValidateRequest(call);
        }

        [Fact]
        public async Task Existing_Rbac_Denial_Precedes_Argument_Resolution_And_Effect_Transport()
        {
            using var fixture = await McpStepTestSupport.CreateAsync(grant: string.Empty);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InvokeAsync());
            Assert.Equal(0, fixture.Payloads.Calls);
            Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData("tenant-2", "execution-1", "publish")]
        [InlineData("tenant-1", "execution-2", "publish")]
        [InlineData("tenant-1", "execution-1", "publish-again")]
        public async Task A_New_Execution_Or_Call_Site_Is_An_Explicit_New_Effect(string tenant, string execution, string step)
        {
            using var first = await McpStepTestSupport.CreateAsync();
            using var second = await McpStepTestSupport.CreateAsync(McpStepTestSupport.Pipeline(
                McpStepTestSupport.Step(name: step)), tenant: tenant, id: execution);
            await first.InvokeAsync(); await second.InvokeAsync();
            Assert.NotEqual(Assert.Single(first.Transport.Calls).Effect!.EffectId,
                Assert.Single(second.Transport.Calls).Effect!.EffectId);
        }
    }
}
