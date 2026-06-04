namespace Multiplexed.Abstractions.AI.Runtime.Execution.Instance
{
    /// <summary>
    /// Provides the stable identity of a running AI runtime instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A runtime instance represents a single running host, process, or worker participating
    /// in AI execution orchestration.
    /// </para>
    /// <para>
    /// The identity exposed by this contract is expected to remain stable for the lifetime
    /// of the runtime process. Distributed execution components can use it for diagnostics,
    /// claim ownership, lease ownership, tracing, metrics, and recovery analysis.
    /// </para>
    /// <para>
    /// This identity does not represent a single step claim. Step claims should still use
    /// their own unique claim tokens. The runtime instance identity identifies the owner
    /// process, while a claim token identifies a specific ownership attempt.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceIdentityDescriptor
    {
        /// <summary>
        /// Gets the stable identifier of the current runtime instance.
        /// </summary>
        /// <remarks>
        /// This value should be created once per runtime process and reused by all runtime
        /// components that need to identify the current runtime instance.
        /// </remarks>
        string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the host name on which the runtime instance is running.
        /// </summary>
        string HostName { get; }

        /// <summary>
        /// Gets the process identifier of the runtime process.
        /// </summary>
        int ProcessId { get; }

        /// <summary>
        /// Gets the UTC timestamp at which the runtime instance identity was created.
        /// </summary>
        DateTimeOffset StartedAtUtc { get; }
    }
}