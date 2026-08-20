namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>Defines stable identifier prefixes used to correlate runtime failure incidents.</summary>
    public static class AiRuntimeFailureIdentifiers
    {
        /// <summary>Gets the prefix used to construct durable runtime failure incident identifiers.</summary>
        public const string RuntimeFailureIncidentPrefix = "runtime-failure";
    }
}
