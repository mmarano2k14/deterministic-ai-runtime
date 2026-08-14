namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement
{
    /// <summary>
    /// Defines what admission may do when a placement target cannot be selected.
    /// </summary>
    public enum AiRunPlacementFallback
    {
        /// <summary>
        /// Continue normal admission against any compatible runtime capacity.
        /// </summary>
        AnyCompatibleCapacity = 0,

        /// <summary>
        /// Queue the run globally when the control plane allows global queue fallback.
        /// </summary>
        GlobalQueue = 1,

        /// <summary>
        /// Reject the admission request instead of silently selecting another target.
        /// </summary>
        Reject = 2
    }
}
