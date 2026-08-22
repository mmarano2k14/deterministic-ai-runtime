using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support
{
    /// <summary>
    /// Captures canonical control-plane events emitted by Child DAG composition tests.
    /// </summary>
    internal sealed class CapturingAiControlPlaneObserver : IAiControlPlaneObserver
    {
        /// <summary>
        /// Gets the recorded events in emission order.
        /// </summary>
        public List<AiControlPlaneEvent> Events { get; } = [];

        /// <inheritdoc />
        public Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);
            cancellationToken.ThrowIfCancellationRequested();
            this.Events.Add(controlPlaneEvent);
            return Task.CompletedTask;
        }
    }
}
