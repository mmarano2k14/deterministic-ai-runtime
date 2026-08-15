using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Periodically runs durable child-completion and parent-continuation reconciliation.
    /// </summary>
    /// <remarks>
    /// This hosted service is singleton-scoped by the host. Each iteration creates a normal DI scope before resolving
    /// <see cref="AiChildContinuationReconciler"/> so scoped DAG engine services are never captured by the hosted
    /// service lifetime. Correctness decisions remain owned by the reconciler and durable relation/execution state.
    /// </remarks>
    public sealed class AiChildContinuationReconcilerHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory scopeFactory;
        private readonly IOptionsMonitor<AiChildContinuationReconciliationOptions> options;
        private readonly ILogger<AiChildContinuationReconcilerHostedService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationReconcilerHostedService"/> class.
        /// </summary>
        /// <param name="scopeFactory">The scope factory used to resolve one reconciler per iteration.</param>
        /// <param name="options">The reconciliation options.</param>
        /// <param name="logger">The logger.</param>
        public AiChildContinuationReconcilerHostedService(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<AiChildContinuationReconciliationOptions> options,
            ILogger<AiChildContinuationReconcilerHostedService> logger)
        {
            this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var current = this.options.CurrentValue;
                var interval = current.Interval > TimeSpan.Zero
                    ? current.Interval
                    : TimeSpan.FromSeconds(1);
                var batchSize = Math.Max(1, current.BatchSize);

                try
                {
                    if (current.Enabled)
                    {
                        using var scope = this.scopeFactory.CreateScope();
                        var reconciler = scope.ServiceProvider.GetRequiredService<AiChildContinuationReconciler>();
                        var result = await reconciler
                            .ReconcileAsync(batchSize, stoppingToken)
                            .ConfigureAwait(false);

                        this.logger.LogDebug(
                            "Child continuation reconciliation completed. Incomplete={IncompleteCount}, Completed={CompletedCount}, Continuations={ContinuationCount}, ParkCandidates={ParkCandidateCount}, ParkRepairs={ParkRepairCount}.",
                            result.IncompleteRelationCount,
                            result.CompletedRelationCount,
                            result.ContinuationCandidateCount,
                            result.ParkConsistencyCandidateCount,
                            result.ParkRepairEnqueueCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    this.logger.LogWarning(
                        exception,
                        "Child continuation reconciliation hosted loop failed.");
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
