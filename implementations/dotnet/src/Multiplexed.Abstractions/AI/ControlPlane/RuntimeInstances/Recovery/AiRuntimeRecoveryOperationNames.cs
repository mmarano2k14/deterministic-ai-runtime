namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>Defines stable operation names used by runtime execution recovery.</summary>
    public static class AiRuntimeRecoveryOperationNames
    {
        /// <summary>Gets the operation name used for runtime execution recovery reconciliation.</summary>
        public const string ExecutionRecoveryReconcile = "runtime-execution-recovery-reconcile";

        /// <summary>Gets the operation name used to requeue an in-flight execution for recovery.</summary>
        public const string ExecutionRecoveryRequeue = "runtime-execution-recovery-requeue";

        /// <summary>Gets the operation name used to requeue local queued work for recovery.</summary>
        public const string LocalQueuedRecoveryRequeue = "runtime-local-queued-recovery-requeue";
    }
}
