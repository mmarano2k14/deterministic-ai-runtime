using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
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
        private readonly IAiRuntimeLogger logger;
        private readonly IAiRuntimeObservability observability;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePipelineBackgroundControllerHostedService"/> class.
        /// </summary>
        public AiRuntimePipelineBackgroundControllerHostedService(
            IAiRuntimePipelineBackgroundController controller,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability)
        {
            this.controller = controller
                ?? throw new ArgumentNullException(nameof(controller));

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

            await controller
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);

            logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER HOSTED SERVICE] Background controller started.");

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

            await controller
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);

            logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER HOSTED SERVICE] Background controller stopped.");
            
            /*  
            observability.Metrics.Execution.RecordExecutionEvent(
                "pipeline-controller-stopped");
            */
        }
    }
}