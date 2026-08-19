using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.DI
{
    /// <summary>
    /// Registers the production services required by deterministic child DAG composition.
    /// </summary>
    public static class AiChildDagCompositionServiceCollectionExtensions
    {
        /// <summary>
        /// Registers deterministic child DAG composition on top of the existing execution, policy, payload,
        /// persistence, shared queue, and recovery infrastructure.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection.</returns>
        /// <remarks>
        /// <para>
        /// This registration is intentionally opt-in at the host configuration boundary. Existing hosts keep their
        /// historical no-op run lifecycle behavior unless child DAG composition is explicitly enabled.
        /// </para>
        /// <para>
        /// The registration assumes the host already configured the normal runtime dependencies, including MongoDB
        /// execution snapshot persistence and the shared/global queue used by the existing runtime controller.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddAiChildDagComposition(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddOptions<AiChildExecutionRelationMongoOptions>();
            services.AddOptions<AiChildContinuationReconciliationOptions>();

            services.TryAddSingleton<IAiChildExecutionRelationStore, MongoAiChildExecutionRelationStore>();

            services.TryAddScoped<AiChildDagSnapshotService>();
            services.TryAddScoped<AiChildDelegationPolicyCoordinator>();
            services.TryAddScoped<AiChildExecutionAllocator>();
            services.TryAddScoped<AiChildExecutionDispatcher>();
            services.TryAddScoped<AiChildExecutionWaitingCoordinator>();
            services.TryAddScoped<AiChildInvocationGenerationCoordinator>();
            services.TryAddScoped<AiChildContinuationScheduler>();
            services.TryAddScoped<AiChildContinuationCoordinator>();
            services.TryAddScoped<AiChildExecutionCompletionCoordinator>();
            services.TryAddScoped<AiChildContinuationReconciler>();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiRuntimePipelineRunLifecycleHook, AiChildExecutionCompletionLifecycleHook>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AiChildContinuationReconcilerHostedService>());

            return services;
        }
    }
}
