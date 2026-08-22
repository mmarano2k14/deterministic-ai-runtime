using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

namespace Multiplexed.AI.Runtime.AI.Policies
{
    /// <summary>
    /// Preserves direct-construction compatibility while routing canonical policy facts through
    /// the existing Event Manager and its centralized Ledger / Metrics projections.
    /// </summary>
    internal static class AiPolicyObservabilityCompatibility
    {
        /// <summary>
        /// Resolves the Event Manager from the step service provider and adds any missing policy
        /// projection owners required by direct-construction tests or compatibility callers.
        /// </summary>
        /// <param name="services">The step-scoped service provider.</param>
        /// <param name="observability">The existing runtime observability facade.</param>
        /// <returns>The observer used for canonical policy event emission.</returns>
        public static IAiControlPlaneObserver Compose(
            IServiceProvider services,
            IAiRuntimeObservability observability)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(observability);

            var observer = services.GetService(typeof(IAiControlPlaneObserver)) as IAiControlPlaneObserver;
            var ledger = observability.Ledger ?? new NoOpAiDecisionLedgerRecorder();
            var ledgerProjection = RuntimeObservabilityAiControlPlaneEventSink.CreateForLedger(ledger);
            var metricsProjection = new PolicyMetricsAiControlPlaneEventSink(observability.Metrics.Policy);

            if (observer is CompositeAiControlPlaneObserver composite)
            {
                return composite
                    .WithProjectionSink(ledgerProjection)
                    .WithProjectionSink(metricsProjection);
            }

            var projectionObserver = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    ledgerProjection,
                    metricsProjection
                });

            if (observer is null || observer is NoopAiControlPlaneObserver)
            {
                return projectionObserver;
            }

            return new CompatibilityObserver(observer, projectionObserver);
        }

        private sealed class CompatibilityObserver : IAiControlPlaneObserver
        {
            private readonly IAiControlPlaneObserver observer;
            private readonly IAiControlPlaneObserver projectionObserver;

            public CompatibilityObserver(
                IAiControlPlaneObserver observer,
                IAiControlPlaneObserver projectionObserver)
            {
                this.observer = observer;
                this.projectionObserver = projectionObserver;
            }

            public async Task RecordAsync(
                Multiplexed.Abstractions.AI.ControlPlane.Observability.Events.AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                await this.projectionObserver
                    .RecordAsync(controlPlaneEvent, cancellationToken)
                    .ConfigureAwait(false);

                await this.observer
                    .RecordAsync(controlPlaneEvent, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
