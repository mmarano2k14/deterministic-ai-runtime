using System.Collections.Concurrent;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    /// <summary>Hosted policy transport remains a short worker call, not a durable step invocation.</summary>
    public sealed class AiHostedConcurrencyPolicyTransportTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public void Canonical_Languages_Are_Exposed_Exactly(string language)
        {
            var fixture = new Fixture(language);
            Assert.Equal(language, fixture.Policy.ExecutionLanguage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Python")]
        [InlineData("java")]
        public void Unknown_Or_Noncanonical_Languages_Are_Refused(string? language)
        {
            Assert.ThrowsAny<ArgumentException>(() => new AiHostedConcurrencyPolicyTransport(
                language!, new Preparer("python"), new Transport()));
        }

        [Fact]
        public async Task Evaluation_Uses_The_Exact_Policy_Request_As_Portable_Inputs()
        {
            var fixture = new Fixture();
            var response = await fixture.Policy.EvaluateAsync(fixture.Request);

            Assert.Equal("allow", response.GetProperty("decision").GetString());
            var wire = Assert.Single(fixture.Transport.Requests);
            Assert.Equal(fixture.Request.RequestId, wire.RequestId);
            Assert.Equal("policy-eval-" + fixture.Request.RequestId, wire.OperationId);
            Assert.Equal(wire.OperationId, wire.EffectIdempotencyKey);
            Assert.Equal(fixture.Request.Context.TenantId, wire.TenantId);
            Assert.Equal(fixture.Request.Context.ExecutionId, wire.ExecutionId);
            Assert.Equal(fixture.Request.Context.StepName, wire.StepName);
            Assert.Equal(1, wire.Epoch);
            Assert.Equal(0, wire.Generation);
            Assert.Equal(fixture.Request.DeadlineUtc, wire.DeadlineUtc);
            Assert.Equal(fixture.Request.PolicyName, wire.Inputs.GetProperty("policyName").GetString());
            Assert.Equal(fixture.Request.ImplementationRef, wire.Inputs.GetProperty("implementationRef").GetString());
            Assert.Equal("concurrency", wire.Inputs.GetProperty("policyKind").GetString());
            Assert.Equal("allow", response.GetProperty("decision").GetString());
        }

        [Fact]
        public async Task Worker_Business_Failure_Is_A_Technical_Policy_Failure_Not_A_Denial()
        {
            var fixture = new Fixture();
            fixture.Transport.Body = (_, _, _) => Task.FromResult(new AiDurableInvocationResult(false, "{\"reason\":\"worker failed\"}"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Policy.EvaluateAsync(fixture.Request));
            Assert.True(error.Message.Contains("technical failure", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Malformed_Policy_Payload_Is_Left_For_The_Existing_Response_Reader_To_Reject()
        {
            var fixture = new Fixture();
            fixture.Transport.Body = (_, _, _) => Task.FromResult(new AiDurableInvocationResult(true, "[]"));

            var response = await fixture.Policy.EvaluateAsync(fixture.Request);
            Assert.Equal(JsonValueKind.Array, response.ValueKind);
        }

        [Fact]
        public async Task Prepared_Implementation_Cannot_Substitute_The_Resolved_Language()
        {
            var fixture = new Fixture();
            fixture.Preparer.Bundle = fixture.Preparer.Bundle with
            {
                Target = fixture.Preparer.Bundle.Target with { ExecutionLanguage = "typescript" }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Policy.EvaluateAsync(fixture.Request));
            Assert.Empty(fixture.Transport.Requests);
        }

        [Fact]
        public async Task Prepared_Implementation_Cannot_Substitute_The_Implementation_Reference()
        {
            var fixture = new Fixture();
            fixture.Preparer.Bundle = fixture.Preparer.Bundle with
            {
                Target = fixture.Preparer.Bundle.Target with { ImplementationRef = "impl-other" }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Policy.EvaluateAsync(fixture.Request));
            Assert.Empty(fixture.Transport.Requests);
        }

        [Fact]
        public async Task Expired_Deadline_Does_Not_Read_Publication_Or_Start_A_Worker()
        {
            var fixture = new Fixture();
            var request = fixture.Request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };

            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Policy.EvaluateAsync(request));
            Assert.Equal(0, fixture.Preparer.Calls);
            Assert.Empty(fixture.Transport.Requests);
        }

        [Fact]
        public async Task Cancellation_Before_Evaluation_Does_Not_Read_Publication_Or_Start_A_Worker()
        {
            var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Policy.EvaluateAsync(fixture.Request, cancellation.Token));
            Assert.Equal(0, fixture.Preparer.Calls);
            Assert.Empty(fixture.Transport.Requests);
        }

        [Fact]
        public async Task Transport_Cancellation_Is_Not_Converted_Into_A_Policy_Decision()
        {
            var fixture = new Fixture();
            fixture.Transport.Body = async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Policy.EvaluateAsync(fixture.Request, cancellation.Token));
        }

        private sealed class Fixture
        {
            internal readonly Preparer Preparer;
            internal readonly Transport Transport = new();
            internal readonly AiHostedConcurrencyPolicyTransport Policy;
            internal readonly AiConcurrencyPolicyRequest Request;

            internal Fixture(string language = "python")
            {
                Preparer = new Preparer(language);
                Policy = new AiHostedConcurrencyPolicyTransport(language, Preparer, Transport);
                Request = CreateRequest(language, Preparer.Bundle.Target.ImplementationRef);
                Transport.Body = (wire, _, _) => Task.FromResult(new AiDurableInvocationResult(true,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        requestId = wire.RequestId,
                        policyKind = "concurrency",
                        decision = "allow",
                        reason = (string?)null
                    })));
            }
        }

        private sealed class Preparer : IAiConcurrencyPolicyCodePreparer
        {
            internal int Calls;
            internal AiWorkerCodeBundle Bundle;
            internal Preparer(string language)
            {
                var target = DurableInvocationTestSupport.Definition().Target with
                {
                    ExecutionLanguage = language,
                    ImplementationRef = "impl-policy"
                };
                Bundle = WorkerTestSupport.Bundle(target) with
                {
                    Runtime = PublicationTestSupport.Environment(language)
                };
            }
            public Task<AiWorkerCodeBundle> PrepareAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Calls);
                return Task.FromResult(Bundle);
            }
        }

        private sealed class Transport : IAiWorkerInvocationTransport
        {
            internal readonly ConcurrentQueue<AiWorkerInvocationRequest> Requests = new();
            internal Func<AiWorkerInvocationRequest, Func<CancellationToken, Task>, CancellationToken, Task<AiDurableInvocationResult>> Body =
                (_, _, _) => Task.FromResult(new AiDurableInvocationResult(true, "{}"));

            public Task<AiDurableInvocationResult> InvokeAsync(
                AiWorkerInvocationRequest request,
                Func<CancellationToken, Task> heartbeat,
                CancellationToken cancellationToken = default)
            {
                Requests.Enqueue(request);
                return Body(request, heartbeat, cancellationToken);
            }
        }

        private static AiConcurrencyPolicyRequest CreateRequest(string language, string implementationRef) => new(
            "request-policy-a",
            "capacity-guard",
            "Pipeline",
            null,
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(5),
            new AiConcurrencyPolicyInput(
                "tenant-a", "group-a", "execution-a", "pipeline-a", "step-a", "code-step-a",
                "runtime-a", "provider-a", "model-a", "operation-a"),
            JsonSerializer.SerializeToElement(new { limit = 2 }));
    }
}
