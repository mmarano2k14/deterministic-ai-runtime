using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Provides identity information for a logical runtime worker.
    /// </summary>
    public interface IAiRuntimeInstanceWorkerIdentity
    {
        /// <summary>
        /// Gets the owning runtime instance identity descriptor.
        /// </summary>
        IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <summary>
        /// Gets the logical worker identifier.
        /// </summary>
        string WorkerId { get; }
    }
}