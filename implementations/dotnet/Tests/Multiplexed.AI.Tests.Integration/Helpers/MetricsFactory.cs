using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Metrics;
using Multiplexed.AI.Runtime.Metrics.Retention;
using Multiplexed.AI.Runtime.Observability;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Runtime.Observability.Metrics;
using Multiplexed.AI.Runtime.Observability.Metrics.Execution;
using Multiplexed.AI.Runtime.Observability.Metrics.HotState;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Metrics.Resolvers;
using Multiplexed.AI.Runtime.Observability.Metrics.Retention;
using Multiplexed.AI.Runtime.Observability.Metrics.Storage;
using Multiplexed.AI.Runtime.Observability.Metrics.Workers;
using Multiplexed.AI.Runtime.Observability.Tracing;

namespace Multiplexed.AI.Tests.Integration.Helpers
{
    /// <summary>
    /// Provides factory helpers for creating AI runtime metrics and observability objects in tests.
    /// </summary>
    public static class MetricsFactory
    {
        /// <summary>
        /// Creates a default <see cref="IAiRuntimeMetrics"/> instance for tests.
        /// </summary>
        /// <returns>A fully initialized runtime metrics instance.</returns>
        public static IAiRuntimeMetrics Create()
        {
            IAiRuntimeMetricWriter metricWriter =
                NoOpAiRuntimeMetricWriter.Instance;

            return new AiRuntimeMetrics(
                new AiExecutionMetrics(
                    metricWriter),
                new AiRetentionMetrics(
                    new AiRetentionTriggerMetrics(
                        metricWriter),
                    new AiRetentionDecisionMetrics(
                        metricWriter),
                    new AiRetentionPlanMetrics(
                        metricWriter),
                    new AiRetentionExecutionMetrics(
                        metricWriter)),
                new AiStorageMetrics(
                    metricWriter),
                new AiHotStateMetrics(
                    metricWriter),
                new AiResolverMetrics(
                    metricWriter),
                new AiPolicyMetrics(
                    metricWriter),
                new AiRuntimeInstanceWorkerMetrics(
                    metricWriter));
        }

        /// <summary>
        /// Factory helper for creating a fully configured <see cref="IAiRuntimeObservability"/> instance.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Provides a simple way to construct observability without DI.
        /// - Used in tests, local runtime setups, and lightweight execution contexts.
        ///
        /// DESIGN:
        /// - Composes Metrics, Tracer, Logger, Ledger recorder, and correlation accessor
        ///   into a single observability instance.
        /// - Uses in-memory metrics, no-op tracing, and no-op decision ledger recording by default.
        ///
        /// IMPORTANT:
        /// - This factory is not intended for production DI usage.
        /// - Production environments should rely on IServiceCollection configuration.
        /// </remarks>
        public static class ObservabilityFactory
        {
            /// <summary>
            /// Creates a default <see cref="IAiRuntimeObservability"/> instance.
            /// </summary>
            /// <returns>A fully initialized observability instance.</returns>
            public static IAiRuntimeObservability Create()
            {
                var metrics = MetricsFactory.Create();

                IAiRuntimeTracer tracer =
                    new NoOpAiRuntimeTracer();

                IAiRuntimeLogger logger =
                    new NoopLogger();

                IAiDecisionLedgerRecorder ledger =
                    new NoOpAiDecisionLedgerRecorder();

                IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =
                    new DefaultAiRuntimeInstanceIdentity();

                IAiRuntimeCorrelationAccessor correlation =
                    new AsyncLocalAiRuntimeCorrelationAccessor(
                        runtimeInstanceIdentity);

                return new AiRuntimeObservability(
                    metrics,
                    tracer,
                    logger,
                    ledger,
                    correlation);
            }
        }
    }
}