using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.DI
{
    /// <summary>
    /// Unit tests for <see cref="HttpAiRuntimeInstanceProviderServiceCollectionExtensions"/>.
    /// </summary>
    public sealed class HttpAiRuntimeInstanceProviderServiceCollectionExtensionsTests
    {
        /// <summary>
        /// Verifies that HTTP provider registration is opt-in and not included in the default provider registration.
        /// </summary>
        [Fact]
        public void AddAiRuntimeInstanceProviders_WithoutHttpProvider_ShouldNotRegisterHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();

            using var provider =
                services.BuildServiceProvider();

            var providers =
                provider.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            Assert.DoesNotContain(
                providers,
                runtimeProvider => runtimeProvider is HttpAiRuntimeInstanceProvider);
        }

        /// <summary>
        /// Verifies that the HTTP runtime instance provider can be registered explicitly.
        /// </summary>
        [Fact]
        public void AddAiHttpRuntimeInstanceProvider_ShouldRegisterHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            using var provider =
                services.BuildServiceProvider();

            var providers =
                provider.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            Assert.Contains(
                providers,
                runtimeProvider => runtimeProvider is HttpAiRuntimeInstanceProvider);
        }

        /// <summary>
        /// Verifies that the provider router can resolve the HTTP provider after opt-in registration.
        /// </summary>
        [Fact]
        public void AddAiHttpRuntimeInstanceProvider_ShouldAllowRouterToResolveHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            using var provider =
                services.BuildServiceProvider();

            var router =
                provider.GetRequiredService<IAiRuntimeInstanceProviderRouter>();

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = "runtime-http-1",
                    Metadata = new Dictionary<string, string>
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "http",
                        [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                            "http://runtime-http-1:8080"
                    }
                };

            var resolved =
                router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                    descriptor,
                    out var resolvedProvider);

            Assert.True(resolved);
            Assert.NotNull(resolvedProvider);
            Assert.IsType<HttpAiRuntimeInstanceProvider>(resolvedProvider);
        }

        /// <summary>
        /// Registers the minimum dependencies required to instantiate the local and HTTP runtime instance providers.
        /// </summary>
        /// <param name="services">The service collection.</param>
        private static void AddRequiredLocalProviderDependencies(
            IServiceCollection services)
        {
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AiHttpRuntimeInstanceProvider:EnableRetry"] = "true",
                            ["AiHttpRuntimeInstanceProvider:MaxRetryAttempts"] = "1",
                            ["AiHttpRuntimeInstanceProvider:RetryBaseDelay"] = "00:00:00.010",
                            ["AiHttpRuntimeInstanceProvider:RetryMaxDelay"] = "00:00:00.050",
                            ["AiHttpRuntimeInstanceProvider:RetryTimeouts"] = "false",
                            ["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "true",
                            ["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "5",
                            ["AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration"] = "00:00:30",
                            ["AiHttpRuntimeInstanceProvider:DispatchTimeout"] = "00:00:30"
                        })
                    .Build());

            services.AddSingleton<IAiSharedRuntimeInstanceRegistry, TestSharedRuntimeInstanceRegistry>();
        }

        /// <summary>
        /// Test shared runtime instance registry used only to satisfy local provider activation.
        /// </summary>
        private sealed class TestSharedRuntimeInstanceRegistry : IAiSharedRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, IAiSharedRuntimeInstance> instances =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task RegisterAsync(
                IAiSharedRuntimeInstance instance,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(instance);

                instances[instance.RuntimeInstanceId] = instance;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<IAiSharedRuntimeInstance?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                instances.TryGetValue(
                    runtimeInstanceId,
                    out var instance);

                return Task.FromResult(instance);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<IAiSharedRuntimeInstance>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                IReadOnlyCollection<IAiSharedRuntimeInstance> result =
                    instances.Values.ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<bool> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                return Task.FromResult(
                    instances.Remove(runtimeInstanceId));
            }
        }
    }
}