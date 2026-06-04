using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceWorkerIdentity"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This identity links a logical worker identifier to the runtime instance that owns
    /// the worker.
    /// </para>
    ///
    /// <para>
    /// The runtime instance identity remains responsible for host, process, pod, and
    /// runtime participant information. The worker identifier is used to distinguish
    /// multiple logical workers running under the same runtime instance.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorkerIdentity : IAiRuntimeInstanceWorkerIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceWorkerIdentity"/> class.
        /// </summary>
        /// <param name="runtimeInstanceIdentity">The owning runtime instance identity.</param>
        /// <param name="workerId">The logical worker identifier.</param>
        public AiRuntimeInstanceWorkerIdentity(
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity,
            string workerId)
        {
            RuntimeInstanceIdentity = runtimeInstanceIdentity
                ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));

            WorkerId = string.IsNullOrWhiteSpace(workerId)
                ? throw new ArgumentException("Worker id cannot be null or whitespace.", nameof(workerId))
                : workerId;
        }

        /// <inheritdoc />
        public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <inheritdoc />
        public string WorkerId { get; }
    }
}