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
    public sealed class AiHostedDelegationPolicyTransportTests
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
            Assert.ThrowsAny<ArgumentException>(() => new AiHostedDelegationPolicyTransport(
                language!, new Preparer("python"), new Transport()));
        }

        [Fact]
        public async Task Evaluation_Uses_The_Exact_Preallocation_Request_As_Portable_Inputs()
        {
            var fixture = new Fixture();
            var response = await fixture.Policy.EvaluateAsync(fixture.Request);

            Assert.Equal("approve", response.GetProperty("decision").GetString());
            var wire = Assert.Single(fixture.Transport.Requests);
            Assert.Equal(fixture.Request.RequestId, wire.RequestId);
            Assert.Equal("delegation-policy-" + fixture.Request.RequestId, wire.OperationId);
            Assert.Equal(wire.OperationId, wire.EffectIdempotencyKey);
            Assert.Equal(fixture.Request.Context.ParentExecutionId, wire.ExecutionId);
            Assert.Equal(fixture.Request.Context.ParentCallSiteId, wire.StepName);
            Assert.Equal(0, wire.Generation);
            Assert.Equal(2, wire.Inputs.GetProperty("context").GetProperty("invocationGeneration").GetInt32());
            Assert.Equal("delegation", wire.Inputs.GetProperty("policyKind").GetString());
            Assert.False(wire.Inputs.GetProperty("context").TryGetProperty("childExecutionId", out _));
        }

        [Fact]
        public async Task Worker_Business_Failure_Is_A_Technical_Policy_Failure_Not_A_Denial()
        {
            var fixture = new Fixture();
            fixture.Transport.Body = (_, _, _) => Task.FromResult(
                new AiDurableInvocationResult(false, "{\"reason\":\"worker failed\"}"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Policy.EvaluateAsync(fixture.Request));

            Assert.Contains("technical failure", error.Message, StringComparison.OrdinalIgnoreCase);
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
        public async Task Cancellation_Before_Evaluation_Does_Not_Start_A_Worker()
        {
            var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Policy.EvaluateAsync(fixture.Request, cancellation.Token));
            Assert.Equal(0, fixture.Preparer.Calls);
            Assert.Empty(fixture.Transport.Requests);
        }

        private sealed class Fixture
        {
            internal readonly Preparer Preparer;
            internal readonly Transport Transport = new();
            internal readonly AiHostedDelegationPolicyTransport Policy;
            internal readonly AiDelegationPolicyRequest Request;

            internal Fixture(string language = "python")
            {
                Preparer = new Preparer(language);
                Policy = new AiHostedDelegationPolicyTransport(language, Preparer, Transport);
                Request = CreateRequest(language, Preparer.Bundle.Target.ImplementationRef);
                Transport.Body = (wire, _, _) => Task.FromResult(new AiDurableInvocationResult(true,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        requestId = wire.RequestId,
                        policyKind = "delegation",
                        decision = "approve",
                        reason = (string?)null
                    })));
            }
        }

        private sealed class Preparer : IAiDelegationPolicyCodePreparer
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

            public Task<AiWorkerCodeBundle> PrepareAsync(
                AiDelegationPolicyRequest request,
                CancellationToken cancellationToken = default)
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

        private static AiDelegationPolicyRequest CreateRequest(string language, string implementationRef) => new(
            "request-policy-a",
            "delegation-guard",
            "Step",
            "invoke-child",
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(5),
            new AiDelegationPolicyInput(
                "tenant-a", "group-a", "parent-a", "invoke-child", "child-a", "1", "child-key", 2),
            JsonSerializer.SerializeToElement(new { region = "eu" }));
    }
}
