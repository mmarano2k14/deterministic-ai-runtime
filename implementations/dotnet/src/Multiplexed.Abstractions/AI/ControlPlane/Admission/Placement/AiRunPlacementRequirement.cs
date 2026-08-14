namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement
{
    /// <summary>
    /// Defines how strongly run admission must honor a placement target.
    /// </summary>
    public enum AiRunPlacementRequirement
    {
        /// <summary>
        /// Admission should try the requested target first and may use the configured fallback.
        /// </summary>
        Preferred = 0,

        /// <summary>
        /// Admission must not silently select another target unless the configured fallback explicitly allows it.
        /// </summary>
        Required = 1
    }
}
