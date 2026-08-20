namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>Defines stable status values used by runtime recovery transition evidence.</summary>
    public static class AiRuntimeRecoveryTransitionStatuses
    {
        /// <summary>The assigned work was released so recovery can redispatch it.</summary>
        public const string ReleasedForRecovery = "released-for-recovery";
    }
}
