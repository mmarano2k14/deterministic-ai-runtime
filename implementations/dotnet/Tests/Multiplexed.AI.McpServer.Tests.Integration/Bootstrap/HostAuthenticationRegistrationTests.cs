using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    public sealed class HostAuthenticationRegistrationTests
    {
        [Fact]
        public async Task Configure_StandaloneJwt_Registers_Bearer_As_Default_Scheme()
        {
            var configuration = Configuration(
                new Dictionary<string, string?>
                {
                    ["AiMcpAuthentication:Enabled"] = "true",
                    ["AiMcpAuthentication:Issuer"] = "https://issuer.example",
                    ["AiMcpAuthentication:Audience"] = "multiplexed-ai-sdk",
                    ["AiMcpAuthentication:SymmetricSigningKey"] =
                        "0123456789abcdef0123456789abcdef"
                });
            var services = new ServiceCollection();

            HostAuthenticationRegistration.Configure(
                services,
                configuration,
                new AiMcpHostOptions
                {
                    Mode = AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances
                });

            services.AddLogging();

            await using var provider = services.BuildServiceProvider();
            var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

            Assert.Equal(
                JwtBearerDefaults.AuthenticationScheme,
                (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
            Assert.Equal(
                JwtBearerDefaults.AuthenticationScheme,
                (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
            Assert.NotNull(provider.GetService<IAuthorizationService>());
        }

        [Fact]
        public void Configure_DisabledStandaloneAuth_Registers_Framework_But_No_Default_Scheme()
        {
            var services = new ServiceCollection();

            HostAuthenticationRegistration.Configure(
                services,
                Configuration(new Dictionary<string, string?>()),
                new AiMcpHostOptions
                {
                    Mode = AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances
                });

            services.AddLogging();

            using var provider = services.BuildServiceProvider();
            var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

            Assert.Null(schemes.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult());
            Assert.Null(schemes.GetDefaultChallengeSchemeAsync().GetAwaiter().GetResult());
        }

        [Fact]
        public void Configure_StandaloneJwt_Rejects_Short_Symmetric_Key()
        {
            var services = new ServiceCollection();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                HostAuthenticationRegistration.Configure(
                    services,
                    Configuration(
                        new Dictionary<string, string?>
                        {
                            ["AiMcpAuthentication:Enabled"] = "true",
                            ["AiMcpAuthentication:Issuer"] = "issuer",
                            ["AiMcpAuthentication:Audience"] = "audience",
                            ["AiMcpAuthentication:SymmetricSigningKey"] = "short"
                        }),
                    new AiMcpHostOptions
                    {
                        Mode = AiMcpHostMode.ControlPlaneOnly
                    }));

            Assert.Contains(
                "at least 32 UTF-8 bytes",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Configure_RuntimeInstanceOnly_Does_Not_Register_Http_Authentication()
        {
            var services = new ServiceCollection();

            HostAuthenticationRegistration.Configure(
                services,
                Configuration(new Dictionary<string, string?>()),
                new AiMcpHostOptions
                {
                    Mode = AiMcpHostMode.RuntimeInstanceOnly
                });

            Assert.DoesNotContain(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(IAuthenticationSchemeProvider));
        }

        private static IConfiguration Configuration(
            IReadOnlyDictionary<string, string?> values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
    }
}
