using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Dispatches each structured control-plane event to all registered observability sinks.
    /// </summary>
    public sealed class CompositeAiControlPlaneObserver : IAiControlPlaneObserver
    {
        private readonly IReadOnlyList<IAiControlPlaneEventSink> sinks;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeAiControlPlaneObserver"/> class.
        /// </summary>
        /// <param name="sinks">The registered control-plane event sinks.</param>
        public CompositeAiControlPlaneObserver(IEnumerable<IAiControlPlaneEventSink> sinks)
        {
            this.sinks = sinks?.ToArray() ?? Array.Empty<IAiControlPlaneEventSink>();
        }

        /// <inheritdoc />
        public async Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            Exception? firstException = null;

            foreach (var sink in this.sinks)
            {
                try
                {
                    await sink.RecordAsync(controlPlaneEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    firstException ??= exception;
                }
            }

            if (firstException is not null)
            {
                throw firstException;
            }
        }
    }
}