namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Marks a control-plane event sink as the owner of one centralized observability projection surface.
    /// </summary>
    /// <remarks>
    /// Generic extension sinks may continue to implement <see cref="IAiControlPlaneEventSink"/> directly.
    /// Built-in canonical projection sinks implement this interface so the Event Manager can apply the
    /// authoritative event-to-projection contract without duplicating <c>CanHandle</c> logic in each sink.
    /// </remarks>
    public interface IAiControlPlaneEventProjectionSink : IAiControlPlaneEventSink
    {
        /// <summary>
        /// Gets the centralized projection surface owned by this sink.
        /// </summary>
        AiEngineEventProjectionTarget ProjectionTarget { get; }
    }
}
