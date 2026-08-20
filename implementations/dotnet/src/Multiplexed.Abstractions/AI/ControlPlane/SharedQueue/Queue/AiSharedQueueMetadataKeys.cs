namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue
{
    /// <summary>
    /// Defines canonical metadata keys used by the shared queue contract.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names used by shared queue
    /// implementations and recovery transitions. They do not change queue ordering or
    /// recovery behavior.
    /// </remarks>
    public static class AiSharedQueueMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the shared queue priority.
        /// </summary>
        public const string Priority = "queue.priority";
    }
}
