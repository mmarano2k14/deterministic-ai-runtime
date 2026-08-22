namespace Multiplexed.Abstractions.AI.Observability.Events
{
    /// <summary>
    /// Defines canonical runtime infrastructure lifecycle event types.
    /// </summary>
    /// <remarks>
    /// These declarations are shared by production emitters, lifecycle journal projections,
    /// diagnostics, and tests. Their persisted string values must remain stable.
    /// </remarks>
    public static class AiRuntimeLifecycleEvents
    {
        /// <summary>
        /// Indicates that host creation was requested.
        /// </summary>
        public const string HostCreationRequested = "host.creation.requested";

        /// <summary>
        /// Indicates that host creation started.
        /// </summary>
        public const string HostCreationStarted = "host.creation.started";

        /// <summary>
        /// Indicates that host creation succeeded.
        /// </summary>
        public const string HostCreationSucceeded = "host.creation.succeeded";

        /// <summary>
        /// Indicates that host creation failed.
        /// </summary>
        public const string HostCreationFailed = "host.creation.failed";

        /// <summary>
        /// Indicates that a runtime instance was registered.
        /// </summary>
        public const string RuntimeRegistered = "runtime.registered";

        /// <summary>
        /// Indicates that a runtime instance became ready.
        /// </summary>
        public const string RuntimeReady = "runtime.ready";

        /// <summary>
        /// Indicates that a runtime instance entered draining state.
        /// </summary>
        public const string RuntimeDraining = "runtime.draining";

        /// <summary>
        /// Indicates that a runtime instance was suppressed.
        /// </summary>
        public const string RuntimeSuppressed = "runtime.suppressed";

        /// <summary>
        /// Indicates that a runtime instance became unhealthy.
        /// </summary>
        public const string RuntimeUnhealthy = "runtime.unhealthy";

        /// <summary>
        /// Indicates that a runtime instance stopped.
        /// </summary>
        public const string RuntimeStopped = "runtime.stopped";

        /// <summary>
        /// Indicates that host deletion was requested.
        /// </summary>
        public const string HostDeletionRequested = "host.deletion.requested";

        /// <summary>
        /// Indicates that a host was deleted.
        /// </summary>
        public const string HostDeleted = "host.deleted";

        /// <summary>
        /// Indicates that a previously known host disappeared.
        /// </summary>
        public const string HostDisappeared = "host.disappeared";

        /// <summary>
        /// Indicates that runtime replacement was requested.
        /// </summary>
        public const string RuntimeReplacementRequested = "runtime.replacement.requested";

        /// <summary>
        /// Indicates that a replacement runtime was registered.
        /// </summary>
        public const string RuntimeReplacementRegistered = "runtime.replacement.registered";

        /// <summary>
        /// Indicates that work was assigned to a runtime.
        /// </summary>
        public const string WorkAssigned = "work.assigned";

        /// <summary>
        /// Indicates that work was reassigned to a runtime.
        /// </summary>
        public const string WorkReassigned = "work.reassigned";

        /// <summary>
        /// Indicates that work was released from a runtime.
        /// </summary>
        public const string WorkReleased = "work.released";
    }
}
