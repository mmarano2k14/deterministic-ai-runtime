using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Explicit composition, required capabilities and absence of hidden default execution services.</summary>
    public sealed class AiPublicationRegistrationTests
    {
        [Fact]
        public void Duplicate_Registration_Is_Refused()
        {
            var services = new ServiceCollection(); services.AddAiImmutablePublications(PublicationTestSupport.Options);
            Assert.Throws<InvalidOperationException>(() => services.AddAiImmutablePublications(PublicationTestSupport.Options));
        }

        [Fact]
        public void Existing_Target_Resolver_Is_Not_Silently_Replaced()
        {
            var services = new ServiceCollection(); services.AddSingleton<IAiDurableInvocationTargetResolver>(new Resolver());
            Assert.Throws<InvalidOperationException>(() => services.AddAiImmutablePublications(PublicationTestSupport.Options));
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(AiPublicationOptions));
        }

        [Fact]
        public void Registration_Does_Not_Start_Workers_Or_Install_Rbac_Grants()
        {
            var services = new ServiceCollection(); services.AddAiImmutablePublications(PublicationTestSupport.Options);
            Assert.DoesNotContain(services, d => d.ServiceType.FullName == "Microsoft.Extensions.Hosting.IHostedService");
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiPublicationEnvironmentCatalog));
            Assert.DoesNotContain(services, d => d.ServiceType.Name is "IAiPolicy" or "IAiStep" or "IAuthorizationEngine");
        }

        [Theory]
        [InlineData("*")]
        [InlineData("code:admin")]
        [InlineData("code/other")]
        [InlineData("")]
        public void Host_Capability_Requests_Must_Be_Concrete(string resource)
        {
            var defaults = PublicationTestSupport.Options;
            Assert.Throws<ArgumentException>(() => new AiPublicationOptions(new(resource, "publication", "publish"), defaults.Read, defaults.Execute));
        }

        [Fact]
        public async Task A_Legacy_Mutable_Store_Is_Not_Used_As_An_Immutable_Fallback()
        {
            using var fixture = new PublicationTestSupport.Fixture(payloadStore: new LegacyPayloadStore());
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.PublishAsync());
        }

        [Fact]
        public void Duplicate_Environment_References_Are_Refused()
        {
            var environment = PublicationTestSupport.Environment("python");
            Assert.Throws<ArgumentException>(() => new AiConfiguredPublicationEnvironmentCatalog(new[] { environment, environment }));
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("mcp")]
        public void Environment_Catalog_Is_Not_A_Language_Fallback(string language)
        {
            var environment = PublicationTestSupport.Environment("python") with { ExecutionLanguage = language };
            Assert.Throws<InvalidOperationException>(() => new AiConfiguredPublicationEnvironmentCatalog(new[] { environment }));
        }

        private sealed class Resolver : IAiDurableInvocationTargetResolver
        {
            public Task<AiDurableInvocationTarget?> ResolveAsync(AiDurableInvocationTargetRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult<AiDurableInvocationTarget?>(null);
        }
        private sealed class LegacyPayloadStore : IAiPayloadStore
        {
            public Task<string> SaveAsync(string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
