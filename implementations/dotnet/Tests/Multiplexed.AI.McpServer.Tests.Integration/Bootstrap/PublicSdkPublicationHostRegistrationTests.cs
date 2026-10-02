using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    public sealed class PublicSdkPublicationHostRegistrationTests
    {
        [Theory]
        [InlineData(AiMcpHostMode.ControlPlaneOnly)]
        [InlineData(AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances)]
        [InlineData(AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances)]
        [InlineData(AiMcpHostMode.ControlPlaneWithGrpcRuntimeInstances)]
        public void Configure_ControlPlaneMode_Registers_NativeOnly_Publication_Authority(
            AiMcpHostMode mode)
        {
            var services = new ServiceCollection();

            PublicSdkPublicationHostRegistration.Configure(
                services,
                new AiMcpHostOptions
                {
                    Mode = mode
                });

            Assert.Single(
                services.Where(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(AiPublicationOptions)));

            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(AiPipelinePublicationService));

            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(AiPublishedDagRunService));

            using var provider = services.BuildServiceProvider();

            var catalog =
                provider.GetRequiredService<IAiPublicationEnvironmentCatalog>();

            Assert.Null(catalog.Find("not-provisioned"));
        }

        [Fact]
        public void Configure_RuntimeInstanceOnly_Does_Not_Register_Publication_Authority()
        {
            var services = new ServiceCollection();

            PublicSdkPublicationHostRegistration.Configure(
                services,
                new AiMcpHostOptions
                {
                    Mode = AiMcpHostMode.RuntimeInstanceOnly
                });

            Assert.DoesNotContain(
                services,
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(AiPublicationOptions));

            Assert.DoesNotContain(
                services,
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(AiPipelinePublicationService));
        }

        [Fact]
        public void Configure_Reuses_Complete_Hosted_Publication_Registration()
        {
            var services = new ServiceCollection();

            var catalog =
                new AiConfiguredPublicationEnvironmentCatalog(
                    Array.Empty<AiPublicationEnvironment>());

            services.AddSingleton<IAiPublicationEnvironmentCatalog>(
                catalog);

            services.AddSingleton<IAiPublicationExecutionEnvironmentCatalog>(
                catalog);

            services.AddAiImmutablePublications(
                Options());

            PublicSdkPublicationHostRegistration.Configure(
                services,
                new AiMcpHostOptions
                {
                    Mode = AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances
                });

            Assert.Single(
                services.Where(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(AiPublicationOptions)));

            Assert.Single(
                services.Where(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(AiPipelinePublicationService)));

            Assert.Single(
                services.Where(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(AiPublishedDagRunService)));
        }

        [Fact]
        public void Configure_Partial_Preexisting_Publication_Registration_Fails_Closed()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Options());

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PublicSdkPublicationHostRegistration.Configure(
                    services,
                    new AiMcpHostOptions
                    {
                        Mode = AiMcpHostMode.ControlPlaneOnly
                    }));

            Assert.Contains(
                nameof(AiPipelinePublicationService),
                exception.Message,
                StringComparison.Ordinal);

            Assert.Contains(
                nameof(AiPublishedDagRunService),
                exception.Message,
                StringComparison.Ordinal);
        }

        private static AiPublicationOptions Options() =>
            new(
                new AiPublicationCapability(
                    "code",
                    "publication",
                    "publish"),
                new AiPublicationCapability(
                    "code",
                    "publication",
                    "read"),
                new AiPublicationCapability(
                    "code",
                    "publication",
                    "execute"));
    }
}
