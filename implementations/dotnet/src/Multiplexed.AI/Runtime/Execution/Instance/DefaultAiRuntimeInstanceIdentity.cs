using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.AI.Runtime.Execution.Instance
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceIdentity"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation creates a stable runtime instance identifier when the object is
    /// constructed. The generated identifier combines the machine name, process identifier,
    /// and a unique runtime-generated value.
    /// </para>
    /// <para>
    /// The instance should be registered as a singleton so the same identity is reused for
    /// the lifetime of the runtime host.
    /// </para>
    /// <para>
    /// The generated identity is intended for distributed runtime ownership and diagnostics.
    /// It is not intended to be a security token or a durable identity across process restarts.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiRuntimeInstanceIdentity : IAiRuntimeInstanceIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceIdentity"/> class.
        /// </summary>
        public DefaultAiRuntimeInstanceIdentity()
            : this($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceIdentity"/> class
        /// using a fixed runtime instance identifier.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier to expose.</param>
        public DefaultAiRuntimeInstanceIdentity(string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            HostName = Environment.MachineName;
            ProcessId = Environment.ProcessId;
            StartedAtUtc = DateTimeOffset.UtcNow;

            RuntimeInstanceId = runtimeInstanceId;
        }

        /// <inheritdoc />
        public string RuntimeInstanceId { get; }

        /// <inheritdoc />
        public string HostName { get; }

        /// <inheritdoc />
        public int ProcessId { get; }

        /// <inheritdoc />
        public DateTimeOffset StartedAtUtc { get; }
    }
}