using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Periodically runs runtime execution recovery reconciliation for unavailable runtime instances.
    /// </summary>
    /// <remarks>
    /// This hosted service is the automatic control-plane loop that invokes
    /// <see cref="IAiRuntimeExecutionRecoveryReconciler"/>. The reconciler owns the recovery
    /// decision and transition boundaries; this service only schedules reconciliation iterations.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryReconcilerHostedService : BackgroundService
    {
        private static readonly TimeSpan DefaultInterval =
            TimeSpan.FromSeconds(1);

        private readonly IAiRuntimeExecutionRecoveryReconciler reconciler;
        private readonly IOptionsMonitor<AiRuntimeExecutionRecoveryReconciliationOptions> options;
        private readonly ILogger<AiRuntimeExecutionRecoveryReconcilerHostedService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconcilerHostedService"/> class.
        /// </summary>
        /// <param name="reconciler">The runtime execution recovery reconciler.</param>
        /// <param name="options">The runtime execution recovery reconciliation options.</param>
        /// <param name="logger">The logger.</param>
        public AiRuntimeExecutionRecoveryReconcilerHostedService(
            IAiRuntimeExecutionRecoveryReconciler reconciler,
            IOptionsMonitor<AiRuntimeExecutionRecoveryReconciliationOptions> options,
            ILogger<AiRuntimeExecutionRecoveryReconcilerHostedService> logger)
        {
            this.reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var currentOptions =
                    this.options.CurrentValue;

                var interval =
                    ResolveInterval(currentOptions);

                try
                {
                    if (currentOptions.Enabled)
                    {
                        var result =
                            await this.reconciler
                                .ReconcileAsync(stoppingToken)
                                .ConfigureAwait(false);

                        this.logger.LogInformation(
                            "Runtime execution recovery reconciliation iteration completed. Enabled={Enabled}, ScannedRuntimeInstances={ScannedRuntimeInstanceCount}, IgnoredRuntimeInstances={IgnoredRuntimeInstanceCount}, DiscoveredUnfinishedRuns={DiscoveredUnfinishedRunCount}, RecoveredRuns={RecoveredRunCount}, Decisions={DecisionCount}.",
                            currentOptions.Enabled,
                            result.ScannedRuntimeInstanceCount,
                            result.IgnoredRuntimeInstanceCount,
                            result.DiscoveredUnfinishedRunCount,
                            result.RecoveredRunCount,
                            result.Decisions.Count);
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
                        "Runtime execution recovery reconciliation hosted loop failed.");
                }

                await Task
                    .Delay(interval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the reconciliation interval.
        /// </summary>
        /// <param name="options">The recovery reconciliation options.</param>
        /// <returns>The resolved interval.</returns>
        private static TimeSpan ResolveInterval(
            AiRuntimeExecutionRecoveryReconciliationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var intervalProperty =
                options
                    .GetType()
                    .GetProperty("Interval");

            if (intervalProperty?.GetValue(options) is TimeSpan interval &&
                interval > TimeSpan.Zero)
            {
                return interval;
            }

            var intervalSecondsProperty =
                options
                    .GetType()
                    .GetProperty("IntervalSeconds");

            if (intervalSecondsProperty?.GetValue(options) is int intervalSeconds &&
                intervalSeconds > 0)
            {
                return TimeSpan.FromSeconds(intervalSeconds);
            }

            return DefaultInterval;
        }
    }
}