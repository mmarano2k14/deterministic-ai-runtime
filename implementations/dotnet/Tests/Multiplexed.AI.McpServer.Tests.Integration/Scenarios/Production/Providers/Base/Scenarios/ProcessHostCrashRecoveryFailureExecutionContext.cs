using System;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.Stores;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios
{
    /// <summary>
    /// Carries the complete existing crash-recovery helper authority for one impacted tenant flow.
    /// </summary>
    public sealed class ProcessHostCrashRecoveryFailureExecutionContext
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="ProcessHostCrashRecoveryFailureExecutionContext"/> class.
        /// </summary>
        /// <param name="output">The scenario output helper.</param>
        /// <param name="services">The running MCP host service provider.</param>
        /// <param name="processControl">The runtime process-control authority.</param>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunStore">The durable shared-run store.</param>
        /// <param name="sharedQueue">The durable shared queue.</param>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="inventory">The exact assigned-work inventory selected for failure.</param>
        /// <param name="minimumCompletedStepsBeforeKill">The minimum durable progress required before failure.</param>
        /// <param name="progressTimeout">The maximum crash-window observation duration.</param>
        /// <param name="unsafeTimeout">The maximum unsafe-runtime detection duration.</param>
        /// <param name="requeueTimeout">The maximum recovery requeue duration.</param>
        /// <param name="redispatchTimeout">The maximum replacement redispatch duration.</param>
        /// <param name="executionResolveTimeout">The maximum durable execution resolution duration.</param>
        /// <param name="observationMode">The production recovery observation mode.</param>
        /// <param name="signalSubscriber">The runtime signal subscriber used in hybrid mode.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="hybridFallbackPollInterval">The durable hybrid fallback polling interval.</param>
        /// <param name="crashCheckpointGate">The optional durable crash checkpoint gate.</param>
        /// <param name="runtimePoolFailurePhase">
        /// The Runtime Pool failure phase assigned to the tenant, or <c>null</c> for historical process-host scenarios.
        /// </param>
        public ProcessHostCrashRecoveryFailureExecutionContext(
            ITestOutputHelper output,
            IServiceProvider services,
            IAiRuntimeHostProcessControl processControl,
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiDagExecutionStore dagStore,
            RealRuntimeCrashAssignedWorkInventoryProof inventory,
            int minimumCompletedStepsBeforeKill,
            TimeSpan progressTimeout,
            TimeSpan unsafeTimeout,
            TimeSpan requeueTimeout,
            TimeSpan redispatchTimeout,
            TimeSpan executionResolveTimeout,
            ProductionRecoveryObservationMode observationMode,
            IAiRuntimeSignalSubscriber? signalSubscriber,
            string controlPlaneId,
            TimeSpan hybridFallbackPollInterval,
            ProductionCrashCheckpointGate? crashCheckpointGate,
            RuntimePoolCrashFailurePhase? runtimePoolFailurePhase)
        {
            Output = output ?? throw new ArgumentNullException(nameof(output));
            Services = services ?? throw new ArgumentNullException(nameof(services));
            ProcessControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            RunExecutionIndex = runExecutionIndex ?? throw new ArgumentNullException(nameof(runExecutionIndex));
            SharedRunStore = sharedRunStore ?? throw new ArgumentNullException(nameof(sharedRunStore));
            SharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
            DagStore = dagStore ?? throw new ArgumentNullException(nameof(dagStore));
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                minimumCompletedStepsBeforeKill);

            ValidatePositiveTimeout(
                progressTimeout,
                nameof(progressTimeout));

            ValidatePositiveTimeout(
                unsafeTimeout,
                nameof(unsafeTimeout));

            ValidatePositiveTimeout(
                requeueTimeout,
                nameof(requeueTimeout));

            ValidatePositiveTimeout(
                redispatchTimeout,
                nameof(redispatchTimeout));

            ValidatePositiveTimeout(
                executionResolveTimeout,
                nameof(executionResolveTimeout));

            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            ValidatePositiveTimeout(
                hybridFallbackPollInterval,
                nameof(hybridFallbackPollInterval));

            MinimumCompletedStepsBeforeKill =
                minimumCompletedStepsBeforeKill;

            ProgressTimeout = progressTimeout;
            UnsafeTimeout = unsafeTimeout;
            RequeueTimeout = requeueTimeout;
            RedispatchTimeout = redispatchTimeout;
            ExecutionResolveTimeout = executionResolveTimeout;
            ObservationMode = observationMode;
            SignalSubscriber = signalSubscriber;
            ControlPlaneId = controlPlaneId;
            HybridFallbackPollInterval = hybridFallbackPollInterval;
            CrashCheckpointGate = crashCheckpointGate;
            RuntimePoolFailurePhase = runtimePoolFailurePhase;
        }

        /// <summary>
        /// Gets the scenario output helper.
        /// </summary>
        public ITestOutputHelper Output { get; }

        /// <summary>
        /// Gets the running MCP host service provider.
        /// </summary>
        public IServiceProvider Services { get; }

        /// <summary>
        /// Gets the runtime process-control authority.
        /// </summary>
        public IAiRuntimeHostProcessControl ProcessControl { get; }

        /// <summary>
        /// Gets the runtime instance registry.
        /// </summary>
        public IAiRuntimeInstanceRegistry Registry { get; }

        /// <summary>
        /// Gets the runtime run execution index.
        /// </summary>
        public IAiRuntimeRunExecutionIndex RunExecutionIndex { get; }

        /// <summary>
        /// Gets the durable shared-run store.
        /// </summary>
        public IAiSharedRunStore SharedRunStore { get; }

        /// <summary>
        /// Gets the durable shared queue.
        /// </summary>
        public IAiSharedQueue SharedQueue { get; }

        /// <summary>
        /// Gets the durable DAG execution store.
        /// </summary>
        public IAiDagExecutionStore DagStore { get; }

        /// <summary>
        /// Gets the exact assigned-work inventory selected for failure.
        /// </summary>
        public RealRuntimeCrashAssignedWorkInventoryProof Inventory { get; }

        /// <summary>
        /// Gets the minimum durable progress required before failure.
        /// </summary>
        public int MinimumCompletedStepsBeforeKill { get; }

        /// <summary>
        /// Gets the maximum crash-window observation duration.
        /// </summary>
        public TimeSpan ProgressTimeout { get; }

        /// <summary>
        /// Gets the maximum unsafe-runtime detection duration.
        /// </summary>
        public TimeSpan UnsafeTimeout { get; }

        /// <summary>
        /// Gets the maximum recovery requeue duration.
        /// </summary>
        public TimeSpan RequeueTimeout { get; }

        /// <summary>
        /// Gets the maximum replacement redispatch duration.
        /// </summary>
        public TimeSpan RedispatchTimeout { get; }

        /// <summary>
        /// Gets the maximum durable execution resolution duration.
        /// </summary>
        public TimeSpan ExecutionResolveTimeout { get; }

        /// <summary>
        /// Gets the production recovery observation mode.
        /// </summary>
        public ProductionRecoveryObservationMode ObservationMode { get; }

        /// <summary>
        /// Gets the runtime signal subscriber used in hybrid mode.
        /// </summary>
        public IAiRuntimeSignalSubscriber? SignalSubscriber { get; }

        /// <summary>
        /// Gets the logical control-plane identifier.
        /// </summary>
        public string ControlPlaneId { get; }

        /// <summary>
        /// Gets the durable hybrid fallback polling interval.
        /// </summary>
        public TimeSpan HybridFallbackPollInterval { get; }

        /// <summary>
        /// Gets the optional durable crash checkpoint gate.
        /// </summary>
        public ProductionCrashCheckpointGate? CrashCheckpointGate { get; }

        /// <summary>
        /// Gets the Runtime Pool failure phase assigned to the tenant.
        /// </summary>
        public RuntimePoolCrashFailurePhase? RuntimePoolFailurePhase { get; }

        private static void ValidatePositiveTimeout(
            TimeSpan timeout,
            string parameterName)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    timeout,
                    "The timeout must be greater than zero.");
            }
        }
    }
}
