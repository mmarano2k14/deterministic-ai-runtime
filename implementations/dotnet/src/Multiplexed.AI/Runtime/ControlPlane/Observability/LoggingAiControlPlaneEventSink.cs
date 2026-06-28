using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.AI.Runtime.Observability.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Writes structured control-plane events to the runtime logging layer.
    /// </summary>
    public sealed class LoggingAiControlPlaneEventSink : IAiControlPlaneEventSink
    {
        private readonly IAiControlPlaneLogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LoggingAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="logger">The control-plane logger.</param>
        public LoggingAiControlPlaneEventSink(IAiControlPlaneLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            this.logger.LogControlPlaneEvent(controlPlaneEvent);

            return Task.CompletedTask;
        }
    }
}