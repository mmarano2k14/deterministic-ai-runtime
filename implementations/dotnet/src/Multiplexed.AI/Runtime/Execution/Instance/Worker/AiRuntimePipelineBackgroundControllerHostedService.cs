using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.Observability.Logging;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Starts and stops the runtime pipeline background controller automatically
    /// when the host starts and stops.
    /// </summary>
    public sealed class AiRuntimePipelineBackgroundControllerHostedService
        : IHostedService
    {
        private readonly IAiRuntimePipelineBackgroundController controller;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;
        private readonly IAiRuntimeLogger logger;
        private readonly IAiRuntimeObservability observability;

        private string? registeredRuntimeInstanceId;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePipelineBackgroundControllerHostedService"/> class.
        /// </summary>
        public AiRuntimePipelineBackgroundControllerHostedService(
            IAiRuntimePipelineBackgroundController controller,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability)
        {
            this.controller = controller
                ?? throw new ArgumentNullException(nameof(controller));

            this.sharedRuntimeInstanceRegistry = sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.runtimeQueueControlPlane = runtimeQueueControlPlane
                ?? throw new ArgumentNullException(nameof(runtimeQueueControlPlane));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));

            this.observability = observability
                ?? throw new ArgumentNullException(nameof(observability));
        }

        /// <inheritdoc />
        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER HOSTED SERVICE] Starting background controller.");

            Console.WriteLine(
                 $"[AI PIPELINE CONTROLLER HOSTED SERVICE] ControllerHash='{controller.GetHashCode()}' QueueControlPlaneHash='{runtimeQueueControlPlane.GetHashCode()}'");

            await controller
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);

            var queueState = await controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            registeredRuntimeInstanceId = queueState.RuntimeInstanceId;

            await sharedRuntimeInstanceRegistry
                .RegisterAsync(
                    new LocalAiSharedRuntimeInstance(
                        registeredRuntimeInstanceId,
                        runtimeQueueControlPlane),
                    cancellationToken)
                .ConfigureAwait(false);

            logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER HOSTED SERVICE] Background controller started and shared runtime instance registered. RuntimeInstanceId='{registeredRuntimeInstanceId}'.");

            /*
            observability.Metrics.Execution.RecordExecutionEvent(
                "pipeline-controller-started");
            */
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken)
        {
            logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER HOSTED SERVICE] Stopping background controller.");

            if (!string.IsNullOrWhiteSpace(registeredRuntimeInstanceId))
            {
                await sharedRuntimeInstanceRegistry
                    .UnregisterAsync(
                        registeredRuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await controller
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);

            logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER HOSTED SERVICE] Background controller stopped and shared runtime instance unregistered. RuntimeInstanceId='{registeredRuntimeInstanceId}'.");

            /*
            observability.Metrics.Execution.RecordExecutionEvent(
                "pipeline-controller-stopped");
            */
        }
    }
}