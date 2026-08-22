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
    /// Dispatches structured control-plane events to registered observability sinks.
    /// </summary>
    /// <remarks>
    /// Generic control-plane operation events preserve the historical fan-out behavior.
    /// Canonical semantic engine events are dispatched through the centralized
    /// <see cref="AiEngineEventProjectionCatalog"/> so projection selection and failure
    /// semantics remain authoritative in one place.
    /// </remarks>
    public sealed class CompositeAiControlPlaneObserver : IAiControlPlaneObserver
    {
        private readonly IReadOnlyList<IAiControlPlaneEventSink> sinks;
        private readonly IReadOnlyDictionary<AiEngineEventProjectionTarget, IAiControlPlaneEventProjectionSink> projectionSinks;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeAiControlPlaneObserver"/> class.
        /// </summary>
        /// <param name="sinks">The registered control-plane event sinks.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when more than one built-in projection sink owns the same centralized projection surface.
        /// </exception>
        public CompositeAiControlPlaneObserver(IEnumerable<IAiControlPlaneEventSink> sinks)
        {
            this.sinks = sinks?.ToArray() ?? Array.Empty<IAiControlPlaneEventSink>();
            this.projectionSinks = BuildProjectionSinkMap(this.sinks);
        }

        /// <inheritdoc />
        public Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            return string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType)
                ? this.RecordLegacyAsync(controlPlaneEvent, cancellationToken)
                : this.RecordCanonicalAsync(controlPlaneEvent, cancellationToken);
        }


        /// <summary>
        /// Determines whether this Event Manager owns the specified centralized projection surface.
        /// </summary>
        /// <param name="target">The projection target.</param>
        /// <returns><c>true</c> when a sink owns the target; otherwise, <c>false</c>.</returns>
        internal bool HasProjectionSink(AiEngineEventProjectionTarget target)
        {
            return this.projectionSinks.ContainsKey(target);
        }

        /// <summary>
        /// Creates a compatibility Event Manager that preserves the existing sink set and adds one
        /// missing centralized projection owner.
        /// </summary>
        /// <param name="projectionSink">The projection sink to add.</param>
        /// <returns>This observer when the target is already owned; otherwise a composite containing all existing sinks plus the projection.</returns>
        internal CompositeAiControlPlaneObserver WithProjectionSink(
            IAiControlPlaneEventProjectionSink projectionSink)
        {
            ArgumentNullException.ThrowIfNull(projectionSink);

            if (this.HasProjectionSink(projectionSink.ProjectionTarget))
            {
                return this;
            }

            return new CompositeAiControlPlaneObserver(
                this.sinks.Concat(new IAiControlPlaneEventSink[] { projectionSink }));
        }

        /// <summary>
        /// Preserves the historical fan-out behavior for generic non-semantic control-plane events.
        /// </summary>
        /// <param name="controlPlaneEvent">The generic control-plane event.</param>
        /// <param name="cancellationToken">A token used to cancel observation.</param>
        /// <returns>A task representing the asynchronous fan-out operation.</returns>
        /// <exception cref="Exception">
        /// Re-throws the first sink failure after all remaining legacy sinks have been attempted.
        /// </exception>
        private async Task RecordLegacyAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken)
        {
            Exception? firstException = null;

            foreach (var sink in this.sinks)
            {
                try
                {
                    await sink.RecordAsync(controlPlaneEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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

        /// <summary>
        /// Dispatches a canonical semantic event according to the central projection catalog.
        /// </summary>
        /// <param name="controlPlaneEvent">The canonical semantic event.</param>
        /// <param name="cancellationToken">A token used to cancel projection.</param>
        /// <returns>A task representing the asynchronous projection operation.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the event is not cataloged, a mandatory projection sink is missing,
        /// or a required durable projection fails.
        /// </exception>
        private async Task RecordCanonicalAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken)
        {
            var semanticEventType = controlPlaneEvent.SemanticEventType!;
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(semanticEventType);

            this.ValidateRequiredProjectionSinks(descriptor);

            Exception? firstRequiredException = null;

            foreach (var sink in this.sinks)
            {
                if (sink is not IAiControlPlaneEventProjectionSink projectionSink)
                {
                    // Generic extension sinks remain part of the legacy control-plane fan-out only.
                    // Canonical semantic events are dispatched exclusively through projection sinks
                    // whose surface is represented in the central projection catalog.
                    continue;
                }

                var requirement = descriptor.GetRequirement(projectionSink.ProjectionTarget);

                if (requirement == AiEngineEventProjectionRequirement.None)
                {
                    continue;
                }

                try
                {
                    await projectionSink
                        .RecordAsync(controlPlaneEvent, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (requirement == AiEngineEventProjectionRequirement.BestEffort)
                    {
                        continue;
                    }

                    firstRequiredException ??= exception;
                }
            }

            if (firstRequiredException is not null)
            {
                throw firstRequiredException;
            }
        }

        /// <summary>
        /// Validates that every mandatory durable projection has exactly one registered sink owner.
        /// </summary>
        /// <param name="descriptor">The canonical event projection descriptor.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a required or replayable durable projection sink is not registered.
        /// </exception>
        private void ValidateRequiredProjectionSinks(
            AiEngineEventProjectionDescriptor descriptor)
        {
            foreach (var target in Enum.GetValues<AiEngineEventProjectionTarget>())
            {
                var requirement = descriptor.GetRequirement(target);

                if (requirement != AiEngineEventProjectionRequirement.RequiredDurable &&
                    requirement != AiEngineEventProjectionRequirement.ReplayableDurable)
                {
                    continue;
                }

                if (this.projectionSinks.ContainsKey(target))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Canonical engine event '{descriptor.EventType}' requires projection '{target}' " +
                    $"with delivery contract '{requirement}', but no projection sink is registered.");
            }
        }

        /// <summary>
        /// Builds the unique projection-surface ownership map from registered control-plane sinks.
        /// </summary>
        /// <param name="sinks">The registered control-plane event sinks.</param>
        /// <returns>The projection sink keyed by its centralized projection target.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when more than one projection sink owns the same target.
        /// </exception>
        private static IReadOnlyDictionary<AiEngineEventProjectionTarget, IAiControlPlaneEventProjectionSink> BuildProjectionSinkMap(
            IReadOnlyList<IAiControlPlaneEventSink> sinks)
        {
            var projectionSinks = new Dictionary<AiEngineEventProjectionTarget, IAiControlPlaneEventProjectionSink>();

            foreach (var projectionSink in sinks.OfType<IAiControlPlaneEventProjectionSink>())
            {
                if (projectionSinks.TryAdd(projectionSink.ProjectionTarget, projectionSink))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"More than one control-plane projection sink is registered for '{projectionSink.ProjectionTarget}'. " +
                    "A centralized projection surface must have exactly one sink owner.");
            }

            return projectionSinks;
        }
    }
}
