using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Test implementation of <see cref="IAiRuntimeInstanceIdentityDescriptor"/> with a deterministic runtime instance identifier.
    /// </summary>
    public sealed class TestAiRuntimeInstanceIdentity : IAiRuntimeInstanceIdentityDescriptor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TestAiRuntimeInstanceIdentity"/> class.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier to expose.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="runtimeInstanceId"/> is null, empty, or whitespace.
        /// </exception>
        public TestAiRuntimeInstanceIdentity(string runtimeInstanceId)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
                throw new ArgumentException("Runtime instance id cannot be null or empty.", nameof(runtimeInstanceId));

            RuntimeInstanceId = runtimeInstanceId;
            HostName = "test-host";
            ProcessId = 0;
            StartedAtUtc = DateTimeOffset.UtcNow;
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