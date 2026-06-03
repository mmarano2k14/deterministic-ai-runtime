using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
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

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstanceHostFactory"/> class.
        /// </summary>
        public AiLocalRuntimeInstanceHostFactory(
            IAiLocalRuntimeInstanceServiceCollectionProvider servicesProvider)
        {
            this.servicesProvider = servicesProvider
                ?? throw new ArgumentNullException(nameof(servicesProvider));
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