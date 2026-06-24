using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
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
        public async Task AddAiRuntimeInstanceProviders_WithoutHttpProvider_ShouldNotRegisterHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();

            await using var provider =
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
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldRegisterHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
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
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldAllowRouterToResolveHttpProvider()
        {
            var services = new ServiceCollection();

            AddRequiredLocalProviderDependencies(services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
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
        /// Verifies that the HTTP runtime instance provider exposes the scale-out provider capability.
        /// </summary>
        [Fact]
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldAllowRouterToResolveHttpScaleOutProvider()
        {
            var services =
                new ServiceCollection();

            AddRequiredLocalProviderDependencies(
                services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
                services.BuildServiceProvider();

            var router =
                provider.GetRequiredService<IAiRuntimeInstanceProviderRouter>();

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = "runtime-http-scaleout-1",
                    Metadata = new Dictionary<string, string>
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "http"
                    }
                };

            var resolved =
                router.TryGetProvider<IAiRuntimeScaleOutProvider>(
                    descriptor,
                    out var resolvedProvider);

            Assert.True(
                resolved);

            Assert.NotNull(
                resolvedProvider);

            Assert.IsType<HttpAiRuntimeInstanceProvider>(
                resolvedProvider);
        }

        /// <summary>
        /// Verifies that the HTTP runtime scale-out provisioner is registered.
        /// </summary>
        [Fact]
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldRegisterHttpRuntimeScaleOutProvisioner()
        {
            var services =
                new ServiceCollection();

            AddRequiredLocalProviderDependencies(
                services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
                services.BuildServiceProvider();

            var provisioner =
                provider.GetRequiredService<IAiHttpRuntimeScaleOutProvisioner>();

            Assert.IsType<AiHttpRuntimeScaleOutProvisioner>(
                provisioner);
        }

        /// <summary>
        /// Verifies that HTTP runtime scale-out options are bound from configuration.
        /// </summary>
        [Fact]
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldBindHttpRuntimeScaleOutOptionsFromConfiguration()
        {
            var services =
                new ServiceCollection();

            AddRequiredLocalProviderDependencies(
                services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
                services.BuildServiceProvider();

            var options =
                provider
                    .GetRequiredService<IOptions<AiHttpRuntimeScaleOutOptions>>()
                    .Value;

            Assert.True(
                options.Enabled);

            Assert.Equal(
                "http-runtime",
                options.DefaultRuntimeInstanceIdPrefix);

            Assert.Equal(
                "http://localhost",
                options.EndpointTemplate);
        }

        /// <summary>
        /// Verifies that resolving the HTTP provider also injects the HTTP runtime scale-out provisioner.
        /// </summary>
        [Fact]
        public async Task AddAiHttpRuntimeInstanceProvider_ShouldResolveHttpProviderWithScaleOutProvisioner()
        {
            var services =
                new ServiceCollection();

            AddRequiredLocalProviderDependencies(
                services);

            services.AddAiRuntimeInstanceProviders();
            services.AddAiHttpRuntimeInstanceProvider();

            await using var provider =
                services.BuildServiceProvider();

            var runtimeProvider =
                provider
                    .GetServices<IAiRuntimeInstanceProvider>()
                    .Single(item => item is HttpAiRuntimeInstanceProvider);

            Assert.IsAssignableFrom<IAiRuntimeScaleOutProvider>(
                runtimeProvider);
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
                            ["AiHttpRuntimeInstanceProvider:DispatchTimeout"] = "00:00:30",

                            ["AiHttpRuntimeScaleOut:Enabled"] = "true",
                            ["AiHttpRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "http-runtime",
                            ["AiHttpRuntimeScaleOut:EndpointTemplate"] = "http://localhost"
                        })
                    .Build());

            services.AddSingleton<IAiSharedRuntimeInstanceRegistry, TestSharedRuntimeInstanceRegistry>();
            services.AddSingleton<IAiRuntimeInstanceRegistry, TestRuntimeInstanceRegistry>();
            services.AddSingleton<IAiRuntimeInstanceCapacityStore, TestRuntimeInstanceCapacityStore>();
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

        /// <summary>
        /// Test runtime instance registry used only to satisfy HTTP scale-out provisioner activation.
        /// </summary>
        private sealed class TestRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, AiRuntimeInstanceSnapshot> registrations =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);
                ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeInstanceId);

                var snapshot =
                    CreateSnapshot(
                        registration,
                        AiRuntimeInstanceStatus.Ready,
                        isQueuePaused: false,
                        canAcceptRun: true);

                registrations[registration.RuntimeInstanceId] =
                    snapshot;

                return Task.FromResult(
                    snapshot);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                if (!registrations.TryGetValue(
                        runtimeInstanceId,
                        out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                        null);
                }

                var updated =
                    new AiRuntimeInstanceSnapshot
                    {
                        RuntimeInstanceId = existing.RuntimeInstanceId,
                        ControlPlaneId = existing.ControlPlaneId,
                        ControlPlaneHostId = existing.ControlPlaneHostId,
                        HostId = existing.HostId,
                        RuntimeId = existing.RuntimeId,
                        Role = existing.Role,
                        Status = status,
                        WorkerCount = existing.WorkerCount,
                        QueueCapacity = existing.QueueCapacity,
                        MaxConcurrentRuns = existing.MaxConcurrentRuns,
                        RegisteredAtUtc = existing.RegisteredAtUtc,
                        LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                        QueuedRunCount = queuedRunCount,
                        RunningRunCount = runningRunCount,
                        ActiveRunCount = activeRunCount,
                        AvailableRunSlots = availableRunSlots,
                        ActiveWorkerCount = activeWorkerCount,
                        AvailableWorkerCount = availableWorkerCount,
                        MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution,
                        IsQueuePaused = isQueuePaused,
                        CanAcceptRun = canAcceptRun,
                        Metadata = existing.Metadata
                    };

                registrations[runtimeInstanceId] =
                    updated;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    updated);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                registrations.TryGetValue(
                    runtimeInstanceId,
                    out var snapshot);

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    snapshot);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                var result =
                    registrations
                        .Values
                        .Where(item =>
                            includeStopped ||
                            item.Status != AiRuntimeInstanceStatus.Stopped)
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                if (!registrations.TryGetValue(
                        runtimeInstanceId,
                        out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                        null);
                }

                var updated =
                    CloneWithStatus(
                        existing,
                        AiRuntimeInstanceStatus.Draining,
                        canAcceptRun: false);

                registrations[runtimeInstanceId] =
                    updated;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    updated);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                if (!registrations.TryGetValue(
                        runtimeInstanceId,
                        out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                        null);
                }

                var updated =
                    CloneWithStatus(
                        existing,
                        AiRuntimeInstanceStatus.Stopped,
                        canAcceptRun: false);

                registrations[runtimeInstanceId] =
                    updated;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    updated);
            }

            private static AiRuntimeInstanceSnapshot CreateSnapshot(
                AiRuntimeInstanceRegistration registration,
                AiRuntimeInstanceStatus status,
                bool isQueuePaused,
                bool canAcceptRun)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = registration.RuntimeInstanceId,
                    ControlPlaneId = registration.ControlPlaneId,
                    ControlPlaneHostId = registration.ControlPlaneHostId,
                    HostId = registration.HostId,
                    RuntimeId = registration.RuntimeId,
                    Role = registration.Role,
                    Status = status,
                    WorkerCount = registration.WorkerCount,
                    QueueCapacity = registration.QueueCapacity,
                    MaxConcurrentRuns = registration.MaxConcurrentRuns,
                    RegisteredAtUtc = registration.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    AvailableRunSlots = registration.MaxConcurrentRuns,
                    ActiveWorkerCount = 0,
                    AvailableWorkerCount = registration.WorkerCount,
                    MaxLocalWorkersPerExecution = registration.WorkerCount,
                    IsQueuePaused = isQueuePaused,
                    CanAcceptRun = canAcceptRun,
                    Metadata = registration.Metadata
                };
            }

            private static AiRuntimeInstanceSnapshot CloneWithStatus(
                AiRuntimeInstanceSnapshot existing,
                AiRuntimeInstanceStatus status,
                bool canAcceptRun)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    ControlPlaneId = existing.ControlPlaneId,
                    ControlPlaneHostId = existing.ControlPlaneHostId,
                    HostId = existing.HostId,
                    RuntimeId = existing.RuntimeId,
                    Role = existing.Role,
                    Status = status,
                    WorkerCount = existing.WorkerCount,
                    QueueCapacity = existing.QueueCapacity,
                    MaxConcurrentRuns = existing.MaxConcurrentRuns,
                    RegisteredAtUtc = existing.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    QueuedRunCount = existing.QueuedRunCount,
                    RunningRunCount = existing.RunningRunCount,
                    ActiveRunCount = existing.ActiveRunCount,
                    AvailableRunSlots = existing.AvailableRunSlots,
                    ActiveWorkerCount = existing.ActiveWorkerCount,
                    AvailableWorkerCount = existing.AvailableWorkerCount,
                    MaxLocalWorkersPerExecution = existing.MaxLocalWorkersPerExecution,
                    IsQueuePaused = existing.IsQueuePaused,
                    CanAcceptRun = canAcceptRun,
                    Metadata = existing.Metadata
                };
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(string runtimeInstanceId, CancellationToken cancellationToken = default)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Test runtime instance capacity store used only to satisfy HTTP scale-out provisioner activation.
        /// </summary>
        private sealed class TestRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RuntimeInstanceId);

                descriptors[descriptor.RuntimeInstanceId] =
                    descriptor;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                descriptors.TryGetValue(
                    runtimeInstanceId,
                    out var descriptor);

                return Task.FromResult(descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                    descriptors
                        .Values
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                return Task.FromResult(
                    descriptors.Remove(runtimeInstanceId));
            }
        }
    }
}