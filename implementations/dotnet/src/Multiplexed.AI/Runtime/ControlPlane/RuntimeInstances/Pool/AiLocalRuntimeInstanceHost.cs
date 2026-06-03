using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        private readonly List<IHostedService> hostedServices = new();

        public AiLocalRuntimeInstanceHost(
            string runtimeInstanceId,
            int workerCount,
            IServiceProvider serviceProvider,
            IAiRuntimePipelineBackgroundController controller,
            IAiRuntimeQueueControlPlane queueControlPlane,
            IAiSharedRuntimeInstance sharedRuntimeInstance)
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
            hostedServices.Clear();

            hostedServices.AddRange(
                ServiceProvider.GetServices<IHostedService>());

            if (hostedServices.Count == 0)
            {
                await Controller
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            foreach (var hostedService in hostedServices)
            {
                await hostedService
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            if (hostedServices.Count > 0)
            {
                foreach (var hostedService in hostedServices.AsEnumerable().Reverse())
                {
                    await hostedService
                        .StopAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                hostedServices.Clear();

                return;
            }

            await Controller
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync()
                .ConfigureAwait(false);

            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
            else if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}