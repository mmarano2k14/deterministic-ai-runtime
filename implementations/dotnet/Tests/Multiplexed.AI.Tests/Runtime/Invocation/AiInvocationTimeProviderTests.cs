using System.Reflection;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiInvocationTimeProviderTests
    {
        [Fact]
        public void Invocation_Adapters_Retain_The_Injected_TimeProvider()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero));
            var binding = new AiPolicyInvocationBinding(
                "policy",
                AiPolicyBindingScope.Pipeline,
                null,
                new AiInvocationBinding(
                    AiInvocationKind.Custom,
                    AiExecutionLanguages.Python,
                    AiExecutionLanguageSource.Pipeline,
                    "publication/policy/v1"));
            var declaration = new AiConfiguredPolicyDefinition
            {
                Name = "policy",
                Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Custom,
                    ImplementationRef = "publication/policy/v1"
                }
            };

            var concurrency = new AiConcurrencyPolicyAdapter(
                new ConcurrencyTransport(), binding, "tenant", "group", "execution", "step", "step-key",
                CancellationToken.None, TimeSpan.FromSeconds(5), clock);
            var retry = new AiRetryPolicyAdapter(
                new RetryTransport(), binding, declaration, "tenant", "group", "execution", "pipeline", "step", "step-key",
                CancellationToken.None, TimeSpan.FromSeconds(5), clock);
            var delegation = new AiDelegationPolicyAdapter(
                new DelegationTransport(), binding, declaration, "tenant", "group", "execution", "step",
                CancellationToken.None, TimeSpan.FromSeconds(5), clock);
            var mcp = new AiMcpStepAdapter(
                new AiStepInvocationAdapterContext(
                    "pipeline", "1", "step", "step-key",
                    new AiInvocationBinding(AiInvocationKind.Mcp, null, AiExecutionLanguageSource.None,
                        ConnectionRef: "mcp://connection", Tool: "tool")),
                new McpResolver(), new McpTransport(), TimeSpan.FromSeconds(5), clock);

            Assert.Same(clock, ReadClock(concurrency));
            Assert.Same(clock, ReadClock(retry));
            Assert.Same(clock, ReadClock(delegation));
            Assert.Same(clock, ReadClock(mcp));
        }

        [Fact]
        public void Invocation_Factories_Retain_The_Injected_TimeProvider()
        {
            var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);

            Assert.Same(clock, ReadClock(new AiConcurrencyPolicyAdapterFactory(new[] { new ConcurrencyTransport() }, timeProvider: clock)));
            Assert.Same(clock, ReadClock(new AiRetryPolicyAdapterFactory(new[] { new RetryTransport() }, timeProvider: clock)));
            Assert.Same(clock, ReadClock(new AiDelegationPolicyAdapterFactory(new[] { new DelegationTransport() }, timeProvider: clock)));
            Assert.Same(clock, ReadClock(new AiMcpStepAdapterFactory(new McpResolver(), new McpTransport(), timeProvider: clock)));
        }

        private static TimeProvider ReadClock(object target)
        {
            var field = target.GetType().GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsAssignableFrom<TimeProvider>(field!.GetValue(target));
        }

        private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => utcNow;
        }

        private sealed class ConcurrencyTransport : IAiConcurrencyPolicyTransport
        {
            public string ExecutionLanguage => AiExecutionLanguages.Python;
            public Task<JsonElement> EvaluateAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class RetryTransport : IAiRetryPolicyTransport
        {
            public string ExecutionLanguage => AiExecutionLanguages.Python;
            public Task<JsonElement> EvaluateAsync(AiRetryPolicyRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class DelegationTransport : IAiDelegationPolicyTransport
        {
            public string ExecutionLanguage => AiExecutionLanguages.Python;
            public Task<JsonElement> EvaluateAsync(AiDelegationPolicyRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class McpResolver : IAiMcpToolResolver
        {
            public Task<AiMcpToolBinding?> ResolveAsync(AiMcpToolResolutionRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class McpTransport : IAiMcpToolTransport
        {
            public Task<JsonElement> InvokeAsync(AiMcpToolRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
