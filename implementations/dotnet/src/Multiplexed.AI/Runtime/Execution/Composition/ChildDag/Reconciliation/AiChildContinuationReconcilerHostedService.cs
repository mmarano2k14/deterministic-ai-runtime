using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Periodically runs durable child-completion and parent-continuation reconciliation.
    /// </summary>
    /// <remarks>
    /// This service schedules iterations only. All correctness decisions are owned by
    /// <see cref="AiChildContinuationReconciler"/> and durable relation/execution state.
    /// </remarks>
    public sealed class AiChildContinuationReconcilerHostedService : BackgroundService
    {
        private readonly AiChildContinuationReconciler reconciler;
        private readonly IOptionsMonitor<AiChildContinuationReconciliationOptions> options;
        private readonly ILogger<AiChildContinuationReconcilerHostedService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationReconcilerHostedService"/> class.
        /// </summary>
        /// <param name="reconciler">The durable child continuation reconciler.</param>
        /// <param name="options">The reconciliation options.</param>
        /// <param name="logger">The logger.</param>
        public AiChildContinuationReconcilerHostedService(
            AiChildContinuationReconciler reconciler,
            IOptionsMonitor<AiChildContinuationReconciliationOptions> options,
            ILogger<AiChildContinuationReconcilerHostedService> logger)
        {
            this.reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
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
                        var result = await this.reconciler
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
