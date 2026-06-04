using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default logical worker identity for the runtime instance worker created by dependency injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This identity is used for the default runtime worker resolved directly from the dependency
    /// injection container.
    /// </para>
    ///
    /// <para>
    /// Factory-created distributed workers receive their own explicit worker identities through
    /// <see cref="AiRuntimeInstanceWorkerFactory"/> and do not use this default identity.
    /// </para>
    ///
    /// <para>
    /// When no explicit worker identifier is provided, this class preserves the legacy behavior
    /// by generating <c>{RuntimeInstanceId}:worker:default</c>.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiRuntimeInstanceWorkerIdentity : IAiRuntimeInstanceWorkerIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceWorkerIdentity"/> class
        /// using the default worker identifier.
        /// </summary>
        /// <param name="runtimeInstanceIdentity">The owning runtime instance identity descriptor.</param>
        public DefaultAiRuntimeInstanceWorkerIdentity(
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity)
            : this(runtimeInstanceIdentity, workerId: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiRuntimeInstanceWorkerIdentity"/> class
        /// using an explicit worker identifier.
        /// </summary>
        /// <param name="runtimeInstanceIdentity">The owning runtime instance identity descriptor.</param>
        /// <param name="workerId">The explicit worker identifier.</param>
        public DefaultAiRuntimeInstanceWorkerIdentity(
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity,
            string? workerId)
        {
            RuntimeInstanceIdentity = runtimeInstanceIdentity
                ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));

            ArgumentException.ThrowIfNullOrWhiteSpace(
                RuntimeInstanceIdentity.RuntimeInstanceId);

            WorkerId = !string.IsNullOrWhiteSpace(workerId)
                ? workerId.Trim()
                : $"{RuntimeInstanceIdentity.RuntimeInstanceId}:worker:default";
        }

        /// <inheritdoc />
        public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <inheritdoc />
        public string WorkerId { get; }
    }
}