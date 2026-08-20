namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Defines transport-neutral failure reasons returned by runtime instance command handlers.
    /// </summary>
    public static class AiRuntimeInstanceCommandFailureReasons
    {
        /// <summary>The requested runtime command operation is not supported.</summary>
        public const string UnsupportedCommandOperation = "unsupported-command-operation";

        /// <summary>The runtime-local queue required by the command was not found.</summary>
        public const string RuntimeQueueNotFound = "runtime-queue-not-found";
        /// <summary>The runtime queue request payload required by the command is missing.</summary>
        public const string QueueRequestMissing = "queue-request-missing";

    }
}
