using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication.DI;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    /// <summary>Hosted policy capability is explicit and never appears through policy assembly scanning.</summary>
    public sealed class AiHostedRetryPolicyRegistrationTests
    {
        [Fact]
        public void Hosted_Worker_Transport_Is_Required_First()
        {
            var services = new ServiceCollection();
            services.AddSingleton(PublicationTestSupport.Options);

            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedRetryPolicyExecution());
        }

        [Fact]
        public void Immutable_Publication_Is_Required_First()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAiWorkerInvocationTransport>(new Transport());

            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedRetryPolicyExecution());
        }

        [Fact]
        public void Registration_Adds_Exactly_Three_Canonical_Transports_And_One_Factory()
        {
            var services = Ready();
            services.AddAiHostedRetryPolicyExecution(new AiRetryPolicyInvocationOptions
            {
                EvaluationTimeout = TimeSpan.FromSeconds(3)
            });

            Assert.Equal(3, services.Count(s => s.ServiceType == typeof(IAiRetryPolicyTransport)));
            Assert.Single(services.Where(s => s.ServiceType == typeof(AiRetryPolicyAdapterFactory)));
            Assert.Single(services.Where(s => s.ServiceType == typeof(IAiRetryPolicyCodePreparer)));
            Assert.Single(services.Where(s => s.ServiceType == typeof(AiRetryPolicyInvocationOptions)));
        }

        [Fact]
        public void Duplicate_Hosted_Policy_Registration_Is_Refused()
        {
            var services = Ready();
            services.AddAiHostedRetryPolicyExecution();

            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedRetryPolicyExecution());
        }

        [Fact]
        public void Existing_Custom_Transport_Is_Not_Silently_Combined_With_Hosted_Transports()
        {
            var services = Ready();
            services.AddSingleton<IAiRetryPolicyTransport>(new ExistingPolicyTransport());

            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedRetryPolicyExecution());
        }

        private static ServiceCollection Ready()
        {
            var services = new ServiceCollection();
            services.AddAiImmutablePublications(PublicationTestSupport.Options);
            services.AddSingleton<IAiWorkerInvocationTransport>(new Transport());
            return services;
        }

        private sealed class Transport : IAiWorkerInvocationTransport
        {
            public Task<AiDurableInvocationResult> InvokeAsync(
                AiWorkerInvocationRequest request,
                Func<CancellationToken, Task> heartbeat,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class ExistingPolicyTransport : IAiRetryPolicyTransport
        {
            public string ExecutionLanguage => "python";
            public Task<System.Text.Json.JsonElement> EvaluateAsync(
                AiRetryPolicyRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
