using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Represents a locally hosted runtime instance.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Encapsulates a logical runtime instance.
    /// - Owns a runtime controller.
    /// - Owns a local runtime queue.
    /// - Owns a set of workers.
    /// - Can be registered into a shared runtime registry.
    ///
    /// IMPORTANT:
    /// - Multiple local runtime instances may coexist inside a single process.
    /// - Each runtime instance should have a unique RuntimeInstanceId.
    /// - Future implementations may create isolated service providers per instance.
    /// </remarks>
    public sealed class AiLocalRuntimeInstanceHost :
        IAiLocalRuntimeInstanceHost
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstanceHost"/> class.
        /// </summary>
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

        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the worker count assigned to the runtime instance.
        /// </summary>
        public int WorkerCount { get; }

        /// <summary>
        /// Gets the service provider associated with the runtime instance.
        /// </summary>
        public IServiceProvider ServiceProvider { get; }

        /// <inheritdoc />
        public IAiRuntimePipelineBackgroundController Controller { get; }

        /// <inheritdoc />
        public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        /// <inheritdoc />
        public IAiSharedRuntimeInstance SharedRuntimeInstance { get; }

        /// <inheritdoc />
        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            await Controller
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            await Controller
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
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