using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;


namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Creates isolated local runtime instance hosts.
    /// </summary>
    public sealed class AiLocalRuntimeInstanceHostFactory :
        IAiLocalRuntimeInstanceHostFactory
    {
        private readonly IAiLocalRuntimeInstanceServiceCollectionProvider servicesProvider;
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiExecutionReplayMetadataStore replayMetadataStore;
        private readonly IAiRuntimeObservability observability;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstanceHostFactory"/> class.
        /// </summary>
        public AiLocalRuntimeInstanceHostFactory(
            IAiLocalRuntimeInstanceServiceCollectionProvider servicesProvider,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiExecutionReplayMetadataStore replayMetadataStore,
            IAiRuntimeObservability observability,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
        {
            this.servicesProvider = servicesProvider
                ?? throw new ArgumentNullException(nameof(servicesProvider));

            this.runtimeInstanceRegistry = runtimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));

            this.sharedRuntimeInstanceRegistry = sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.replayMetadataStore = replayMetadataStore
                ?? throw new ArgumentNullException(nameof(replayMetadataStore));

            this.observability = observability
                ?? throw new ArgumentNullException(nameof(observability));

            this.controlPlaneIdResolver = controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
        }

        /// <inheritdoc />
        public async Task<IAiLocalRuntimeInstanceHost> CreateAsync(
            string runtimeInstanceId,
            int workerCount,
            int maxConcurrentRuns,
            int? localQueueCapacity,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRuns);

            cancellationToken.ThrowIfCancellationRequested();

            var identity =
                RuntimeInstanceIdentityParts.Parse(runtimeInstanceId);

            var effectiveMetadata =
                metadata is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        metadata,
                        StringComparer.OrdinalIgnoreCase);

            var controlPlaneMetadata =
                await this.controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Metadata = effectiveMetadata,
                            Source = "local-runtime-instance-host-factory",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var pair in controlPlaneMetadata)
            {
                effectiveMetadata[pair.Key] = pair.Value;
            }

            var services =
                new ServiceCollection();

            foreach (var descriptor in servicesProvider.Services)
            {
                if (descriptor.ServiceType == typeof(IHostedService))
                {
                    continue;
                }

                if (descriptor.ServiceType == typeof(IAiLocalRuntimeInstanceHostFactory))
                {
                    continue;
                }

                if (descriptor.ServiceType == typeof(IAiLocalRuntimeInstanceServiceCollectionProvider))
                {
                    continue;
                }

                services.Add(descriptor);
            }

            services.RemoveAll<IAiRuntimeObservability>();
            services.AddSingleton(observability);

            services.RemoveAll<IAiRuntimeInstanceRegistry>();
            services.AddSingleton(runtimeInstanceRegistry);

            services.RemoveAll<IAiSharedRuntimeInstanceRegistry>();
            services.AddSingleton(sharedRuntimeInstanceRegistry);

            services.RemoveAll<IAiExecutionReplayMetadataStore>();
            services.AddSingleton(replayMetadataStore);

            services.RemoveAll<IAiRuntimeEnvironmentProvider>();
            services.AddSingleton<IAiRuntimeEnvironmentProvider>(
                _ => new PooledLocalRuntimeEnvironmentProvider(
                    runtimeInstanceId,
                    identity.HostId,
                    identity.RuntimeId,
                    identity.ControlPlaneHostId,
                    effectiveMetadata));

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService,
                    AiRuntimeInstanceRegistrationHostedService>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService,
                    AiRuntimePipelineBackgroundControllerHostedService>());

            services.Configure<AiRuntimeInstanceRegistrationOptions>(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = runtimeInstanceId;
                options.WorkerCount = workerCount;
                options.MaxConcurrentRuns = maxConcurrentRuns;
                options.Role = AiRuntimeInstanceRole.Runtime;

                if (localQueueCapacity.HasValue)
                {
                    options.QueueCapacity = localQueueCapacity.Value;
                }

                var registrationMetadata =
                    new Dictionary<string, string>(
                        options.Metadata,
                        StringComparer.OrdinalIgnoreCase);

                var providerMetadata =
                    new Dictionary<string, string>(
                        options.ProviderMetadata,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var pair in effectiveMetadata)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    registrationMetadata[pair.Key] = pair.Value;
                    providerMetadata[pair.Key] = pair.Value;
                }

                options.Metadata = registrationMetadata;
                options.ProviderMetadata = providerMetadata;
            });

            services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
            services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                _ => new DefaultAiRuntimeInstanceIdentity(runtimeInstanceId));

            var serviceProvider =
                services.BuildServiceProvider();

            var logger =
                serviceProvider.GetRequiredService<ILogger<AiLocalRuntimeInstanceHost>>();

            var controller =
                serviceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            var queueControlPlane =
                serviceProvider.GetRequiredService<IAiRuntimeQueueControlPlane>();

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            logger.LogInformation(
                "Pool runtime instance created. RuntimeInstanceId={RuntimeInstanceId}, HostId={HostId}, RuntimeId={RuntimeId}, QueueStateRuntimeInstanceId={QueueStateRuntimeInstanceId}, MetadataCount={MetadataCount}, ControlPlaneId={ControlPlaneId}",
                runtimeInstanceId,
                identity.HostId,
                identity.RuntimeId,
                queueState.RuntimeInstanceId,
                effectiveMetadata.Count,
                controlPlaneMetadata[AiControlPlaneMetadataKeys.ControlPlaneId]);

            logger.LogInformation(
                "Pool runtime instance capacity resolved. RuntimeInstanceId={RuntimeInstanceId}, HostId={HostId}, RuntimeId={RuntimeId}, WorkerCountArg={WorkerCountArg}, MaxConcurrentRunsArg={MaxConcurrentRunsArg}, LocalQueueCapacityArg={LocalQueueCapacityArg}, QueueStateRuntimeInstanceId={QueueStateRuntimeInstanceId}, QueueStateMaxConcurrentRuns={QueueStateMaxConcurrentRuns}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, QueueStateRunningRunCount={QueueStateRunningRunCount}, QueueStateQueuedRunCount={QueueStateQueuedRunCount}, QueueStateQueueCapacity={QueueStateQueueCapacity}, MetadataCount={MetadataCount}",
                runtimeInstanceId,
                identity.HostId,
                identity.RuntimeId,
                workerCount,
                maxConcurrentRuns,
                localQueueCapacity?.ToString() ?? "null",
                queueState.RuntimeInstanceId,
                queueState.MaxConcurrentRuns,
                queueState.AvailableRunSlots,
                queueState.RunningRunCount,
                queueState.QueuedRunCount,
                queueState.QueueCapacity,
                effectiveMetadata.Count);

            var sharedRuntimeInstance =
                new LocalAiSharedRuntimeInstance(
                    runtimeInstanceId,
                    queueControlPlane);

            IAiLocalRuntimeInstanceHost host =
                new AiLocalRuntimeInstanceHost(
                    runtimeInstanceId,
                    workerCount,
                    serviceProvider,
                    controller,
                    queueControlPlane,
                    sharedRuntimeInstance,
                    logger);

            return host;
        }

        private sealed class RuntimeInstanceIdentityParts
        {
            private RuntimeInstanceIdentityParts(
                string hostId,
                string runtimeId,
                string controlPlaneHostId)
            {
                HostId = hostId;
                RuntimeId = runtimeId;
                ControlPlaneHostId = controlPlaneHostId;
            }

            public string HostId { get; }

            public string RuntimeId { get; }

            public string ControlPlaneHostId { get; }

            public static RuntimeInstanceIdentityParts Parse(
                string runtimeInstanceId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                var separatorIndex =
                    runtimeInstanceId.IndexOf(':', StringComparison.Ordinal);

                if (separatorIndex <= 0 ||
                    separatorIndex >= runtimeInstanceId.Length - 1)
                {
                    return new RuntimeInstanceIdentityParts(
                        runtimeInstanceId,
                        runtimeInstanceId,
                        runtimeInstanceId);
                }

                var hostId =
                    runtimeInstanceId[..separatorIndex];

                var runtimeId =
                    runtimeInstanceId[(separatorIndex + 1)..];

                return new RuntimeInstanceIdentityParts(
                    hostId,
                    runtimeId,
                    hostId);
            }
        }

        private sealed class PooledLocalRuntimeEnvironmentProvider :
            IAiRuntimeEnvironmentProvider
        {
            private readonly string runtimeInstanceId;
            private readonly string hostId;
            private readonly string runtimeId;
            private readonly string controlPlaneHostId;
            private readonly IReadOnlyDictionary<string, string> metadata;

            public PooledLocalRuntimeEnvironmentProvider(
                string runtimeInstanceId,
                string hostId,
                string runtimeId,
                string controlPlaneHostId,
                IReadOnlyDictionary<string, string> metadata)
            {
                this.runtimeInstanceId =
                    runtimeInstanceId ?? throw new ArgumentNullException(nameof(runtimeInstanceId));

                this.hostId =
                    hostId ?? throw new ArgumentNullException(nameof(hostId));

                this.runtimeId =
                    runtimeId ?? throw new ArgumentNullException(nameof(runtimeId));

                this.controlPlaneHostId =
                    controlPlaneHostId ?? throw new ArgumentNullException(nameof(controlPlaneHostId));

                this.metadata =
                    new Dictionary<string, string>(
                        metadata ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
            }

            public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processId =
                    Environment.ProcessId;

                var hostName =
                    Environment.MachineName;

                var providerMetadata =
                    new Dictionary<string, string>(
                        this.metadata,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] = AiRuntimeInstanceProviderNames.LocalPool,
                        ["machineName"] = hostName,
                        ["processId"] = processId.ToString(),
                        [AiRuntimeHostMetadataKeys.CamelCaseHostId] = hostId,
                        ["runtimeId"] = runtimeId,
                        ["controlPlaneHostId"] = controlPlaneHostId
                    };

                return Task.FromResult(
                    new AiRuntimeEnvironmentSnapshot
                    {
                        ProviderName = AiRuntimeInstanceProviderNames.LocalPool,
                        RuntimeInstanceId = runtimeInstanceId,
                        HostId = hostId,
                        RuntimeId = runtimeId,
                        ControlPlaneHostId = controlPlaneHostId,
                        HostName = hostName,
                        ProcessId = processId,
                        ProviderMetadata = providerMetadata
                    });
            }
        }
    }
}