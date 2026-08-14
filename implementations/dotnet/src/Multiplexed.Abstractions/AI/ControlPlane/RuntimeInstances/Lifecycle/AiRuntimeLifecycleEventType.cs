namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Defines runtime infrastructure lifecycle event types.
    /// </summary>
    public static class AiRuntimeLifecycleEventType
    {
        public const string HostCreationRequested = "host.creation.requested";
        public const string HostCreationStarted = "host.creation.started";
        public const string HostCreationSucceeded = "host.creation.succeeded";
        public const string HostCreationFailed = "host.creation.failed";

        public const string RuntimeRegistered = "runtime.registered";
        public const string RuntimeReady = "runtime.ready";
        public const string RuntimeDraining = "runtime.draining";
        public const string RuntimeSuppressed = "runtime.suppressed";
        public const string RuntimeUnhealthy = "runtime.unhealthy";
        public const string RuntimeStopped = "runtime.stopped";

        public const string HostDeletionRequested = "host.deletion.requested";
        public const string HostDeleted = "host.deleted";
        public const string HostDisappeared = "host.disappeared";

        public const string RuntimeReplacementRequested = "runtime.replacement.requested";
        public const string RuntimeReplacementRegistered = "runtime.replacement.registered";

        public const string WorkAssigned = "work.assigned";
        public const string WorkReassigned = "work.reassigned";
        public const string WorkReleased = "work.released";
    }
}
