using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1cPolicyTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Real family engine and admission boundary, substituted transport only.</summary>
    public sealed class AiCustomConcurrencyPolicyTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Three_Languages_Reach_The_Installed_Transport_Without_Executing_A_Step(string language)
        {
            var transport = new Transport(language);
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, language, Config(Policy())), new[] { transport });
            Assert.True((await fixture.AdmitAsync()).Allowed);
            Assert.Equal(1, fixture.Gate.AcquireCalls);
            Assert.Empty(fixture.StepRegistry.Implementation.Calls);
            var request = Assert.Single(transport.Calls);
            Assert.Equal(language, request.ExecutionLanguage);
            Assert.Equal("tenant-1", request.Context.TenantId);
            Assert.Equal("execution-1", request.Context.ExecutionId);
            Assert.Equal("runtime-1", request.Context.RuntimeInstanceId);
            Assert.Equal("concurrency", request.PolicyKind);
            Assert.Equal(1, request.SchemaVersion);
        }

        [Theory]
        [InlineData("pipeline", "custom", "typescript", null, "python")]
        [InlineData("step", "custom", "typescript", null, "typescript")]
        [InlineData("step", "custom", "typescript", "dotnet", "dotnet")]
        [InlineData("step", "native", null, null, "python")]
        [InlineData("step", "mcp", null, null, "python")]
        [InlineData("pipeline", "native", null, "typescript", "typescript")]
        public async Task Evaluation_Uses_Compiled_Scope_And_Overrides(
            string scope, string stepKind, string? stepLanguage, string? policyLanguage, string expectedLanguage)
        {
            var policyConfig = Config(Policy(language: policyLanguage));
            var localConfig = scope == "step" ? policyConfig : null;
            var source = stepKind == "custom" ? Custom(language: stepLanguage, config: localConfig)
                : stepKind == "mcp" ? Mcp(config: localConfig) : Native(config: localConfig);
            var transports = new[] { new Transport("python"), new Transport("typescript"), new Transport("dotnet") };
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { source }, config: scope == "pipeline" ? policyConfig : null), transports);
            Assert.True((await fixture.AdmitAsync()).Allowed);
            var request = Assert.Single(transports.SelectMany(x => x.Calls));
            Assert.Equal(expectedLanguage, request.ExecutionLanguage);
            Assert.Equal(scope == "step" ? "Step" : "Pipeline", request.Scope);
            Assert.Equal(scope == "step" ? source.Name : null, request.OwnerStepName);
        }

        [Fact]
        public async Task Explicit_Policy_Override_Works_Without_A_Pipeline_Default()
        {
            var transport = new Transport("typescript");
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, null, Config(Policy(language: "typescript"))), new[] { transport });
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.Equal("typescript", Assert.Single(transport.Calls).ExecutionLanguage);
        }

        [Theory]
        [InlineData("allow", true)]
        [InlineData("deny", false)]
        public async Task Business_Decision_Uses_Existing_Events_And_Precedes_The_Gate(string decision, bool expected)
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Response(request, decision, decision == "deny" ? 1200 : null)) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            var result = await fixture.AdmitAsync();
            Assert.Equal(expected, result.Allowed);
            Assert.Equal(expected ? 1 : 0, fixture.Gate.AcquireCalls);
            if (!expected) Assert.Equal(TimeSpan.FromMilliseconds(1200), result.RetryAfter);
            var policyEvents = fixture.Observer.Events.Where(x => x.SemanticEventType?.StartsWith("policy.") == true).ToArray();
            Assert.Equal(2, policyEvents.Length);
            Assert.Equal(AiEngineEvents.Policy.Evaluated, policyEvents[0].SemanticEventType);
            Assert.Equal(expected ? AiEngineEvents.Policy.Allowed : AiEngineEvents.Policy.Denied, policyEvents[1].SemanticEventType);
            Assert.Equal(expected ? AiControlPlaneOperationOutcome.Succeeded : AiControlPlaneOperationOutcome.Denied, policyEvents[1].Outcome);
            Assert.All(policyEvents, item =>
            {
                Assert.Equal("guard", item.Properties[AiPolicyMetadataKeys.Name]);
                Assert.Equal("python", item.Properties[AiPolicyInvocationMetadataKeys.ExecutionLanguage]);
                Assert.Equal(transport.Calls.Single().RequestId, item.Properties[AiPolicyInvocationMetadataKeys.RequestId]);
            });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(300000)]
        public async Task Validated_Retry_Delay_Boundaries_Are_Preserved(int milliseconds)
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Response(request, "deny", milliseconds)) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            var decision = await fixture.DecideAsync();
            Assert.False(decision.Allowed);
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), decision.RetryAfter);
        }

        [Fact]
        public async Task Denial_Without_Delay_Preserves_The_Configured_Default()
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Response(request, "deny")) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            Assert.Equal(TimeSpan.FromMilliseconds(fixture.Effective.DefaultRetryAfterMs), (await fixture.DecideAsync()).RetryAfter);
        }

        [Fact]
        public async Task Custom_Declaration_Does_Not_Use_A_Homonymous_Native_Policy()
        {
            var native = new ProbePolicy("guard", block: true);
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport }, natives: new[] { native });
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.Empty(native.Calls);
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task Mixed_Native_And_Repeated_Custom_Names_Preserve_Order_And_Own_Config()
        {
            var native = new ProbePolicy("native");
            var transport = new Transport();
            var config = Config(
                Policy("same", config: new Dictionary<string, object?> { ["position"] = 1 }),
                new AiConfiguredPolicyDefinition { Name = "native" },
                Policy("same", config: new Dictionary<string, object?> { ["position"] = 2 }));
            using var fixture = await CreateAsync(CreatePipeline(new[] { Native() }, config: config), new[] { transport }, natives: new[] { native });
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.Equal(new[] { 1, 2 }, transport.Calls.Select(x => x.Config.GetProperty("position").GetInt32()));
            var names = fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Evaluated)
                .Select(x => x.Properties[AiPolicyMetadataKeys.Name]?.ToString());
            Assert.Equal(new[] { "same", nameof(ProbePolicy), "same" }, names);
            Assert.Equal(2, transport.Calls.Select(x => x.RequestId).Distinct().Count());
            Assert.Single(native.Calls);
        }

        [Theory]
        [InlineData("allow")]
        [InlineData("deny")]
        public async Task Native_Block_Is_Not_Overridden_By_Custom_Allow_Or_A_Shorter_Custom_Delay(string decisionKind)
        {
            var native = new ProbePolicy("native", block: true);
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Response(request, decisionKind, decisionKind == "deny" ? 1 : null)) };
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, config: Config(new AiConfiguredPolicyDefinition { Name = "native" }, Policy())),
                new[] { transport }, natives: new[] { native });
            var decision = await fixture.DecideAsync();
            Assert.False(decision.Allowed);
            Assert.Equal(TimeSpan.FromMilliseconds(fixture.Effective.DefaultRetryAfterMs), decision.RetryAfter);
        }

        [Fact]
        public async Task Json_Roundtrip_Preserves_The_Declaration_Used_By_The_Transport()
        {
            var definition = CreatePipeline(new[] { Native(config: Config(Policy("roundtrip", "typescript"))) });
            var restored = JsonSerializer.Deserialize<AiPipelineDefinition>(JsonSerializer.Serialize(definition))!;
            var transport = new Transport("typescript");
            using var fixture = await CreateAsync(restored, new[] { transport });
            Assert.True((await fixture.DecideAsync()).Allowed);
            var request = Assert.Single(transport.Calls);
            Assert.Equal("roundtrip", request.PolicyName);
            Assert.Equal("Step", request.Scope);
            Assert.Equal("publication/roundtrip/v1", request.ImplementationRef);
        }

        [Fact]
        public async Task Native_Fast_Path_Works_Without_Custom_Factory_And_Keeps_Clr_Identity()
        {
            var native = new ProbePolicy("native");
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, config: Config(new AiConfiguredPolicyDefinition { Name = "native" })),
                installFactory: false, natives: new[] { native });
            Assert.True((await fixture.AdmitAsync()).Allowed);
            Assert.Single(native.Calls);
            var started = Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Evaluated));
            Assert.Equal(nameof(ProbePolicy), started.Properties[AiPolicyMetadataKeys.Name]);
            Assert.False(started.Properties.ContainsKey(AiPolicyInvocationMetadataKeys.RequestId));
        }

        [Fact]
        public void Typed_Block_Preserves_Outcome_And_Legacy_Overload()
        {
            var outcome = new AiConcurrencyPolicyOutcome { IsAllowed = false, RetryAfter = TimeSpan.FromSeconds(2) };
            var result = AiPolicyResult.Block(outcome, "denied");
            Assert.Equal(AiPolicyResultKind.Block, result.Kind);
            Assert.Same(outcome, result.Data);
            Assert.Null(AiPolicyResult.Block<AiConcurrencyPolicyOutcome>("legacy").Data);
            Assert.Equal(AiPolicyResultKind.Block, AiPolicyResult.Block("legacy").Kind);
        }
    }
}
