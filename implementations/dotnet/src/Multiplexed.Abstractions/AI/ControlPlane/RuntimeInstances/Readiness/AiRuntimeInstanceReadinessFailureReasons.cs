namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness
{
    /// <summary>
    /// Defines stable failure reason codes emitted by runtime instance readiness validation.
    /// </summary>
    public static class AiRuntimeInstanceReadinessFailureReasons
    {
        /// <summary>Readiness did not converge before the configured timeout.</summary>
        public const string Timeout = "runtime-readiness-timeout";

        /// <summary>Readiness validation was cancelled.</summary>
        public const string Cancelled = "runtime-readiness-cancelled";

        /// <summary>Readiness validation failed with an unexpected exception.</summary>
        public const string Exception = "runtime-readiness-exception";

        /// <summary>The exact runtime instance was missing from the registry.</summary>
        public const string ExactRegistryMissing = "runtime-readiness-exact-registry-missing";

        /// <summary>No compatible runtime instance was present in the registry.</summary>
        public const string CompatibleRegistryMissing = "runtime-readiness-compatible-registry-missing";

        /// <summary>The observed runtime belongs to another control plane.</summary>
        public const string ControlPlaneMismatch = "runtime-readiness-control-plane-mismatch";

        /// <summary>The observed runtime tenant does not match the request.</summary>
        public const string TenantMismatch = "runtime-readiness-tenant-mismatch";

        /// <summary>The observed runtime tenant group does not match the request.</summary>
        public const string TenantGroupMismatch = "runtime-readiness-tenant-group-mismatch";

        /// <summary>The runtime instance is not ready.</summary>
        public const string NotReady = "runtime-readiness-not-ready";

        /// <summary>The runtime instance cannot currently accept a run.</summary>
        public const string CannotAcceptRun = "runtime-readiness-cannot-accept-run";

        /// <summary>The runtime instance has no available capacity.</summary>
        public const string CapacityUnavailable = "runtime-readiness-capacity-unavailable";

        /// <summary>The runtime transport endpoint is missing.</summary>
        public const string TransportEndpointMissing = "runtime-readiness-transport-endpoint-missing";

        /// <summary>The runtime transport endpoint is invalid.</summary>
        public const string TransportEndpointInvalid = "runtime-readiness-transport-endpoint-invalid";

        /// <summary>The runtime command endpoint is missing.</summary>
        public const string CommandEndpointMissing = "runtime-readiness-command-endpoint-missing";

        /// <summary>The readiness transport probe timed out.</summary>
        public const string TransportTimeout = "runtime-readiness-transport-timeout";

        /// <summary>The readiness transport endpoint could not be reached.</summary>
        public const string TransportUnreachable = "runtime-readiness-transport-unreachable";

        /// <summary>The readiness transport probe returned invalid data.</summary>
        public const string TransportInvalid = "runtime-readiness-transport-invalid";
    }
}
