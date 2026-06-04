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
    /// When a logical runtime instance identifier is provided, such as <c>mcp-runtime-1</c>,
    /// the identifier is normalized with the current machine name, for example
    /// <c>MSI:mcp-runtime-1</c>.
    /// </para>
    /// <para>
    /// The instance should be registered as a singleton so the same identity is reused for
    /// the lifetime of the runtime host.
    /// </para>
    /// <para>
    /// The generated identity is intended for distributed runtime ownership, observability,
    /// ledger correlation, tracing, metrics, and diagnostics. It is not intended to be a
    /// security token or a durable identity across process restarts.
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
        /// If a logical identifier is provided, it is prefixed with the machine name.
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
        /// Normalizes a runtime instance identifier for observability and diagnostics.
        /// </summary>
        private static string NormalizeRuntimeInstanceId(
            string? runtimeInstanceId,
            string hostName,
            int processId)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return $"{hostName}:{processId}:{Guid.NewGuid():N}";
            }

            var trimmedRuntimeInstanceId =
                runtimeInstanceId.Trim();

            if (trimmedRuntimeInstanceId.StartsWith(
                    $"{hostName}:",
                    StringComparison.Ordinal))
            {
                return trimmedRuntimeInstanceId;
            }

            return $"{hostName}:{trimmedRuntimeInstanceId}";
        }
    }
}