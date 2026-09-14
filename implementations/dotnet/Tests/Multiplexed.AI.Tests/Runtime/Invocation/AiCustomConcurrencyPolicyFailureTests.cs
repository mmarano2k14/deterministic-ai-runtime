using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1cPolicyTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Malformed and failed evaluations never become an implicit admission.</summary>
    public sealed class AiCustomConcurrencyPolicyFailureTests
    {
        [Theory]
        [InlineData("null")]
        [InlineData("true")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"allowed\":true}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":true}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"validation\",\"decision\":\"allow\"}")]
        [InlineData("{\"schemaVersion\":2,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"allow\"}")]
        [InlineData("{\"schemaVersion\":\"1\",\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"allow\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"old-request\",\"policyKind\":\"concurrency\",\"decision\":\"allow\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"Allow\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"allow\",\"decision\":\"deny\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"allow\",\"unknown\":1}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\",\"reason\":\" \"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"allow\",\"retryAfterMs\":1}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\",\"reason\":\"busy\",\"retryAfterMs\":-1}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\",\"reason\":\"busy\",\"retryAfterMs\":300001}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\",\"reason\":\"busy\",\"retryAfterMs\":1.5}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"$id\",\"policyKind\":\"concurrency\",\"decision\":\"deny\",\"reason\":false}")]
        public async Task Invalid_Response_Emits_Failed_And_Never_Reaches_Gate(string response)
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Json(response.Replace("$id", request.RequestId))) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AdmitAsync());
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            Assert.Empty(fixture.StepRegistry.Implementation.Calls);
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
            Assert.DoesNotContain(fixture.Observer.Events, x => x.SemanticEventType == AiEngineEvents.Policy.Allowed || x.SemanticEventType == AiEngineEvents.Policy.Denied);
        }

        [Fact]
        public async Task Oversized_Response_Reason_Is_A_Technical_Failure()
        {
            var transport = new Transport
            {
                Handler = (request, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1, requestId = request.RequestId, policyKind = "concurrency",
                    decision = "deny", reason = new string('x', 2049)
                }))
            };
            using var fixture = await CreateAsync(transports: new[] { transport });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AdmitAsync());
            Assert.Equal(0, fixture.Gate.AcquireCalls);
        }

        [Fact]
        public async Task Explicitly_Wrong_Custom_Policy_Family_Is_Refused()
        {
            var policy = Policy(); policy.Kind = "Validation";
            var transport = new Transport();
            using var fixture = await CreateAsync(CreatePipeline(new[] { Native() }, config: Config(policy)), new[] { transport });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AdmitAsync());
            Assert.Empty(transport.Calls);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
        }

        [Fact]
        public async Task Undefined_Json_Is_A_Technical_Failure()
        {
            var transport = new Transport { Handler = (_, _) => Task.FromResult(default(JsonElement)) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DecideAsync());
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
        }

        [Fact]
        public async Task Transport_Exception_Remains_Failed_Not_Denied_And_Is_Not_Retried()
        {
            var failure = new IOException("transport failure");
            var transport = new Transport { Handler = (_, _) => Task.FromException<JsonElement>(failure) };
            using var fixture = await CreateAsync(transports: new[] { transport });
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.AdmitAsync()));
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            Assert.Single(transport.Calls);
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
            Assert.DoesNotContain(fixture.Observer.Events, x => x.SemanticEventType == AiEngineEvents.Policy.Denied);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Missing_Factory_Or_Language_Does_Not_Fall_Back_To_Native(bool installFactory)
        {
            var native = new ProbePolicy();
            using var fixture = await CreateAsync(transports: Array.Empty<IAiConcurrencyPolicyTransport>(),
                installFactory: installFactory, natives: new[] { native });
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.AdmitAsync());
            Assert.Empty(native.Calls);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
        }

        [Fact]
        public async Task Deadline_Closes_Admission_Even_When_Asynchronous_Transport_Ignores_Cancellation()
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport { Handler = (_, _) => completion.Task };
            using var fixture = await CreateAsync(transports: new[] { transport },
                options: new AiConcurrencyPolicyInvocationOptions { EvaluationTimeout = TimeSpan.FromMilliseconds(100) });
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => fixture.AdmitAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("server deadline", exception.Message);
            Assert.True(transport.LastToken.IsCancellationRequested);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            completion.TrySetResult(Response(Assert.Single(transport.Calls)));
            Assert.DoesNotContain(fixture.Observer.Events, x => x.SemanticEventType == AiEngineEvents.Policy.Allowed);
            Assert.Single(fixture.Observer.Events.Where(x => x.SemanticEventType == AiEngineEvents.Policy.Failed));
        }

        [Fact]
        public async Task Caller_Cancellation_Does_Not_Become_Timeout_Or_Allow()
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport { Handler = (_, _) => completion.Task };
            using var fixture = await CreateAsync(transports: new[] { transport });
            using var cancellation = new CancellationTokenSource();
            var evaluation = fixture.AdmitAsync(cancellation.Token);
            var request = await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(transport.LastToken.IsCancellationRequested);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            completion.TrySetResult(Response(request));
        }

        [Fact]
        public async Task Already_Cancelled_Evaluation_Does_Not_Call_Transport()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DecideAsync(cancellation.Token));
            Assert.Empty(transport.Calls);
        }

        [Theory]
        [InlineData("name")]
        [InlineData("reference")]
        [InlineData("language")]
        [InlineData("missing")]
        [InlineData("removed")]
        [InlineData("downgrade")]
        public async Task Stale_Compiled_Binding_Cannot_Evaluate_Another_Declaration(string mismatch)
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(transports: new[] { transport });
            if (mismatch == "missing") fixture.Effective.Policies.Add(Policy("extra"));
            else if (mismatch == "removed") fixture.Effective.Policies.Clear();
            else if (mismatch == "downgrade") fixture.Effective.Policies[0] = new AiConfiguredPolicyDefinition { Name = "guard" };
            else fixture.Effective.Policies[0] = Policy(
                name: mismatch == "name" ? "other" : "guard",
                language: mismatch == "language" ? "typescript" : null,
                reference: mismatch == "reference" ? "publication/guard/v2" : null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DecideAsync());
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task Missing_Trusted_Tenant_Is_Not_Taken_From_User_Config()
        {
            var transport = new Transport();
            using var fixture = await CreateAsync(
                CreatePipeline(new[] { Native() }, config: Config(Policy(config: new Dictionary<string, object?> { ["tenantId"] = "spoofed" }))),
                new[] { transport });
            fixture.Execution.Record.ExecutionContextSnapshot = null;
            fixture.Accessor.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DecideAsync());
            Assert.Empty(transport.Calls);
        }

        [Theory]
        [InlineData("ruby")]
        [InlineData("Python")]
        [InlineData("")]
        public void Invalid_Transport_Language_Is_Refused(string language)
        {
            Assert.Throws<InvalidOperationException>(() => new AiConcurrencyPolicyAdapterFactory(new[] { new Transport(language) }));
        }

        [Fact]
        public void Duplicate_Transport_Capability_Is_Refused()
        {
            Assert.Throws<InvalidOperationException>(() => new AiConcurrencyPolicyAdapterFactory(new[] { new Transport(), new Transport() }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(30001)]
        public void Invalid_Server_Deadline_Is_Refused(int milliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiConcurrencyPolicyAdapterFactory(new[] { new Transport() },
                new AiConcurrencyPolicyInvocationOptions { EvaluationTimeout = TimeSpan.FromMilliseconds(milliseconds) }));
        }
    }
}
