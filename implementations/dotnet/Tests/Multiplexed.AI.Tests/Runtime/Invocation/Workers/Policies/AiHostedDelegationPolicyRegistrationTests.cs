using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication.DI;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    public sealed class AiHostedDelegationPolicyRegistrationTests
    {
        [Fact]
        public void Hosted_Worker_Transport_Is_Required_First()
        {
            var services = new ServiceCollection();
            services.AddSingleton(PublicationTestSupport.Options);
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedDelegationPolicyExecution());
        }

        [Fact]
        public void Immutable_Publication_Is_Required_First()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAiWorkerInvocationTransport>(new Transport());
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedDelegationPolicyExecution());
        }

        [Fact]
        public void Registration_Adds_Exactly_Three_Canonical_Transports_And_One_Factory()
        {
            var services = Ready();
            services.AddAiHostedDelegationPolicyExecution(new AiDelegationPolicyInvocationOptions
            {
                EvaluationTimeout = TimeSpan.FromSeconds(3)
            });

            Assert.Equal(3, services.Count(service => service.ServiceType == typeof(IAiDelegationPolicyTransport)));
            Assert.Single(services.Where(service => service.ServiceType == typeof(AiDelegationPolicyAdapterFactory)));
            Assert.Single(services.Where(service => service.ServiceType == typeof(IAiDelegationPolicyCodePreparer)));
            Assert.Single(services.Where(service => service.ServiceType == typeof(AiDelegationPolicyInvocationOptions)));
        }

        [Fact]
        public void Duplicate_Hosted_Policy_Registration_Is_Refused()
        {
            var services = Ready();
            services.AddAiHostedDelegationPolicyExecution();
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedDelegationPolicyExecution());
        }

        [Fact]
        public void Existing_Custom_Transport_Is_Not_Silently_Combined_With_Hosted_Transports()
        {
            var services = Ready();
            services.AddSingleton<IAiDelegationPolicyTransport>(new ExistingPolicyTransport());
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedDelegationPolicyExecution());
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

        private sealed class ExistingPolicyTransport : IAiDelegationPolicyTransport
        {
            public string ExecutionLanguage => "python";
            public Task<JsonElement> EvaluateAsync(
                AiDelegationPolicyRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
