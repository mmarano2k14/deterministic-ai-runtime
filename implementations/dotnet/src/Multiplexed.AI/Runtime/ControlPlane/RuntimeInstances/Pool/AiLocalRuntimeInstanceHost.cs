using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Represents a locally hosted runtime instance.
    /// </summary>
    public sealed class AiLocalRuntimeInstanceHost :
        IAiLocalRuntimeInstanceHost
    {
        private readonly ILogger<AiLocalRuntimeInstanceHost> logger;
        private readonly List<IHostedService> hostedServices = new();

        public AiLocalRuntimeInstanceHost(
            string runtimeInstanceId,
            int workerCount,
            IServiceProvider serviceProvider,
            IAiRuntimePipelineBackgroundController controller,
            IAiRuntimeQueueControlPlane queueControlPlane,
            IAiSharedRuntimeInstance sharedRuntimeInstance,
            ILogger<AiLocalRuntimeInstanceHost> logger)
        {
            RuntimeInstanceId =
                runtimeInstanceId
                ?? throw new ArgumentNullException(nameof(runtimeInstanceId));

            WorkerCount = workerCount;

            ServiceProvider =
                serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));

            Controller =
                controller
                ?? throw new ArgumentNullException(nameof(controller));

            QueueControlPlane =
                queueControlPlane
                ?? throw new ArgumentNullException(nameof(queueControlPlane));

            SharedRuntimeInstance =
                sharedRuntimeInstance
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstance));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        public string RuntimeInstanceId { get; }

        public int WorkerCount { get; }

        public IServiceProvider ServiceProvider { get; }

        public IAiRuntimePipelineBackgroundController Controller { get; }

        public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        public IAiSharedRuntimeInstance SharedRuntimeInstance { get; }

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "Starting local runtime instance host. RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}",
                RuntimeInstanceId,
                WorkerCount);

            hostedServices.Clear();

            hostedServices.AddRange(
                ServiceProvider.GetServices<IHostedService>());

            logger.LogInformation(
                "Local runtime instance hosted services resolved. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceCount={HostedServiceCount}, HostedServices={HostedServices}",
                RuntimeInstanceId,
                hostedServices.Count,
                string.Join(
                    " | ",
                    hostedServices.Select(service => service.GetType().FullName)));

            if (hostedServices.Count == 0)
            {
                logger.LogInformation(
                    "No hosted services found for local runtime instance. Starting controller directly. RuntimeInstanceId={RuntimeInstanceId}",
                    RuntimeInstanceId);

                await Controller
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Local runtime instance controller started directly. RuntimeInstanceId={RuntimeInstanceId}",
                    RuntimeInstanceId);

                return;
            }

            foreach (var hostedService in hostedServices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogInformation(
                    "Starting local runtime instance hosted service. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceType={HostedServiceType}",
                    RuntimeInstanceId,
                    hostedService.GetType().FullName);

                await hostedService
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Local runtime instance hosted service started. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceType={HostedServiceType}",
                    RuntimeInstanceId,
                    hostedService.GetType().FullName);
            }

            logger.LogInformation(
                "Local runtime instance host started. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceCount={HostedServiceCount}",
                RuntimeInstanceId,
                hostedServices.Count);
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "Stopping local runtime instance host. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceCount={HostedServiceCount}",
                RuntimeInstanceId,
                hostedServices.Count);

            if (hostedServices.Count > 0)
            {
                foreach (var hostedService in hostedServices.AsEnumerable().Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    logger.LogInformation(
                        "Stopping local runtime instance hosted service. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceType={HostedServiceType}",
                        RuntimeInstanceId,
                        hostedService.GetType().FullName);

                    await hostedService
                        .StopAsync(cancellationToken)
                        .ConfigureAwait(false);

                    logger.LogInformation(
                        "Local runtime instance hosted service stopped. RuntimeInstanceId={RuntimeInstanceId}, HostedServiceType={HostedServiceType}",
                        RuntimeInstanceId,
                        hostedService.GetType().FullName);
                }

                hostedServices.Clear();

                logger.LogInformation(
                    "Local runtime instance host stopped. RuntimeInstanceId={RuntimeInstanceId}",
                    RuntimeInstanceId);

                return;
            }

            logger.LogInformation(
                "No hosted services tracked for local runtime instance. Stopping controller directly. RuntimeInstanceId={RuntimeInstanceId}",
                RuntimeInstanceId);

            await Controller
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Local runtime instance controller stopped directly. RuntimeInstanceId={RuntimeInstanceId}",
                RuntimeInstanceId);
        }

        public async ValueTask DisposeAsync()
        {
            logger.LogInformation(
                "Disposing local runtime instance host. RuntimeInstanceId={RuntimeInstanceId}",
                RuntimeInstanceId);

            await StopAsync()
                .ConfigureAwait(false);

            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable
                    .DisposeAsync()
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Local runtime instance service provider disposed asynchronously. RuntimeInstanceId={RuntimeInstanceId}",
                    RuntimeInstanceId);
            }
            else if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();

                logger.LogInformation(
                    "Local runtime instance service provider disposed. RuntimeInstanceId={RuntimeInstanceId}",
                    RuntimeInstanceId);
            }
        }
    }
}