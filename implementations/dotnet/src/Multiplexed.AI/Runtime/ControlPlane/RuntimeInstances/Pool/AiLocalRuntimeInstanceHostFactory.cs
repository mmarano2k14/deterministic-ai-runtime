using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
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

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstanceHostFactory"/> class.
        /// </summary>
        public AiLocalRuntimeInstanceHostFactory(
            IAiLocalRuntimeInstanceServiceCollectionProvider servicesProvider,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiExecutionReplayMetadataStore replayMetadataStore,
            IAiRuntimeObservability observability)
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

        }

        /// <inheritdoc />
        public async Task<IAiLocalRuntimeInstanceHost> CreateAsync(
            string runtimeInstanceId,
            int workerCount,
            int maxConcurrentRuns,
            int? localQueueCapacity,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRuns);

            cancellationToken.ThrowIfCancellationRequested();

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

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService,
                    AiRuntimeInstanceRegistrationHostedService>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService,
                    AiRuntimePipelineBackgroundControllerHostedService>());

            /*
            services.RemoveAll<IAiRuntimeInstanceIdentity>();
            services.AddSingleton<IAiRuntimeInstanceIdentity>(
                _ => new DefaultAiRuntimeInstanceIdentity(runtimeInstanceId));
            */

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
            });

            services.Configure<AiEngineOptions>(options =>
            {
                options.PipelineBackgroundController.MaxConcurrentRuns =
                    maxConcurrentRuns;

                if (localQueueCapacity.HasValue && localQueueCapacity.Value > 0)
                {
                    options.PipelineBackgroundController.QueueCapacity = localQueueCapacity.Value;
                }

                options.PipelineBackgroundController.Distributed.Enabled = true;
                options.PipelineBackgroundController.Distributed.WorkerCount = workerCount;
                options.PipelineBackgroundController.RejectEnqueueWhenStopped = false;
                options.PipelineBackgroundController.StopOnFirstFailure = false;
            });

            var serviceProvider =
                services.BuildServiceProvider();

            var controller =
                serviceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            var queueControlPlane =
                serviceProvider.GetRequiredService<IAiRuntimeQueueControlPlane>();

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"POOL INSTANCE CREATED RuntimeInstanceId={runtimeInstanceId}, QueueStateRuntimeInstanceId={queueState.RuntimeInstanceId}");

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
                    sharedRuntimeInstance);

            return host;
        }
    }
}