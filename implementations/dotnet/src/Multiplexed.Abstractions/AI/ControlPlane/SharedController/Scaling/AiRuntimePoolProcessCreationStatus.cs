namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines the outcome of one bounded runtime-process creation request inside an
    /// existing Runtime Pool host.
    /// </summary>
    public enum AiRuntimePoolProcessCreationStatus
    {
        /// <summary>
        /// The request increased the exact host process count.
        /// </summary>
        Created = 0,

        /// <summary>
        /// The same scale-out request was already applied and produced no additional
        /// process mutation.
        /// </summary>
        AlreadyApplied = 1,

        /// <summary>
        /// The selected host had already reached its authoritative maximum process
        /// count.
        /// </summary>
        CapacityUnavailable = 2
    }
}
