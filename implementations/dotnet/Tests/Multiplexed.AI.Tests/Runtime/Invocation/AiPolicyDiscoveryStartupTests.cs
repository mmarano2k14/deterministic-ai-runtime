using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.DI;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Concurrency.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.AI.Retry.Policies;
using Multiplexed.AI.Runtime.Execution.Retention.Policies;
using Multiplexed.AI.Runtime.Invocation;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>
    /// Exercises the real host policy scan, not a hand-built empty registry. Native
    /// startup must not require a custom transport or construct an invocation adapter.
    /// These tests need no Redis, MongoDB, worker process or RBAC replacement.
    /// </summary>
    public sealed class AiPolicyDiscoveryStartupTests
    {
        private static readonly Assembly RuntimeAssembly = typeof(AiRuntimeAssemblyMarker).Assembly;
        private static readonly (Type Type, string Key, AiPolicyKind Kind)[] NativePolicies =
        {
            (typeof(DefaultRateLimitRetryPolicy), "retry.rate-limit.default", AiPolicyKind.Retry),
            (typeof(DefaultTimeoutRetryPolicy), "retry.timeout.default", AiPolicyKind.Retry),
            (typeof(DefaultTransientRetryPolicy), "retry.transient.default", AiPolicyKind.Retry),
            (typeof(CompactAiRetentionPolicy), "retention.compact.terminal", AiPolicyKind.Retention),
            (typeof(EvictAiRetentionPolicy), "retention.evict.terminal", AiPolicyKind.Retention),
            (typeof(HybridAiRetentionPolicy), "retention.hybrid.terminal", AiPolicyKind.Retention),
            (typeof(AiModelAdmissionConcurrencyPolicy), "concurrency.model.admission", AiPolicyKind.Concurrency),
            (typeof(AiOperationAdmissionConcurrencyPolicy), "concurrency.operation.admission", AiPolicyKind.Concurrency),
            (typeof(AiProviderAdmissionConcurrencyPolicy), "concurrency.provider.admission", AiPolicyKind.Concurrency),
            (typeof(AiThrottleConcurrencyPolicy), "concurrency.throttle", AiPolicyKind.Concurrency)
        };

        [Fact]
        public void Runtime_Scan_Excludes_The_Contextual_Adapter()
        {
            var adapter = RuntimeAssembly.GetType(
                "Multiplexed.AI.Runtime.Invocation.AiConcurrencyPolicyAdapter", throwOnError: true)!;
            var services = _createPolicyServices();

            Assert.True(typeof(IAiPolicy).IsAssignableFrom(adapter));
            Assert.True(adapter.IsDefined(typeof(AiPolicyDiscoveryIgnoreAttribute), inherit: true));
            Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == adapter);
        }

        [Fact]
        public void Native_Registry_Resolves_Without_Custom_Transport_Or_Factory()
        {
            var services = _createPolicyServices();
            using var provider = _buildValidatedProvider(services);

            Assert.Null(provider.GetService<IAiConcurrencyPolicyTransport>());
            Assert.Null(provider.GetService<AiConcurrencyPolicyAdapterFactory>());
            _assertNativePolicies(provider);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Hosted_Policy_Consumer_Starts_Without_Custom_Transport(bool validateOnBuild)
        {
            using var host = new HostBuilder()
                .UseDefaultServiceProvider((_, options) =>
                {
                    options.ValidateOnBuild = validateOnBuild;
                    options.ValidateScopes = true;
                })
                .ConfigureServices((_, services) =>
                {
                    _registerPolicyServices(services);
                    services.AddHostedService<PolicyRegistryStartupProbe>();
                })
                .Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // With lazy DI validation the original regression throws from StartAsync,
            // just as the external Process Host does before its readiness endpoint.
            await host.StartAsync(deadline.Token);
            try
            {
                var probe = Assert.Single(host.Services.GetServices<IHostedService>()
                    .OfType<PolicyRegistryStartupProbe>());
                Assert.True(probe.Started);
                Assert.Null(host.Services.GetService<IAiConcurrencyPolicyTransport>());
                _assertNativePolicies(host.Services);
            }
            finally
            {
                await host.StopAsync(deadline.Token);
            }
        }

        [Fact]
        public void Repeated_Host_Scans_Preserve_Native_Singletons_Without_Readding_Adapters()
        {
            var services = _createPolicyServices();
            services.AddAiPoliciesFromAssemblies(RuntimeAssembly, typeof(CompactAiRetentionPolicy).Assembly);
            services.AddAiPoliciesFromAssemblies(RuntimeAssembly);

            var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(IAiPolicy)).ToArray();
            Assert.Equal(NativePolicies.Length, descriptors.Length);
            Assert.All(descriptors, descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
            Assert.Equal(descriptors.Length, descriptors.Select(descriptor => descriptor.ImplementationType).Distinct().Count());

            using var provider = _buildValidatedProvider(services);
            _assertNativePolicies(provider);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_Custom_Capability_Does_Not_Enter_The_Native_Registry(bool installBeforeScan)
        {
            var services = new ServiceCollection();
            var transport = new UnusedTransport();
            if (installBeforeScan) _registerCustomCapability(services, transport);
            _registerPolicyServices(services);
            if (!installBeforeScan) _registerCustomCapability(services, transport);

            using var provider = _buildValidatedProvider(services);
            Assert.NotNull(provider.GetRequiredService<AiConcurrencyPolicyAdapterFactory>());
            Assert.Same(transport, provider.GetRequiredService<IAiConcurrencyPolicyTransport>());
            _assertNativePolicies(provider);
            Assert.Equal(0, transport.CallCount);
        }

        [Fact]
        public void Legacy_Internal_Unattributed_And_Observable_Policies_Remain_Discoverable()
        {
            var services = new ServiceCollection();
            services.AddAiPoliciesFromAssemblies(typeof(AiPolicyDiscoveryStartupTests).Assembly);

            // Inspect descriptors only: other test policies have deliberate constructor
            // dependencies. Discovery must not instantiate them or require AiPolicyAttribute.
            Assert.False(typeof(UnattributedNativePolicy).IsDefined(typeof(AiPolicyAttribute), inherit: false));
            Assert.False(typeof(InternalNativePolicy).IsVisible);
            _assertRegistered(services, typeof(UnattributedNativePolicy));
            _assertRegistered(services, typeof(InternalNativePolicy));
            _assertRegistered(services, typeof(ObservableNativePolicy));
        }

        [Fact]
        public void Discovery_OptOut_Is_Inherited_By_Contextual_Implementations()
        {
            var services = new ServiceCollection();
            services.AddAiPoliciesFromAssemblies(typeof(AiPolicyDiscoveryStartupTests).Assembly);

            Assert.True(typeof(InheritedContextualPolicy).IsDefined(typeof(AiPolicyDiscoveryIgnoreAttribute), inherit: true));
            Assert.False(typeof(InheritedContextualPolicy).IsDefined(typeof(AiPolicyDiscoveryIgnoreAttribute), inherit: false));
            Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(InheritedContextualPolicy));
            Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ExplicitlyRegisteredPolicy));
        }

        [Fact]
        public void Discovery_OptOut_Does_Not_Remove_An_Explicit_Server_Registration()
        {
            var services = _createPolicyServices();
            var policy = new ExplicitlyRegisteredPolicy();
            services.AddSingleton<IAiPolicy>(policy);
            services.AddAiPoliciesFromAssemblies(RuntimeAssembly);
            using var provider = _buildValidatedProvider(services);

            var registry = provider.GetRequiredService<IAiPolicyRegistry>();
            Assert.Same(policy, registry.Resolve(policy.Key, policy.Kind));
            Assert.Equal(NativePolicies.Length + 1, provider.GetServices<IAiPolicy>().Count());
        }

        [Fact]
        public void Missing_Native_Dependency_Is_Still_A_Startup_Error()
        {
            var services = _createPolicyServices();
            services.AddSingleton<IAiPolicy, MisconfiguredNativePolicy>();

            var exception = Assert.Throws<AggregateException>(() =>
            {
                using var provider = _buildValidatedProvider(services);
            });
            Assert.Contains(nameof(UnregisteredNativeDependency), exception.ToString());
        }

        private static ServiceCollection _createPolicyServices()
        {
            var services = new ServiceCollection();
            _registerPolicyServices(services);
            return services;
        }

        private static void _registerPolicyServices(IServiceCollection services)
        {
            services.AddSingleton<IAiPolicyRegistry, DefaultAiPolicyRegistry>();
            services.AddAiPoliciesFromAssemblies(RuntimeAssembly);
        }

        private static ServiceProvider _buildValidatedProvider(IServiceCollection services)
        {
            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        }

        private static void _registerCustomCapability(IServiceCollection services, UnusedTransport transport)
        {
            services.AddSingleton<IAiConcurrencyPolicyTransport>(transport);
            services.AddSingleton<AiConcurrencyPolicyAdapterFactory>();
        }

        private static void _assertRegistered(IServiceCollection services, Type implementation)
        {
            var descriptor = Assert.Single(services.Where(descriptor =>
                descriptor.ServiceType == typeof(IAiPolicy) && descriptor.ImplementationType == implementation));
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        private static void _assertNativePolicies(IServiceProvider provider)
        {
            var policies = provider.GetServices<IAiPolicy>().ToArray();
            var registry = provider.GetRequiredService<IAiPolicyRegistry>();
            Assert.Equal(NativePolicies.Length, policies.Length);
            Assert.DoesNotContain(policies, policy => policy is IAiPolicyInvocationIdentity);
            foreach (var expected in NativePolicies)
            {
                var policy = Assert.Single(policies.Where(policy => policy.GetType() == expected.Type));
                Assert.Equal(expected.Key, policy.Key);
                Assert.Equal(expected.Kind, policy.Kind);
                Assert.Same(policy, registry.Resolve(expected.Key, expected.Kind));
                Assert.Same(policy, provider.GetServices<IAiPolicy>().Single(candidate => candidate.GetType() == expected.Type));
            }
        }

        private sealed class PolicyRegistryStartupProbe : IHostedService
        {
            private readonly IAiPolicyRegistry _registry;
            public PolicyRegistryStartupProbe(IAiPolicyRegistry registry) => _registry = registry;
            public bool Started { get; private set; }
            public Task StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _registry.Resolve("concurrency.provider.admission", AiPolicyKind.Concurrency);
                Started = true;
                return Task.CompletedTask;
            }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class UnusedTransport : IAiConcurrencyPolicyTransport
        {
            public string ExecutionLanguage => AiExecutionLanguages.Python;
            public int CallCount { get; private set; }
            public Task<JsonElement> EvaluateAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default)
            {
                CallCount++;
                throw new InvalidOperationException("Native host startup must not call a custom transport.");
            }
        }

        public class UnattributedNativePolicy : IAiPolicy
        {
            public virtual string Key => "test.discovery.unattributed";
            public AiPolicyKind Kind => AiPolicyKind.Concurrency;
            public Task<AiPolicyResult> ExecuteAsync(object context, CancellationToken cancellationToken = default) =>
                Task.FromResult(AiPolicyResult.Success());
        }

        private sealed class InternalNativePolicy : UnattributedNativePolicy
        {
            public override string Key => "test.discovery.internal";
        }

        public sealed class ObservableNativePolicy : UnattributedNativePolicy, IAiPolicyInvocationIdentity
        {
            public override string Key => "test.discovery.observable";
            public string PolicyName => Key;
            public IReadOnlyDictionary<string, string> InvocationMetadata { get; } = new Dictionary<string, string>();
        }

        [AiPolicyDiscoveryIgnore]
        public abstract class ContextualPolicyBase : UnattributedNativePolicy
        {
        }

        public sealed class InheritedContextualPolicy : ContextualPolicyBase
        {
            public override string Key => "test.discovery.inherited";
        }

        [AiPolicyDiscoveryIgnore]
        public sealed class ExplicitlyRegisteredPolicy : UnattributedNativePolicy
        {
            public override string Key => "test.discovery.explicit";
        }

        public sealed class UnregisteredNativeDependency
        {
        }

        public sealed class MisconfiguredNativePolicy : UnattributedNativePolicy
        {
            public MisconfiguredNativePolicy(UnregisteredNativeDependency dependency) =>
                ArgumentNullException.ThrowIfNull(dependency);
            public override string Key => "test.discovery.misconfigured";
        }
    }
}
