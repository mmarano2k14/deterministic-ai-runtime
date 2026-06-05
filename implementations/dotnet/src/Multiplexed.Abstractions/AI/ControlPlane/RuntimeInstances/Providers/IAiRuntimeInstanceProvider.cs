using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Base contract for runtime instance providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A runtime instance provider knows how to communicate with runtime instances
    /// represented by capacity descriptors.
    /// </para>
    /// <para>
    /// This base contract intentionally stays small. Operational capabilities are
    /// exposed through focused provider interfaces such as dispatch, status, control,
    /// capacity, and scaling providers.
    /// </para>
    /// <para>
    /// Providers must not replace local runtime queues or bypass the DAG execution
    /// engine. They only provide the transport/control bridge to a runtime instance.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Determines whether this provider can handle the specified runtime instance descriptor.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <returns>
        /// <see langword="true"/> if the provider can handle the descriptor; otherwise,
        /// <see langword="false"/>.
        /// </returns>
        bool CanHandle(
            AiRuntimeInstanceCapacityDescriptor descriptor);
    }
}