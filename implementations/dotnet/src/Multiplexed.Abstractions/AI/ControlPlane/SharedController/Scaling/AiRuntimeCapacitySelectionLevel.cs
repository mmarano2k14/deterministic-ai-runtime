namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Identifies one ordered level in hierarchical runtime capacity selection.
    /// </summary>
    /// <remarks>
    /// Values are ordered from the least expensive reusable capacity action to the
    /// final backpressure outcome. Selection implementations must preserve this order.
    /// </remarks>
    public enum AiRuntimeCapacitySelectionLevel
    {
        /// <summary>
        /// Reserves an idle compatible runtime instance that is already warm.
        /// </summary>
        CompatibleWarmRuntime = 0,

        /// <summary>
        /// Reserves an available run slot on an existing compatible runtime instance
        /// hosted by a Runtime Pool Pod.
        /// </summary>
        ExistingPoolRuntimeSlot = 1,

        /// <summary>
        /// Creates a new runtime process inside an existing Runtime Pool Pod that still
        /// exposes process capacity.
        /// </summary>
        ExistingPoolPodProcessCreation = 2,

        /// <summary>
        /// Creates a new Runtime Pool Pod when no existing Pod can provide additional
        /// runtime process capacity.
        /// </summary>
        RuntimePoolPodCreation = 3,

        /// <summary>
        /// Requests external Kubernetes node capacity when another Runtime Pool Pod
        /// cannot currently be scheduled.
        /// </summary>
        ExternalNodeCapacityRequest = 4,

        /// <summary>
        /// Applies backpressure because no safe capacity level can currently satisfy
        /// the request.
        /// </summary>
        Backpressure = 5
    }
}
