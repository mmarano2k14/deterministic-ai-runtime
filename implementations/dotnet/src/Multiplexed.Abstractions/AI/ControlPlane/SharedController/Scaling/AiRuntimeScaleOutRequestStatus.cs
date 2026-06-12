namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines the lifecycle status of a runtime scale-out request.
    /// </summary>
    /// <remarks>
    /// Scale-out requests represent operational intent produced by the control plane
    /// when admission cannot find enough available runtime capacity. They do not
    /// directly create infrastructure. External scalers may observe and fulfill them.
    /// </remarks>
    public enum AiRuntimeScaleOutRequestStatus
    {
        /// <summary>
        /// The request has been created and is waiting to be observed by a scaler.
        /// </summary>
        Pending = 0,

        /// <summary>
        /// The request has been observed by a scaler or control-plane consumer.
        /// </summary>
        Observed = 1,

        /// <summary>
        /// The requested capacity has been provisioned or otherwise satisfied.
        /// </summary>
        Fulfilled = 2,

        /// <summary>
        /// The request was rejected and will not be fulfilled.
        /// </summary>
        Rejected = 3,

        /// <summary>
        /// The request expired before it could be fulfilled.
        /// </summary>
        Expired = 4,

        /// <summary>
        /// The request was cancelled before completion.
        /// </summary>
        Cancelled = 5
    }
}