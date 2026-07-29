namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement
{
    /// <summary>
    /// Represents a typed, provider-neutral placement directive for one run admission attempt.
    /// </summary>
    /// <remarks>
    /// The directive is intentionally separate from free-form metadata. It can evolve from
    /// exact runtime selection to host, pool, and node-aware capacity selection while keeping
    /// placement identities explicit and strongly typed.
    /// </remarks>
    public sealed class AiRunPlacementDirective
    {
        /// <summary>
        /// Gets the hierarchical placement target.
        /// </summary>
        public required AiRunPlacementTarget Target { get; init; }

        /// <summary>
        /// Gets how strongly admission must honor the target.
        /// </summary>
        public AiRunPlacementRequirement Requirement { get; init; } =
            AiRunPlacementRequirement.Preferred;

        /// <summary>
        /// Gets the behavior to apply when the requested target cannot be selected.
        /// </summary>
        public AiRunPlacementFallback Fallback { get; init; } =
            AiRunPlacementFallback.AnyCompatibleCapacity;
    }
}
