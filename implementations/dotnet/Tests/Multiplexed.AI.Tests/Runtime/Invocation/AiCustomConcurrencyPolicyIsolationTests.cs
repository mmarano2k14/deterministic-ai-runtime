using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.Invocation;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1cPolicyTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Explicit projection and isolation assertions, not worker sandbox proofs.</summary>
    public sealed class AiCustomConcurrencyPolicyIsolationTests
    {
        [Fact]
        public async Task Shared_Transport_And_Factory_Do_Not_Mix_Tenants_With_Identical_Policy_Names()
        {
            var transport = new Transport
            {
                Handler = async (request, _) =>
                {
                    await Task.Yield();
                    return Response(request, request.Context.TenantId == "tenant-a" ? "deny" : "allow");
                }
            };
            var sharedFactory = new AiConcurrencyPolicyAdapterFactory(new[] { transport });
            using var first = await CreateAsync(id: "run-a", tenant: "tenant-a", sharedFactory: sharedFactory);
            using var second = await CreateAsync(id: "run-b", tenant: "tenant-b", sharedFactory: sharedFactory);
            var results = await Task.WhenAll(first.AdmitAsync(), second.AdmitAsync());
            Assert.False(results[0].Allowed);
            Assert.True(results[1].Allowed);
            Assert.Equal(0, first.Gate.AcquireCalls);
            Assert.Equal(1, second.Gate.AcquireCalls);
            var requests = transport.Calls.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal("tenant-a", Assert.Single(requests.Where(x => x.Context.ExecutionId == "run-a")).Context.TenantId);
            Assert.Equal("tenant-b", Assert.Single(requests.Where(x => x.Context.ExecutionId == "run-b")).Context.TenantId);
            Assert.Equal(2, requests.Select(x => x.RequestId).Distinct().Count());
            Assert.All(requests, x => Assert.Equal("guard", x.PolicyName));
        }

        [Fact]
        public async Task Repeated_Evaluations_Get_Fresh_Request_Identity_Not_A_Cached_Result()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.Equal(2, transport.Calls.Select(x => x.RequestId).Distinct().Count());
        }

        [Fact]
        public async Task Pending_Request_Config_Is_Detached_From_Later_Dictionary_Mutation()
        {
            var config = new Dictionary<string, object?> { ["limit"] = 7, ["nested"] = new List<object?> { "first", 2, true, null } };
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport { Handler = (_, _) => completion.Task };
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, config: TypedConfig(Policy(config: config))), new[] { transport });
            var evaluation = fixture.DecideAsync();
            var request = await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            config["limit"] = 999;
            ((List<object?>)config["nested"]!)[0] = "changed";
            Assert.Equal(7, request.Config.GetProperty("limit").GetInt32());
            Assert.Equal("first", request.Config.GetProperty("nested")[0].GetString());
            completion.SetResult(Response(request));
            Assert.True((await evaluation).Allowed);
        }

        [Fact]
        public async Task Request_Contains_Only_The_Explicit_Projection_Not_Execution_State_Or_Credentials()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            fixture.Execution.State.Data["serverCredential"] = "never-serialize-this-value";
            fixture.Execution.Record.ExecutionContextSnapshot!.UserId = "private-user-not-an-input";
            await fixture.DecideAsync();
            var json = JsonSerializer.Serialize(Assert.Single(transport.Calls));
            Assert.DoesNotContain("never-serialize-this-value", json);
            Assert.DoesNotContain("private-user-not-an-input", json);
            using var document = JsonDocument.Parse(json);
            var input = document.RootElement.GetProperty("context");
            Assert.Equal(new[] { "executionId", "model", "operation", "pipelineKey", "provider", "runtimeInstanceId", "stepKey", "stepName", "tenantGroupId", "tenantId" },
                input.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal("concurrency", document.RootElement.GetProperty("policyKind").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        [Fact]
        public async Task Arbitrary_Clr_Config_Is_Rejected_Without_Invoking_Its_Property_Getters()
        {
            var poison = new Poison();
            var transport = new Transport();
            using var fixture = await CreateAsync(CreatePipeline(new[] { Native() },
                config: TypedConfig(Policy(config: new Dictionary<string, object?> { ["value"] = poison }))), new[] { transport });
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.DecideAsync());
            Assert.Equal(0, poison.Reads);
            Assert.Empty(transport.Calls);
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
        }

        [Theory]
        [InlineData("bytes")]
        [InlineData("depth")]
        [InlineData("nonfinite")]
        [InlineData("undefined")]
        public async Task Oversized_Or_Nonportable_Config_Fails_Before_Transport(string scenario)
        {
            object? value = scenario == "bytes" ? new string('x', 70000)
                : scenario == "nonfinite" ? double.NaN : default(JsonElement);
            if (scenario == "depth")
            {
                value = "leaf";
                for (var index = 0; index < 40; index++) value = new Dictionary<string, object?> { ["nested"] = value };
            }
            var transport = new Transport();
            using var fixture = await CreateAsync(CreatePipeline(new[] { Native() },
                config: TypedConfig(Policy(config: new Dictionary<string, object?> { ["value"] = value }))), new[] { transport });
            if (scenario == "nonfinite") await Assert.ThrowsAsync<NotSupportedException>(() => fixture.DecideAsync());
            else await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DecideAsync());
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task A_Later_Policy_Failure_Cannot_Reuse_An_Earlier_Approval()
        {
            var transport = new Transport
            {
                Handler = (request, _) => Task.FromResult(request.PolicyName == "first" ? Response(request) : Json("{}"))
            };
            using var fixture = await CreateAsync(CreatePipeline(new[] { Native() }, config: Config(Policy("first"), Policy("second"))), new[] { transport });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AdmitAsync());
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            Assert.Equal(2, transport.Calls.Count);
            var failed = Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
            Assert.Equal("second", failed.Properties[AiPolicyMetadataKeys.Name]);
        }

        [Fact]
        public async Task One_Adapter_Cannot_Be_Reused_For_A_Different_Evaluation()
        {
            using var fixture = await CreateAsync();
            var factory = (AiConcurrencyPolicyAdapterFactory)fixture.Provider.GetService(typeof(AiConcurrencyPolicyAdapterFactory))!;
            var adapter = factory.Bind(fixture.StepContext, fixture.Effective.Policies[0], fixture.StepContext.ConcurrencyPolicyBindings[0]);
            var context = new AiConcurrencyPolicyContext { Concurrency = fixture.Admission.Context };
            Assert.True((await adapter.ExecuteAsync(context)).IsSuccess);
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(context));
        }

        [Fact]
        public async Task Adapter_Rejects_A_Context_For_Another_Execution()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            fixture.Admission.Context.ExecutionId = "other-run";
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DecideAsync());
            Assert.Empty(transport.Calls);
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
        }

        [Fact]
        public async Task Factory_Copies_The_Capability_Collection()
        {
            var original = new Transport();
            var transports = new List<IAiConcurrencyPolicyTransport> { original };
            var factory = new AiConcurrencyPolicyAdapterFactory(transports);
            transports.Clear(); transports.Add(new Transport("typescript"));
            using var fixture = await CreateAsync(sharedFactory: factory);
            Assert.True((await fixture.DecideAsync()).Allowed);
            Assert.Single(original.Calls);
        }

        [Fact]
        public async Task Altered_Inherited_Step_Language_Is_Refused_Not_Reinterpreted()
        {
            var transport = new Transport("typescript");
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Custom(language: "typescript", config: Config(Policy())) }), new[] { transport });
            var factory = (AiConcurrencyPolicyAdapterFactory)fixture.Provider.GetService(typeof(AiConcurrencyPolicyAdapterFactory))!;
            var binding = Assert.Single(fixture.StepContext.ConcurrencyPolicyBindings);
            var altered = binding with { Invocation = binding.Invocation with { ExecutionLanguage = "python" } };
            Assert.Throws<InvalidOperationException>(() => factory.Bind(fixture.StepContext, fixture.Effective.Policies[0], altered));
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task Custom_Binding_Cannot_Run_Outside_Dag_Admission()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            var factory = (AiConcurrencyPolicyAdapterFactory)fixture.Provider.GetService(typeof(AiConcurrencyPolicyAdapterFactory))!;
            var executionOnly = new AiStepExecutionContext(fixture.Execution, fixture.Plan.Steps[0]);
            Assert.Throws<NotSupportedException>(() => factory.Bind(executionOnly,
                fixture.Effective.Policies[0], fixture.StepContext.ConcurrencyPolicyBindings[0]));
            fixture.Execution.Record.ExecutionMode = AiExecutionMode.Sequential;
            Assert.Throws<NotSupportedException>(() => factory.Bind(fixture.StepContext,
                fixture.Effective.Policies[0], fixture.StepContext.ConcurrencyPolicyBindings[0]));
            Assert.Empty(transport.Calls);
        }

        private sealed class Poison
        {
            public int Reads;
            public string Secret { get { Reads++; throw new InvalidOperationException("Must not reflect over this object."); } }
        }
    }
}
