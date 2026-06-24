using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Hosted service that periodically runs runtime instance health reconciliation.
    /// </summary>
    /// <remarks>
    /// This hosted service only invokes <see cref="IAiRuntimeInstanceHealthReconciler"/>.
    /// It does not perform execution recovery, run requeue, host restart, process kill,
    /// or dead-letter queue transitions.
    /// </remarks>
    public sealed class AiRuntimeInstanceHealthReconcilerHostedService : BackgroundService
    {
        private readonly IAiRuntimeInstanceHealthReconciler reconciler;
        private readonly AiRuntimeInstanceHealthReconcilerHostedServiceOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceHealthReconcilerHostedService"/> class.
        /// </summary>
        /// <param name="reconciler">The runtime instance health reconciler.</param>
        /// <param name="options">The hosted service options.</param>
        public AiRuntimeInstanceHealthReconcilerHostedService(
            IAiRuntimeInstanceHealthReconciler reconciler,
            IOptions<AiRuntimeInstanceHealthReconcilerHostedServiceOptions> options)
        {
            ArgumentNullException.ThrowIfNull(reconciler);
            ArgumentNullException.ThrowIfNull(options);

            this.reconciler = reconciler;
            this.options = options.Value;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await reconciler
                        .ReconcileAsync(stoppingToken)
                        .ConfigureAwait(false);

                    await Task
                        .Delay(options.Interval, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    await Task
                        .Delay(options.ErrorDelay, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}