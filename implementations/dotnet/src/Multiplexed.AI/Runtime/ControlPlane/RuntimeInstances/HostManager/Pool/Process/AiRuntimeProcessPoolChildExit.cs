namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents the typed completion result of one runtime pool child process.
    /// </summary>
    public sealed record AiRuntimeProcessPoolChildExit
    {
        /// <summary>
        /// Gets the child completion kind.
        /// </summary>
        public AiRuntimeProcessPoolChildExitKind Kind { get; init; }

        /// <summary>
        /// Gets the optional operating-system process exit code.
        /// </summary>
        public int? ExitCode { get; init; }

        /// <summary>
        /// Gets the optional lifecycle failure message.
        /// </summary>
        /// <remarks>
        /// This value is diagnostic only. Replacement correctness does not parse or branch on the
        /// message content.
        /// </remarks>
        public string? FailureMessage { get; init; }
    }
}
