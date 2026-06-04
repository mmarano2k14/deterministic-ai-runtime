using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceWorkerFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This factory creates independent runtime instance workers for distributed
    /// execution scenarios where multiple runtime workers cooperate on the same
    /// execution identifier.
    /// </para>
    ///
    /// <para>
    /// Each created worker receives its own logical worker identity while preserving
    /// the owning runtime instance identity. This allows metrics, tracing, leases,
    /// diagnostics, decision ledger entries, and correlation contexts to distinguish
    /// workers without confusing them with the host runtime instance.
    /// </para>
    ///
    /// <para>
    /// The factory does not create separate execution identifiers. All created
    /// workers are expected to operate on the same execution namespace while relying
    /// on distributed DAG claims, leases, and convergence semantics for correctness.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorkerFactory : IAiRuntimeInstanceWorkerFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IAiRuntimeInstanceIdentityDescriptor _runtimeInstanceIdentity;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceWorkerFactory"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider used to create runtime instance workers.
        /// </param>
        /// <param name="runtimeInstanceIdentity">
        /// The owning runtime instance identity used to create logical worker identities.
        /// </param>
        public AiRuntimeInstanceWorkerFactory(
            IServiceProvider serviceProvider,
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity)
        {
            _serviceProvider =
                serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));

            _runtimeInstanceIdentity =
                runtimeInstanceIdentity
                ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));

            ArgumentException.ThrowIfNullOrWhiteSpace(
                _runtimeInstanceIdentity.RuntimeInstanceId);
        }

        /// <summary>
        /// Creates runtime instance workers for distributed execution participation.
        /// </summary>
        /// <param name="workerCount">
        /// The number of runtime instance workers to create.
        /// </param>
        /// <returns>
        /// The created runtime instance workers.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Each created worker receives a unique logical worker identity.
        /// </para>
        ///
        /// <para>
        /// The workers are intended to cooperate on the same execution identifier
        /// while relying on distributed Redis-backed claims and convergence semantics
        /// to avoid duplicate execution ownership.
        /// </para>
        /// </remarks>
        public IReadOnlyCollection<IAiRuntimeInstanceWorker> CreateWorkers(
            int workerCount)
        {
            var count = Math.Max(1, workerCount);

            var workers = new List<IAiRuntimeInstanceWorker>(
                count);

            for (var index = 0; index < count; index++)
            {
                var workerIndex = index + 1;

                IAiRuntimeInstanceWorkerIdentity workerIdentity =
                    new DefaultAiRuntimeInstanceWorkerIdentity(
                        _runtimeInstanceIdentity,
                        $"{_runtimeInstanceIdentity.RuntimeInstanceId}:worker:{workerIndex}");

                workers.Add(
                    ActivatorUtilities.CreateInstance<AiRuntimeInstanceWorker>(
                        _serviceProvider,
                        workerIdentity));
            }

            return workers;
        }
    }
}