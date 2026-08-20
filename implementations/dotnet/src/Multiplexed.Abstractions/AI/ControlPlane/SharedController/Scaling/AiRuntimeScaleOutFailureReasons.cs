namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines transport-neutral failure reason codes shared by runtime scale-out providers.
    /// </summary>
    public static class AiRuntimeScaleOutFailureReasons
    {
        /// <summary>Runtime readiness validation failed.</summary>
        public const string RuntimeReadinessFailed = "runtime-readiness-failed";

        /// <summary>The runtime host manager failed to start a runtime host.</summary>
        public const string RuntimeHostStartFailed = "runtime-host-start-failed";

        /// <summary>Process-host control required for the operation is unavailable.</summary>
        public const string ProcessControlUnavailable = "process-control-unavailable";

        /// <summary>The host manager returned a runtime instance that was explicitly excluded.</summary>
        public const string RuntimeHostStartedWithExcludedRuntimeInstanceId = "runtime-host-started-with-excluded-runtime-instance-id";

        /// <summary>The host manager reported success without returning a runtime instance identifier.</summary>
        public const string RuntimeHostStartedWithoutRuntimeInstanceId = "runtime-host-started-without-runtime-instance-id";

        /// <summary>Readiness resolved to a runtime instance that was explicitly excluded.</summary>
        public const string RuntimeReadinessReturnedExcludedRuntimeInstanceId = "runtime-readiness-returned-excluded-runtime-instance-id";

        /// <summary>Kubernetes Runtime Pool Pod creation was rejected.</summary>
        public const string KubernetesRuntimePoolPodCreateRejected = "kubernetes-runtime-pool-pod-create-rejected";
    }
}
