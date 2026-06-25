using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.AI.Runtime.Execution.Instance
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceIdentityDescriptor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation creates a stable runtime instance identifier when the object is
    /// constructed. When no identifier is provided, the generated identifier combines the
    /// machine name, process identifier, and a unique runtime-generated value.
    /// </para>
    /// <para>
    /// When a logical runtime instance identifier is provided by the control plane, host manager,
    /// process launcher, Kubernetes adapter, or attach mode, it is preserved exactly as provided.
    /// This is important because the runtime instance id is the durable correlation key used by
    /// registry, capacity, shared dispatch, runtime execution indexes, recovery, tracing, and
    /// observability.
    /// </para>
    /// <para>
    /// The instance should be registered as a singleton so the same identity is reused for
    /// the lifetime of the runtime host.
    /// </para>
    /// <para>
    /// The generated fallback identity is intended for local in-process diagnostics only. It is
    /// not intended to replace a control-plane assigned runtime instance id in production modes.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiRuntimeInstanceIdentity : IAiRuntimeInstanceIdentityDescriptor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceIdentity"/> class.
        /// </summary>
        public DefaultAiRuntimeInstanceIdentity()
            : this(runtimeInstanceId: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceIdentity"/> class
        /// using a fixed or logical runtime instance identifier.
        /// </summary>
        /// <param name="runtimeInstanceId">
        /// The runtime instance identifier to expose.
        /// If null or empty, a generated fallback identifier is used.
        /// If provided, the identifier is preserved exactly as supplied.
        /// </param>
        public DefaultAiRuntimeInstanceIdentity(
            string? runtimeInstanceId)
        {
            HostName = Environment.MachineName;
            ProcessId = Environment.ProcessId;
            StartedAtUtc = DateTimeOffset.UtcNow;

            RuntimeInstanceId = NormalizeRuntimeInstanceId(
                runtimeInstanceId,
                HostName,
                ProcessId);
        }

        /// <inheritdoc />
        public string RuntimeInstanceId { get; }

        /// <inheritdoc />
        public string HostName { get; }

        /// <inheritdoc />
        public int ProcessId { get; }

        /// <inheritdoc />
        public DateTimeOffset StartedAtUtc { get; }

        /// <summary>
        /// Normalizes a runtime instance identifier.
        /// </summary>
        /// <param name="runtimeInstanceId">The configured runtime instance identifier.</param>
        /// <param name="hostName">The local host name used only for fallback identity generation.</param>
        /// <param name="processId">The local process id used only for fallback identity generation.</param>
        /// <returns>The effective runtime instance identifier.</returns>
        private static string NormalizeRuntimeInstanceId(
            string? runtimeInstanceId,
            string hostName,
            int processId)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return $"{hostName}:{processId}:{Guid.NewGuid():N}";
            }

            return runtimeInstanceId.Trim();
        }
    }
}