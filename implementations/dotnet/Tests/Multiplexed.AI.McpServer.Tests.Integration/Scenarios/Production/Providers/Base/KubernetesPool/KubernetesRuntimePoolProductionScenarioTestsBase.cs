using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;
using Xunit.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Stores;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using StackExchange.Redis;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Provides the reusable Kubernetes Runtime Pool production proof harness shared by transport-specific test classes.
    /// </summary>
    public abstract class KubernetesRuntimePoolProductionScenarioTestsBase :
        ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private const int FinalScenarioKillAfterCompletedStepCount = 25;
        private const int BoundaryFailureCrashCheckpointStateTtlMinutes = 30;
        private const int BoundaryFailureAdmissionBackpressureTimeoutMinutes = 5;
        private const int ExternalBoundaryFailureWaitTimeoutMinutes = 15;

        protected readonly ITestOutputHelper output;
        protected readonly IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile;
        private readonly Func<int, int, IRuntimePoolCrashRecoveryScenarioRuntimeProfile> boundedCapacityProfileFactory;
        private readonly KubernetesRuntimePoolProductionInfrastructure infrastructure;
        protected readonly ConcurrentDictionary<string, RuntimePoolAllInOneFailureState> states =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the bounded Runtime Pool scenario profile used by this proof.
        /// </summary>
        protected IRuntimePoolCrashRecoveryScenarioRuntimeProfile RuntimePoolProfile =>
            profile;

        /// <summary>
        /// Initializes the reusable Kubernetes Runtime Pool production proof harness.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The bounded Runtime Pool scenario profile.</param>
        protected KubernetesRuntimePoolProductionScenarioTestsBase(
            ITestOutputHelper output,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
            Func<int, int, IRuntimePoolCrashRecoveryScenarioRuntimeProfile> boundedCapacityProfileFactory)
            : base(
                output,
                profile)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.boundedCapacityProfileFactory =
                boundedCapacityProfileFactory
                ?? throw new ArgumentNullException(nameof(boundedCapacityProfileFactory));
            infrastructure =
                new KubernetesRuntimePoolProductionInfrastructure(
                    this.output,
                    this.profile.LogPrefix);
        }

        /// <inheritdoc />
        protected override async Task AssertRuntimeBelongsToTenantAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(tenant);

            var snapshot =
                await GetRequiredRuntimeSnapshotAsync(
                        registry,
                        runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(
                tenant.TenantId,
                snapshot.TenantId);
        }

        /// <summary>
        /// Measures the bounded Kubernetes Runtime Pool capacity without injecting any failure.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves.</param>
        /// <returns>A task that completes when all submitted DAG executions have drained.</returns>
        protected Task ExecuteBoundedCapacityMachineLimitScenarioAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            return ExecuteBoundedCapacityScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                injectPodFailure: false);
        }

        /// <summary>
        /// Measures the same bounded Kubernetes Runtime Pool capacity while force-deleting one fully busy Pod.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves.</param>
        /// <returns>A task that completes when replacement capacity and every recovered DAG have converged.</returns>
        protected Task ExecuteBoundedCapacityPodFailureMachineLimitScenarioAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            return ExecuteBoundedCapacityScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                injectPodFailure: true);
        }

        /// <summary>
        /// Executes repeated bounded-capacity Pod-failure cycles against one warm Kubernetes Runtime Pool.
        /// The control plane, host, Pods, and surviving runtime identities remain alive between cycles;
        /// deterministic cleanup runs only after the final cycle.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential production cycles executed against the same warm pool.</param>
        /// <returns>A task that completes after every cycle proves reuse, exact Pod recovery, replay, ledger, and trace.</returns>
        protected Task ExecuteReusableBoundedCapacityPodFailureProductionScenarioAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return ExecuteReusableBoundedCapacityPodFailureProductionScenarioCoreAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: false);
        }

        /// <summary>
        /// Executes the final hierarchical KubernetesPool proof: one exact in-Pod runtime process
        /// is killed after durable DAG progress, its Pod and siblings survive, then one distinct
        /// fully busy Pod is force-deleted. The converged warm pool is reused across every cycle.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG. Zero preserves the historical workload shape.</param>
        /// <returns>A task that completes after the hierarchical runtime and Pod failure proof converges across every cycle.</returns>
        protected Task ExecuteFullFailureProductionScenarioAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth = 0)
        {
            return ExecuteReusableBoundedCapacityPodFailureProductionScenarioCoreAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: true,
                childDepth: childDepth);
        }

        /// <summary>
        /// Executes the same final hierarchical KubernetesPool proof, but leaves the distinct
        /// fully busy Pod alive until an operator force-deletes that exact Pod externally.
        /// The test waits for the selected Pod UID to disappear before running the unchanged
        /// suppression, replacement, recovery, warm-reuse, replay, ledger, and cleanup proof.
        /// Keep the manual gate watcher open in a separate PowerShell window:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-kubernetes-kill.txt" -Wait</code>
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG. Zero preserves the historical workload shape.</param>
        /// <returns>A task that completes after the external Pod failure and hierarchical recovery proof converges across every cycle.</returns>
        protected Task ExecuteFullFailureProductionScenarioAwaitExternalPodFailureAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth = 0)
        {
            var signalPath =
                ManualExternalFailureGateSignal.PrepareKubernetesWatch();

            output.WriteLine(
                $"[{profile.LogPrefix} MANUAL EXTERNAL FAILURE WATCH] TargetKind='KubernetesPod', PowerShellCommand='{ManualExternalFailureGateSignal.KubernetesPowerShellWatchCommand}', SignalFile='{signalPath}', Instruction='Keep this watcher open for every cycle.'");

            return ExecuteReusableBoundedCapacityPodFailureProductionScenarioCoreAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: true,
                waitForExternalPodDeletion: true,
                childDepth: childDepth);
        }

        private async Task ExecuteBoundedCapacityScenarioAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            bool injectPodFailure)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIterationCount);

            const int stepCount = 50;
            const int comparableP35ExecutionCount = 315;
            const int comparableP35LogicalStepCount = 15_750;

            var runsPerIteration =
                checked(maximumPodCount * runtimeCountPerPod);

            var submittedRunCount =
                checked(runsPerIteration * submissionIterationCount);

            var logicalStepCount =
                checked(submittedRunCount * stepCount);

            var maximumRuntimeCapacity =
                checked(maximumPodCount * runtimeCountPerPod);

            var maximumConcurrentMcpSubmissions =
                Math.Clamp(
                    maximumRuntimeCapacity,
                    4,
                    16);

            const int maximumAdmissionAttemptCount = 8;

            var boundedCapacityProfile =
                boundedCapacityProfileFactory(
                    maximumPodCount,
                    runtimeCountPerPod);

            var workloadExceedsMaximumCapacity =
                submittedRunCount > maximumRuntimeCapacity;

            Assert.Equal(
                maximumPodCount,
                boundedCapacityProfile.CrashRecoveryPlan.MaximumPodCount);

            Assert.Equal(
                runtimeCountPerPod,
                boundedCapacityProfile.CrashRecoveryPlan.MaximumRuntimeCountPerPod);

            var scenarioName =
                injectPodFailure
                    ? $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-bounded-capacity-pod-failure-machine-limit"
                    : $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-bounded-capacity-machine-limit";

            var controlPlaneIdPrefix =
                injectPodFailure
                    ? $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-bounded-capacity-pod-failure"
                    : $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-bounded-capacity-machine-limit";

            var baseScenario =
                ProductionRuntimeScenarioFactory
                    .CreateSingleTenantDedicatedRuntimeModeScenario();

            var templateTenant =
                baseScenario.Tenants.Single();

            var tenant =
                templateTenant with
                {
                    TenantId = "tenant-kubernetes-pool-machine-limit",
                    TenantGroupId = "tenant-kubernetes-pool-machine-limit-group",
                    RuntimeMode = ProductionTenantRuntimeMode.Shared,
                    ExpectDedicatedRuntimePrefix = false,
                    RuntimeInstanceIdPrefix = "kubernetes-pool-machine-limit-runtime",
                    MaxRuntimeInstances = maximumRuntimeCapacity,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = submittedRunCount,
                        StepCount = stepCount,
                        DelayMs = 750,
                        FlakyStepInterval = 0,
                        EnableRetention = false
                    }
                };

            var scenario =
                baseScenario with
                {
                    Name = scenarioName,
                    ControlPlaneIdPrefix = controlPlaneIdPrefix,
                    Tenants = new[] { tenant },
                    PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                    ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                    // This load proof must persist every submission in the shared queue.
                    // The queue dispatcher then requests scale-out and requeues the claimed item
                    // until bounded runtime capacity becomes visible.
                    SubmitMode = ProductionRuntimeSubmitMode.QueueFirst,
                    ScaleOutTimeout = TimeSpan.FromMinutes(5),
                    DispatchTimeout = TimeSpan.FromMinutes(15),
                    CompletionTimeout = injectPodFailure
                        ? TimeSpan.FromMinutes(60)
                        : TimeSpan.FromMinutes(45),
                    Assertions = new ProductionRuntimeScenarioAssertionOptions
                    {
                        AssertAllRunsCompleted = true,
                        AssertTenantIsolation = true,
                        AssertScaleOut = true,
                        AssertMaxRuntimeInstances = true,
                        AssertLedger = true,
                        AssertTrace = true,
                        AssertReplayReport = true,
                        AssertReplayLedger = true,
                        AssertReplayTrace = true
                    }
                };

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var poolId =
                ResolvePoolId(controlPlaneId);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver
                    .ResolveRuntimeHostAssemblyPath();

            var settings =
                boundedCapacityProfile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            settings["Tests:UseCapturingLedgerRecorder"] = "false";
            settings["Tests:UseMongoRuntimeLifecycleJournal"] = "true";
            settings["AiRuntimeRecoveryForensics:StrictPersistence"] = "true";

            var ledgerTimelineFromUtc =
                DateTimeOffset.UtcNow.AddSeconds(-5);

            await using var dataStoreTrafficObserver =
                await ProductionDataStoreTrafficObserver
                    .StartAsync(output)
                    .ConfigureAwait(false);

            await OnCrashRecoveryScenarioStartingAsync(controlPlaneId)
                .ConfigureAwait(false);

            var totalStopwatch =
                Stopwatch.StartNew();

            var phaseStopwatch =
                Stopwatch.StartNew();

            var phaseTimings =
                new List<(string Name, TimeSpan Duration)>();

            void WritePhaseHeader(
                int phaseNumber,
                string title,
                string passTarget)
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"# PHASE {phaseNumber}/6 - {title}");
                output.WriteLine(passTarget);
            }

            void CompletePhase(string phaseName)
            {
                phaseStopwatch.Stop();

                phaseTimings.Add(
                    (phaseName, phaseStopwatch.Elapsed));

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY TIMING] " +
                    $"Phase='{phaseName}', " +
                    $"Duration='{phaseStopwatch.Elapsed}', " +
                    $"TotalElapsed='{totalStopwatch.Elapsed}'.");

                phaseStopwatch.Restart();
            }

            void WriteTimingSummary()
            {
                output.WriteLine(string.Empty);
                output.WriteLine(
                    $"# {boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY TIMING SUMMARY");

                foreach (var phaseTiming in phaseTimings)
                {
                    output.WriteLine(
                        $"  - {phaseTiming.Name}: {phaseTiming.Duration}");
                }

                output.WriteLine(
                    $"  - Scenario total: {totalStopwatch.Elapsed}");
            }

            output.WriteLine(
                $"# {boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY PRODUCTION PROOF");
            output.WriteLine(
                injectPodFailure
                    ? "Executive proof: bounded Kubernetes Runtime Pool capacity survives one forced busy-Pod deletion, restores fresh capacity, and drains the shared workload without duplicate execution, lost work, or configured Pod-capacity overflow."
                    : "Executive proof: bounded Kubernetes Runtime Pool capacity drains a shared-queue workload without failure injection, duplicate dispatch, lost work, recovery contamination, or configured Pod-capacity overflow.");
            output.WriteLine(string.Empty);
            output.WriteLine("Scenario contract:");
            output.WriteLine("  - [ON] Every submission is persisted through QueueFirst shared-queue admission.");
            output.WriteLine("  - [ON] Existing Runtime Pool capacity is reused before additional Pod capacity is requested.");
            output.WriteLine("  - [ON] Active Pods and runtime membership never exceed their configured bounds.");
            output.WriteLine("  - [ON] Every shared run resolves to exactly one local run and one DAG execution.");
            output.WriteLine("  - [ON] Every DAG completes exactly 50 logical steps.");
            output.WriteLine(
                injectPodFailure
                    ? "  - [ON] One fully busy Pod is force-deleted; exact membership suppression, replacement, recovery, replay, ledger, trace, and topology are validated."
                    : "  - [ON] Replay, ledger, trace, topology, datastore traffic, and no-recovery evidence are validated.");
            output.WriteLine(string.Empty);
            output.WriteLine("Workload summary:");
            output.WriteLine($"  MaximumConfiguredPodCount='{maximumPodCount}'");
            output.WriteLine($"  RuntimeCountPerPod='{runtimeCountPerPod}'");
            output.WriteLine($"  MaximumRuntimeCapacity='{maximumRuntimeCapacity}'");
            output.WriteLine($"  SubmissionIterationCount='{submissionIterationCount}'");
            output.WriteLine($"  RunsPerIteration='{runsPerIteration}'");
            output.WriteLine($"  SubmittedRunCount='{submittedRunCount}'");
            output.WriteLine($"  LogicalStepCount='{logicalStepCount}'");
            output.WriteLine(
                $"  MaximumConcurrentMcpSubmissions='{maximumConcurrentMcpSubmissions}'");
            output.WriteLine(
                $"  MaximumAdmissionAttemptCount='{maximumAdmissionAttemptCount}'");
            output.WriteLine(string.Empty);
            output.WriteLine("Runtime profile:");
            output.WriteLine($"  Provider='{boundedCapacityProfile.ProviderLabel}'");
            output.WriteLine($"  ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"  PoolId='{poolId}'");
            output.WriteLine("  HostCreationMode='KubernetesPool'");
            output.WriteLine($"  PersistenceProfile='{scenario.PersistenceProfile}'");
            output.WriteLine($"  ObservabilityProfile='{scenario.ObservabilityProfile}'");
            output.WriteLine("  SubmitMode='QueueFirst'");

            WritePhaseHeader(
                1,
                "SETUP HOST SERVICES AND SCALE-OUT WATCHER",
                "[PASS TARGET] Start the durable Mongo/Redis control plane and prove the scale-out watcher is ready for this exact ControlPlaneId.");

            var submissionDuration =
                TimeSpan.Zero;

            var drainDuration =
                TimeSpan.Zero;

            var admissionTooManyRequestsRetryCount = 0;

            BoundedCapacityPodFailureProof? podFailureProof = null;

            var observation =
                new BoundedCapacityMachineLimitObservation(
                    maximumPodCount,
                    runtimeCountPerPod,
                    maximumRuntimeCapacity);

            try
            {
                await using var host =
                    new GenericMcpServerTestHost(settings);

                var configuredPodCreationExecutor =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolPodCreationExecutor>();

                Assert.IsType<
                    AiRuntimePoolPodCreationExecutor>(
                        configuredPodCreationExecutor);

                var configuredRuntimePoolOptions =
                    host.Services
                        .GetRequiredService<
                            IOptions<
                                AiKubernetesRuntimePoolOptions>>()
                        .Value;

                Assert.Equal(
                    maximumPodCount,
                    configuredRuntimePoolOptions.MaximumPodCount);

                var physicalPodInventory =
                    host.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodInventory>();

                var initialPhysicalPodCount =
                    await physicalPodInventory
                        .CountRuntimePoolPodsAsync(
                            configuredRuntimePoolOptions.Namespace,
                            configuredRuntimePoolOptions.PoolId)
                        .ConfigureAwait(false);

                Assert.Equal(0, initialPhysicalPodCount);

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY AUTHORITY] " +
                    $"Executor='{configuredPodCreationExecutor.GetType().Name}', " +
                    $"PhysicalInventory='{physicalPodInventory.GetType().Name}', " +
                    $"InitialPhysicalPodCount='{initialPhysicalPodCount}', " +
                    $"MaximumPodCount='{configuredRuntimePoolOptions.MaximumPodCount}', " +
                    $"PoolId='{configuredRuntimePoolOptions.PoolId}'.");

                var registry =
                    host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

                var sharedRunStore =
                    host.Services.GetRequiredService<IAiSharedRunStore>();

                var sharedQueue =
                    host.Services.GetRequiredService<IAiSharedQueue>();

                var scaleOutRequestStore =
                    host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

                var runExecutionIndex =
                    host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

                var dagStore =
                    host.Services.GetRequiredService<IAiDagExecutionStore>();

                var forensicsQueryService =
                    host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

                using var httpClient =
                    host.CreateClient();

                httpClient.Timeout =
                    TimeSpan.FromMinutes(15);

                var mcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            host,
                            httpClient,
                            boundedCapacityProfile.RequestedBy,
                            tenantId: tenant.TenantId,
                            tenantGroupId: tenant.TenantGroupId)
                        .ConfigureAwait(false);

                await WaitForBoundedCapacityScaleOutWatcherReadyAsync(
                        host.Services,
                        controlPlaneId)
                    .ConfigureAwait(false);

                CompletePhase(
                    "Setup host services, tenant MCP client, and scale-out watcher");

                WritePhaseHeader(
                    2,
                    "SUBMIT FULL-CAPACITY WAVES",
                    "[PASS TARGET] Submit every run through a dynamically bounded MCP producer, honor transient 429 backpressure, and persist every logical run through QueueFirst admission without waiting for DAG completion.");

                using var observationCancellation =
                    new CancellationTokenSource();

                var observationTask =
                    ObserveBoundedCapacityAsync(
                        registry,
                        sharedRunStore,
                        sharedQueue,
                        scaleOutRequestStore,
                        controlPlaneId,
                        poolId,
                        tenant.TenantId,
                        observation,
                        observationCancellation.Token);

                try
                {
                    RuntimePoolProductionCycleAdmissionProof admissionProof;

                    var submissionStopwatch =
                        Stopwatch.StartNew();

                    try
                    {
                        admissionProof =
                            await RuntimePoolProductionCycleExecutor
                                .SubmitQueueFirstWavesAsync(
                                    mcp,
                                    tenant,
                                    scenario.Name,
                                    controlPlaneId,
                                    boundedCapacityProfile.RequestedBy,
                                    boundedCapacityProfile.Source,
                                    runsPerIteration,
                                    submissionIterationCount,
                                    maximumConcurrentMcpSubmissions,
                                    maximumAdmissionAttemptCount)
                                .ConfigureAwait(false);

                        admissionTooManyRequestsRetryCount =
                            admissionProof.TooManyRequestsRetryCount;

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY MCP ADMISSION] " +
                            $"MaximumConcurrentSubmissions='" +
                            $"{maximumConcurrentMcpSubmissions}', " +
                            $"TooManyRequestsRetryCount='" +
                            $"{Volatile.Read(ref admissionTooManyRequestsRetryCount)}'.");
                    }
                    finally
                    {
                        submissionStopwatch.Stop();
                        submissionDuration = submissionStopwatch.Elapsed;
                    }

                    var submittedSharedRunIds =
                        admissionProof.SharedRunIds;

                    CompletePhase(
                        "Submit bounded-capacity waves");

                    WritePhaseHeader(
                        3,
                        injectPodFailure
                            ? "FORCE-DELETE ONE BUSY POD, RECOVER, AND DRAIN EVERY DAG"
                            : "DRAIN SHARED QUEUE AND COMPLETE EVERY DAG",
                        injectPodFailure
                            ? "[PASS TARGET] Kill one Pod only after all of its runtimes own active work, suppress that exact membership, create one fresh replacement Pod, recover the impacted work once, and complete all 50 DAG steps."
                            : "[PASS TARGET] Resolve every SharedRunId to one local run and one execution, then complete all 50 DAG steps without loss or duplicate dispatch.");

                    var drainStopwatch =
                        Stopwatch.StartNew();

                IReadOnlyList<BoundedCapacityCompletedRun> finalRuns;

                try
                {
                    if (injectPodFailure)
                    {
                        podFailureProof =
                            await InjectBoundedCapacityPodFailureAsync(
                                    host.Services,
                                    registry,
                                    sharedRunStore,
                                    runExecutionIndex,
                                    submittedSharedRunIds,
                                    tenant,
                                    controlPlaneId,
                                    poolId,
                                    runtimeCountPerPod,
                                    maximumRuntimeCapacity,
                                    observation,
                                    TimeSpan.FromMinutes(10))
                                .ConfigureAwait(false);
                    }

                    finalRuns =
                        await WaitForSubmittedRunsToCompleteAsync(
                                sharedRunStore,
                                runExecutionIndex,
                                dagStore,
                                submittedSharedRunIds,
                                controlPlaneId,
                                tenant.TenantId,
                                observation,
                                scenario.CompletionTimeout,
                                TimeSpan.FromMinutes(5))
                            .ConfigureAwait(false);
                }
                finally
                {
                    drainStopwatch.Stop();
                    drainDuration = drainStopwatch.Elapsed;
                }

                await Task.WhenAll(
                        finalRuns.Select(
                            run =>
                                ProductionRecoveryWaitHelpers
                                    .WaitForDagCompletedStepCountAsync(
                                        dagStore,
                                        run.ExecutionId,
                                        stepCount,
                                        TimeSpan.FromMinutes(2))))
                    .ConfigureAwait(false);

                using var runtimeStatusProofHttpClient =
                    host.CreateClient();

                runtimeStatusProofHttpClient.Timeout =
                    TimeSpan.FromMinutes(15);

                var runtimeStatusProofMcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            host,
                            runtimeStatusProofHttpClient,
                            boundedCapacityProfile.RequestedBy,
                            tenantId: tenant.TenantId,
                            tenantGroupId:
                                tenant.TenantGroupId)
                        .ConfigureAwait(false);

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY RUNTIME STATUS PROOF] DedicatedRbacContext='true', RunCount='{finalRuns.Count}'.");

                var finalRuntimeStatuses =
                    await McpTestWaitHelpers
                        .WaitForTerminalRuntimeRunStatusesAsync(
                            runtimeStatusProofMcp,
                            finalRuns
                                .Select(run => run.SharedRun)
                                .ToArray(),
                            timeout: scenario.CompletionTimeout)
                        .ConfigureAwait(false);

                Assert.Equal(
                    submittedRunCount,
                    finalRuntimeStatuses.Count);

                foreach (var finalRuntimeStatus in finalRuntimeStatuses)
                {
                    Assert.True(
                        finalRuntimeStatus.Success,
                        finalRuntimeStatus.FailureReason ??
                        finalRuntimeStatus.Message);

                    Assert.True(
                        string.Equals(
                            finalRuntimeStatus.RunState?.Status,
                            "completed",
                            StringComparison.OrdinalIgnoreCase),
                        $"Bounded-capacity runtime work did not complete. RuntimeInstanceId='{finalRuntimeStatus.RuntimeInstanceId}', RunId='{finalRuntimeStatus.RunId}', ExecutionId='{finalRuntimeStatus.ExecutionId ?? finalRuntimeStatus.RunState?.ExecutionId}', Status='{finalRuntimeStatus.RunState?.Status}', RunStateFailureReason='{finalRuntimeStatus.RunState?.FailureReason}', ControlPlaneFailureReason='{finalRuntimeStatus.FailureReason}', Message='{finalRuntimeStatus.Message}'.");
                }

                CompletePhase(
                    injectPodFailure
                        ? "Force-delete one busy Pod, recover exact assigned work, and complete every DAG"
                        : "Drain shared queue and complete every DAG");

                WritePhaseHeader(
                    4,
                    injectPodFailure
                        ? "BOUNDED CAPACITY AND EXACT POD-RECOVERY SAFETY PROOF"
                        : "BOUNDED CAPACITY AND NO-RECOVERY SAFETY PROOF",
                    injectPodFailure
                        ? "[PASS TARGET] Prove exact failed-Pod membership suppression, fresh replacement membership, bounded active capacity, an empty shared queue, and recovery forensics tied only to the injected Pod failure."
                        : "[PASS TARGET] Prove bounded Pod/runtime topology, exact dispatch identity, an empty shared queue, and zero recovery forensics without injected failure.");

                observationCancellation.Cancel();

                await observationTask
                    .ConfigureAwait(false);

                observation.ThrowIfViolated();

                Assert.Equal(
                    submittedRunCount,
                    finalRuns
                        .Select(run => run.LocalRunId)
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                Assert.Equal(
                    submittedRunCount,
                    finalRuns
                        .Select(run => run.ExecutionId)
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                AiRuntimeRecoveryForensicsQueryResult recoveryForensics;

                if (injectPodFailure)
                {
                    var requiredPodFailureProof =
                        podFailureProof
                        ?? throw new InvalidOperationException(
                            "The bounded-capacity Pod failure proof was not captured.");

                    var exactRecoveryForensics =
                        new List<AiRuntimeRecoveryForensicsReadModel>(
                            requiredPodFailureProof.RecoveryForensicsIds.Count);

                    foreach (var forensicsId in
                             requiredPodFailureProof.RecoveryForensicsIds
                                 .OrderBy(value => value, StringComparer.Ordinal))
                    {
                        var record =
                            await forensicsQueryService
                                .GetByForensicsIdAsync(forensicsId)
                                .ConfigureAwait(false);

                        Assert.NotNull(record);
                        exactRecoveryForensics.Add(record!);
                    }

                    recoveryForensics =
                        new AiRuntimeRecoveryForensicsQueryResult
                        {
                            Items = exactRecoveryForensics,
                            Limit = exactRecoveryForensics.Count
                        };

                    Assert.Equal(
                        requiredPodFailureProof.RecoveredSharedRunIds.Count,
                        recoveryForensics.Count);
                    Assert.All(
                        recoveryForensics.Items,
                        record =>
                        {
                            Assert.Contains(
                                record.ForensicsId,
                                requiredPodFailureProof.RecoveryForensicsIds);
                            Assert.Equal(
                                requiredPodFailureProof.FailureId,
                                record.RuntimeFailureIncidentId);
                            Assert.False(
                                string.IsNullOrWhiteSpace(record.SharedRunId));
                            Assert.Contains(
                                record.SharedRunId!,
                                requiredPodFailureProof.RecoveredSharedRunIds);
                            Assert.Contains(
                                record.ExecutionId,
                                requiredPodFailureProof.ImpactedExecutionIds);
                        });
                }
                else
                {
                    recoveryForensics =
                        await forensicsQueryService
                            .SearchAsync(
                                new AiRuntimeRecoveryForensicsQuery
                                {
                                    TenantId = tenant.TenantId,
                                    TenantGroupId = tenant.TenantGroupId,
                                    ControlPlaneId = controlPlaneId,
                                    Limit = Math.Max(100, submittedRunCount)
                                })
                            .ConfigureAwait(false);

                    Assert.True(
                        recoveryForensics.Count == 0,
                        string.Concat(
                            "The bounded-capacity workload produced runtime recovery forensics even though no failure was injected.",
                            Environment.NewLine,
                            string.Join(
                                Environment.NewLine,
                                recoveryForensics.Items.Select(
                                    record =>
                                        $"ForensicsId='{record.ForensicsId}', SharedRunId='{record.SharedRunId}', ExecutionId='{record.ExecutionId}', RuntimeFailureIncidentId='{record.RuntimeFailureIncidentId}'."))));
                }

                observation.ObserveFinalDispatchBindings(
                    finalRuns.Select(run => run.SharedRun));

                    var finalPoolRuntimes =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            snapshot => StringComparer.Ordinal.Equals(
                                snapshot.PoolId,
                                poolId))
                        .ToArray();

                var finalPods =
                    finalPoolRuntimes
                        .GroupBy(
                            snapshot => snapshot.HostId,
                            StringComparer.Ordinal)
                        .ToArray();

                if (injectPodFailure)
                {
                    Assert.Equal(maximumPodCount, finalPods.Length);
                    Assert.Equal(maximumRuntimeCapacity, finalPoolRuntimes.Length);
                }
                else
                {
                    Assert.InRange(
                        finalPods.Length,
                        1,
                        maximumPodCount);
                }

                if (injectPodFailure)
                {
                    var requiredPodFailureProof =
                        podFailureProof
                        ?? throw new InvalidOperationException(
                            "The bounded-capacity Pod failure proof was not captured.");

                    Assert.DoesNotContain(
                        finalPoolRuntimes,
                        runtime =>
                            requiredPodFailureProof.FailedRuntimeInstanceIds.Contains(
                                runtime.RuntimeInstanceId));
                    Assert.All(
                        requiredPodFailureProof.ReplacementRuntimeInstanceIds,
                        runtimeInstanceId => Assert.Contains(
                            finalPoolRuntimes,
                            runtime => StringComparer.Ordinal.Equals(
                                runtime.RuntimeInstanceId,
                                runtimeInstanceId)));
                    Assert.Contains(
                        finalPods,
                        pod => StringComparer.Ordinal.Equals(
                            pod.Key,
                            requiredPodFailureProof.ReplacementPodUid));
                }

                Assert.All(
                    finalPods,
                    pod =>
                    {
                        Assert.False(string.IsNullOrWhiteSpace(pod.Key));
                        Assert.Equal(runtimeCountPerPod, pod.Count());
                        Assert.All(
                            pod,
                            runtime => Assert.Equal(
                                AiRuntimeInstanceStatus.Ready,
                                runtime.Status));
                    });

                var finalQueueItems =
                    (await sharedQueue
                            .ListAsync(includeTerminal: false)
                            .ConfigureAwait(false))
                        .Where(
                            item => StringComparer.Ordinal.Equals(
                                item.ControlPlaneId,
                                controlPlaneId))
                        .ToArray();

                Assert.Empty(finalQueueItems);

                using var noRecoveryProofHttpClient =
                    host.CreateClient();

                noRecoveryProofHttpClient.Timeout =
                    TimeSpan.FromMinutes(2);

                var noRecoveryProofMcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            host,
                            noRecoveryProofHttpClient,
                            boundedCapacityProfile.RequestedBy,
                            tenantId: tenant.TenantId,
                            tenantGroupId:
                                tenant.TenantGroupId)
                        .ConfigureAwait(false);

                AiRuntimeRecoveryForensicsQueryResult forensics;

                if (injectPodFailure)
                {
                    var requiredPodFailureProof =
                        podFailureProof
                        ?? throw new InvalidOperationException(
                            "The bounded-capacity Pod failure proof was not captured.");

                    var exactMcpRecoveryForensics =
                        new List<AiRuntimeRecoveryForensicsReadModel>(
                            requiredPodFailureProof.RecoveryForensicsIds.Count);

                    foreach (var forensicsId in
                             requiredPodFailureProof.RecoveryForensicsIds
                                 .OrderBy(value => value, StringComparer.Ordinal))
                    {
                        var exactResult =
                            await SearchRuntimeRecoveryForensicsWithBackpressureAsync(
                                    noRecoveryProofMcp,
                                    new AiRuntimeRecoveryForensicsQuery
                                    {
                                        ForensicsId = forensicsId,
                                        Limit = 1
                                    },
                                    maximumAttemptCount: 8)
                                .ConfigureAwait(false);

                        var exactRecord = Assert.Single(exactResult.Items);
                        Assert.Equal(forensicsId, exactRecord.ForensicsId);
                        exactMcpRecoveryForensics.Add(exactRecord);
                    }

                    forensics =
                        new AiRuntimeRecoveryForensicsQueryResult
                        {
                            Items = exactMcpRecoveryForensics,
                            Limit = exactMcpRecoveryForensics.Count
                        };

                    Assert.Equal(
                        requiredPodFailureProof.RecoveredSharedRunIds.Count,
                        forensics.Count);
                    Assert.All(
                        forensics.Items,
                        record => Assert.Equal(
                            requiredPodFailureProof.FailureId,
                            record.RuntimeFailureIncidentId));
                }
                else
                {
                    forensics =
                        await SearchRuntimeRecoveryForensicsWithBackpressureAsync(
                                noRecoveryProofMcp,
                                new AiRuntimeRecoveryForensicsQuery
                                {
                                    ControlPlaneId = controlPlaneId,
                                    TenantId = tenant.TenantId,
                                    Limit = submittedRunCount
                                },
                                maximumAttemptCount: 8)
                            .ConfigureAwait(false);

                    Assert.Empty(forensics.Items);
                }

                var scaleOutRequests =
                    observation.GetScaleOutRequests(
                        submittedSharedRunIds);

                Assert.True(
                    scaleOutRequests.Count > 0,
                    "The bounded-capacity observer did not capture any scale-out request before the operational Redis records expired.");

                Assert.All(
                    scaleOutRequests,
                    request => Assert.Equal(
                        0,
                        request.AvailableInstanceCount));

                var completedRunCount =
                    finalRuns.Count;

                const int failedRunCount = 0;

                var lostRunCount =
                    submittedRunCount - finalRuns.Count;

                var duplicateDispatchBindings =
                    observation.GetDuplicateDispatchBindings(
                        submittedSharedRunIds);

                var recoveredSharedRunIds =
                    podFailureProof?.RecoveredSharedRunIds
                    ?? (IReadOnlySet<string>)new HashSet<string>(
                        StringComparer.Ordinal);

                var unexpectedDuplicateDispatchBindings =
                    duplicateDispatchBindings
                        .Where(
                            item =>
                                item.Value.Count > 1 &&
                                (!recoveredSharedRunIds.Contains(item.Key) ||
                                 item.Value.Count > 2))
                        .ToDictionary(
                            item => item.Key,
                            item => item.Value,
                            StringComparer.Ordinal);

                var recoveryRedispatchBindingCount =
                    duplicateDispatchBindings.Count(
                        item =>
                            recoveredSharedRunIds.Contains(item.Key) &&
                            item.Value.Count == 2);

                var duplicateDispatchCount =
                    unexpectedDuplicateDispatchBindings.Sum(
                        item =>
                            Math.Max(
                                0,
                                item.Value.Count - 1));

                if (duplicateDispatchCount > 0)
                {
                    output.WriteLine(
                        $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY DUPLICATE DISPATCH DIAGNOSTICS] Count='{duplicateDispatchCount}'.");

                    foreach (var duplicate in
                        unexpectedDuplicateDispatchBindings
                            .Where(item => item.Value.Count > 1)
                            .OrderBy(
                                item => item.Key,
                                StringComparer.Ordinal))
                    {
                        output.WriteLine(
                            $"  SharedRunId='{duplicate.Key}', Bindings='{string.Join(",", duplicate.Value.OrderBy(binding => binding, StringComparer.Ordinal))}'.");
                    }
                }

                var effectiveMaximumObservedPodCount =
                    Math.Max(
                        observation.MaximumObservedPodCount,
                        finalPods.Length);

                var effectiveMaximumObservedRuntimeCount =
                    Math.Max(
                        observation.MaximumObservedRuntimeCount,
                        finalPoolRuntimes.Length);

                var configuredFullCapacityReached =
                    effectiveMaximumObservedPodCount == maximumPodCount &&
                    effectiveMaximumObservedRuntimeCount == maximumRuntimeCapacity;

                Assert.Empty(observation.Violations);
                Assert.InRange(
                    effectiveMaximumObservedPodCount,
                    1,
                    maximumPodCount);
                Assert.InRange(
                    effectiveMaximumObservedRuntimeCount,
                    runtimeCountPerPod,
                    maximumRuntimeCapacity);
                Assert.Equal(
                    finalPods.Length * runtimeCountPerPod,
                    finalPoolRuntimes.Length);

                if (workloadExceedsMaximumCapacity)
                {
                    Assert.True(
                        observation.MaximumSharedQueuedRunCount > 0,
                        "A workload larger than the configured simultaneous runtime capacity must create observable shared-queue pressure.");
                }

                Assert.Equal(0, duplicateDispatchCount);
                Assert.Equal(0, lostRunCount);
                Assert.Equal(0, failedRunCount);
                Assert.Equal(submittedRunCount, completedRunCount);

                CompletePhase(
                    injectPodFailure
                        ? "Validate bounded capacity, exact Pod recovery, queue drain, and dispatch convergence"
                        : "Validate bounded capacity, queue drain, exact dispatch, and no recovery");

                WritePhaseHeader(
                    5,
                    "MCP REPLAY LEDGER AND TRACE PROOF",
                    "[PASS TARGET] Every completed execution must be replayable through MCP with execution ledger, trace, completion, and exact step-completion evidence; control-plane ledger entries must prove scale-out and dispatch.");

                using var replayProofHttpClient =
                    host.CreateClient();

                replayProofHttpClient.Timeout =
                    TimeSpan.FromMinutes(15);

                var replayProofMcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            host,
                            replayProofHttpClient,
                            boundedCapacityProfile.RequestedBy,
                            tenantId: tenant.TenantId,
                            tenantGroupId:
                                tenant.TenantGroupId)
                        .ConfigureAwait(false);

                var phase5TooManyRequestsRetryCount = 0;

                void ObservePhase5BackpressureRetry(
                    string operationName,
                    int attempt,
                    TimeSpan retryDelay)
                {
                    _ = operationName;
                    _ = attempt;
                    _ = retryDelay;

                    Interlocked.Increment(
                        ref phase5TooManyRequestsRetryCount);
                }

                var replayProofs =
                    await RecoveredExecutionReplayProofAssertions
                        .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                            replayProofMcp,
                            tenant.TenantId,
                            finalRuntimeStatuses,
                            boundedCapacityProfile.RequestedBy,
                            boundedCapacityProfile.Source,
                            ObservePhase5BackpressureRetry)
                        .ConfigureAwait(false);

                Assert.Equal(
                    submittedRunCount,
                    replayProofs.Count);

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY REPLAY PROOF] " +
                    $"TenantId='{tenant.TenantId}', " +
                    $"ReplayProofCount='{replayProofs.Count}', " +
                    $"ExecutionIds='{string.Join(",", replayProofs.Select(proof => proof.ExecutionId))}'.");

                var ledgerTimelineToUtc =
                    DateTimeOffset.UtcNow.AddSeconds(5);

                var executionLedgerEntries =
                    new List<AiDecisionLedgerEntry>();

                foreach (var executionIdBatch in replayProofs
                             .Select(proof => proof.ExecutionId)
                             .Distinct(StringComparer.Ordinal)
                             .Chunk(8))
                {
                    var currentBatch =
                        await Task.WhenAll(
                                executionIdBatch.Select(
                                    executionId =>
                                        McpBackpressureRetryHelper
                                            .ExecuteAsync(
                                                () => replayProofMcp.QueryLedgerAsync(
                                                    new AiDecisionLedgerQuery
                                                    {
                                                        ExecutionId = executionId,
                                                        TimestampFromUtc = ledgerTimelineFromUtc,
                                                        TimestampToUtc = ledgerTimelineToUtc
                                                    }),
                                                $"observability.ledger.execution:{tenant.TenantId}:{executionId}",
                                                onRetry: ObservePhase5BackpressureRetry)))
                            .ConfigureAwait(false);

                    Assert.All(
                        currentBatch,
                        entries => Assert.NotEmpty(entries));

                    executionLedgerEntries.AddRange(
                        currentBatch.SelectMany(entries => entries));
                }

                var controlPlaneLedgerEntries =
                    new List<AiDecisionLedgerEntry>();

                foreach (var sharedRunIdBatch in submittedSharedRunIds
                             .OrderBy(value => value, StringComparer.Ordinal)
                             .Chunk(8))
                {
                    var currentBatch =
                        await Task.WhenAll(
                                sharedRunIdBatch.Select(
                                    sharedRunId =>
                                        McpBackpressureRetryHelper
                                            .ExecuteAsync(
                                                () => replayProofMcp.QueryLedgerAsync(
                                                    new AiDecisionLedgerQuery
                                                    {
                                                        ExecutionId =
                                                            $"control-plane-run:{sharedRunId}",
                                                        TimestampFromUtc = ledgerTimelineFromUtc,
                                                        TimestampToUtc = ledgerTimelineToUtc
                                                    }),
                                                $"observability.ledger.control-plane-run:{tenant.TenantId}:{sharedRunId}",
                                                onRetry: ObservePhase5BackpressureRetry)))
                            .ConfigureAwait(false);

                    Assert.All(
                        currentBatch,
                        entries => Assert.NotEmpty(entries));

                    controlPlaneLedgerEntries.AddRange(
                        currentBatch.SelectMany(entries => entries));
                }

                var runtimeLifecycleLedgerEntries =
                    new List<AiDecisionLedgerEntry>();

                var assignedRuntimeInstanceIds =
                    finalRuns
                        .Select(run => run.SharedRun.AssignedRuntimeInstanceId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                foreach (var runtimeInstanceIdBatch in assignedRuntimeInstanceIds
                             .Chunk(8))
                {
                    var currentBatch =
                        await Task.WhenAll(
                                runtimeInstanceIdBatch.Select(
                                    runtimeInstanceId =>
                                        McpBackpressureRetryHelper
                                            .ExecuteAsync(
                                                () => replayProofMcp.QueryLedgerAsync(
                                                    new AiDecisionLedgerQuery
                                                    {
                                                        ExecutionId =
                                                            $"control-plane-runtime-instance:{runtimeInstanceId}",
                                                        TimestampFromUtc = ledgerTimelineFromUtc,
                                                        TimestampToUtc = ledgerTimelineToUtc
                                                    }),
                                                $"observability.ledger.runtime-instance:{tenant.TenantId}:{runtimeInstanceId}",
                                                onRetry: ObservePhase5BackpressureRetry)))
                            .ConfigureAwait(false);

                    runtimeLifecycleLedgerEntries.AddRange(
                        currentBatch.SelectMany(entries => entries));
                }

                var combinedLedgerEntries =
                    executionLedgerEntries
                        .Concat(controlPlaneLedgerEntries)
                        .Concat(runtimeLifecycleLedgerEntries)
                        .DistinctBy(entry => entry.EntryId)
                        .OrderBy(entry => entry.TimestampUtc)
                        .ThenBy(entry => entry.Sequence)
                        .ToArray();

                var stepCompletedLedgerCount =
                    executionLedgerEntries.Count(
                        entry => string.Equals(
                            entry.EventType,
                            AiEngineEvents.Step.Completed,
                            StringComparison.OrdinalIgnoreCase));

                var stepCompletionLedgerProof =
                    RuntimePoolProductionCycleExecutor
                        .AssertLogicalStepCompletionEvidence(
                            executionLedgerEntries,
                            replayProofs
                                .Select(proof => proof.ExecutionId)
                                .ToHashSet(StringComparer.Ordinal),
                            podFailureProof?.ImpactedExecutionIds ??
                                new HashSet<string>(StringComparer.Ordinal),
                            stepCount,
                            "Bounded-capacity logical step completion ledger proof");

                var dispatchLedgerProof =
                    RuntimePoolProductionCycleExecutor
                        .AssertDurableDispatchEvidence(
                            submittedSharedRunIds,
                            podFailureProof?.RecoveredSharedRunIds ??
                                new HashSet<string>(StringComparer.Ordinal),
                            controlPlaneLedgerEntries,
                            "Bounded-capacity durable dispatch ledger proof");

                var dispatchedSharedRunCount =
                    dispatchLedgerProof
                        .DurableDispatchProvenSharedRunIds
                        .Count;

                Assert.Equal(
                    stepCompletedLedgerCount,
                    stepCompletionLedgerProof.RawStepCompletedEntryCount);

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY STEP LEDGER PROOF] " +
                    $"ExpectedLogicalStepCount='{logicalStepCount}', " +
                    $"DistinctLogicalStepCompletedCount='{stepCompletionLedgerProof.DistinctLogicalStepCompletedCount}', " +
                    $"RawStepCompletedEntryCount='{stepCompletionLedgerProof.RawStepCompletedEntryCount}', " +
                    $"RecoveryCoveredDuplicateEntryCount='{stepCompletionLedgerProof.DuplicateStepCompletedEntryCount}', " +
                    $"DuplicateEvidenceExecutionIds='{string.Join(",", stepCompletionLedgerProof.DuplicateEvidenceExecutionIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY DISPATCH LEDGER PROOF] " +
                    $"SubmittedRunCount='{submittedRunCount}', " +
                    $"InitialDispatchSucceededCount='{dispatchLedgerProof.InitialDispatchSucceededSharedRunIds.Count}', " +
                    $"RecoveryCoveredMissingInitialDispatchCount='{dispatchLedgerProof.RecoveryCoveredSharedRunIds.Count}', " +
                    $"DurableDispatchProvenCount='{dispatchedSharedRunCount}', " +
                    $"RecoveryCoveredSharedRunIds='{string.Join(",", dispatchLedgerProof.RecoveryCoveredSharedRunIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                Assert.Contains(
                    controlPlaneLedgerEntries,
                    entry => entry.EventType.Contains(
                        "runtime-scale-out-request-watch.succeeded",
                        StringComparison.OrdinalIgnoreCase));

                var runtimeHostCreationLedgerEntryCount =
                    runtimeLifecycleLedgerEntries.Count(
                        entry => entry.EventType.Contains(
                            "runtime-host-creation.succeeded",
                            StringComparison.OrdinalIgnoreCase));

                if (injectPodFailure)
                {
                    Assert.Contains(
                        combinedLedgerEntries,
                        entry => entry.EventType.StartsWith(
                            "control.recovery.",
                            StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    Assert.DoesNotContain(
                        combinedLedgerEntries,
                        entry => entry.EventType.StartsWith(
                            "control.recovery.",
                            StringComparison.OrdinalIgnoreCase));
                }

                ProductionTenantLedgerSummaryOutput.Write(
                    output,
                    "TENANT-SCOPED LEDGER SUMMARY",
                    new[]
                    {
                        new ProductionTenantLedgerSummary(
                            tenant.TenantId,
                            finalRuns
                                .Select(run => run.SharedRun.AssignedRuntimeInstanceId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
                                .Distinct(StringComparer.Ordinal)
                                .ToArray(),
                            replayProofs
                                .Select(proof => proof.ExecutionId)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray(),
                            combinedLedgerEntries)
                    },
                    maxLedgerEntriesPerTenant: 50,
                    maxEventTypeRowsPerTenant: 30,
                    maxLedgerEntriesPerExecution: 25);

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY MCP REPLAY BACKPRESSURE] " +
                    "DedicatedRbacContext='true', " +
                    "MaximumAttemptCount='8', " +
                    $"TooManyRequestsRetryCount='{Volatile.Read(ref phase5TooManyRequestsRetryCount)}'.");

                CompletePhase(
                    "Validate MCP replay, execution ledger, trace, and control-plane ledger");

                WritePhaseHeader(
                    6,
                    "FINAL TOPOLOGY PERFORMANCE AND SAFETY PROOF",
                    "[PASS TARGET] Print complete Pod/runtime membership, run placement, timing, throughput, datastore, replay, ledger, and safety evidence before deterministic cleanup.");

                output.WriteLine(string.Empty);
                output.WriteLine(
                    "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY");
                output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
                output.WriteLine($"PoolId='{poolId}'");
                output.WriteLine(
                    "TopologySource='Current runtime registry + shared-run store + runtime execution index'");
                output.WriteLine($"ActiveKubernetesPodCount='{finalPods.Length}'");
                output.WriteLine($"ActiveRuntimeInstanceCount='{finalPoolRuntimes.Length}'");
                output.WriteLine($"RunPlacementCount='{finalRuns.Count}'");

                var orderedPods =
                    finalPods
                        .OrderBy(pod => pod.Key, StringComparer.Ordinal)
                        .ToArray();

                for (var podIndex = 0; podIndex < orderedPods.Length; podIndex++)
                {
                    var pod = orderedPods[podIndex];
                    var firstRuntime = pod.First();

                    output.WriteLine(
                        $"  Pod[{podIndex + 1:00}] HostId='{pod.Key}', " +
                        $"Namespace='{firstRuntime.KubernetesNamespace}', " +
                        $"PodName='{firstRuntime.KubernetesPodName}', " +
                        $"RuntimeCount='{pod.Count()}', " +
                        $"Statuses='{string.Join(",", pod.Select(runtime => runtime.Status).Distinct())}'.");

                    var orderedRuntimes =
                        pod
                            .OrderBy(
                                runtime => runtime.RuntimeInstanceId,
                                StringComparer.Ordinal)
                            .ToArray();

                    for (var runtimeIndex = 0;
                         runtimeIndex < orderedRuntimes.Length;
                         runtimeIndex++)
                    {
                        var runtime = orderedRuntimes[runtimeIndex];

                        output.WriteLine(
                            $"    Runtime[{runtimeIndex + 1:00}] " +
                            $"RuntimeInstanceId='{runtime.RuntimeInstanceId}', " +
                            $"Status='{runtime.Status}', " +
                            $"ProcessId='{runtime.ProcessId}', " +
                            $"TenantId='{runtime.TenantId}', " +
                            $"TenantGroupId='{runtime.TenantGroupId}'.");
                    }
                }

                var orderedRuns =
                    finalRuns
                        .OrderBy(
                            run => run.SharedRun.SharedRunId,
                            StringComparer.Ordinal)
                        .ToArray();

                for (var runIndex = 0; runIndex < orderedRuns.Length; runIndex++)
                {
                    var run = orderedRuns[runIndex];

                    output.WriteLine(
                        $"  Run[{runIndex + 1:000}] " +
                        //$"TenantId='{run.SharedRun.TenantId}', " +
                        $"SharedRunId='{run.SharedRun.SharedRunId}', " +
                        $"RuntimeInstanceId='{run.SharedRun.AssignedRuntimeInstanceId}', " +
                        $"LocalRunId='{run.LocalRunId}', " +
                        $"ExecutionId='{run.ExecutionId}', " +
                        $"CompletedAtUtc='{run.CompletedAtUtc:O}', " +
                        "Moved='false'.");
                }

                CompletePhase(
                    "Produce final topology, run placement, performance, and safety proof");

                totalStopwatch.Stop();

                var totalDuration =
                    submissionDuration + drainDuration;

                var scenarioTotalDuration =
                    totalStopwatch.Elapsed;

                var executionsPerSecond =
                    totalDuration.TotalSeconds <= 0
                        ? 0
                        : completedRunCount / totalDuration.TotalSeconds;

                var logicalStepsPerSecond =
                    totalDuration.TotalSeconds <= 0
                        ? 0
                        : logicalStepCount / totalDuration.TotalSeconds;

                output.WriteLine(string.Empty);
                output.WriteLine($"# {boundedCapacityProfile.LogPrefix} BOUNDED CAPACITY MACHINE LIMIT");
                output.WriteLine($"MaximumConfiguredPodCount={maximumPodCount}");
                output.WriteLine($"RuntimeCountPerPod={runtimeCountPerPod}");
                output.WriteLine($"MaximumRuntimeCapacity={maximumRuntimeCapacity}");
                output.WriteLine($"SubmissionIterationCount={submissionIterationCount}");
                output.WriteLine($"RunsPerIteration={runsPerIteration}");
                output.WriteLine($"SubmittedRunCount={submittedRunCount}");
                output.WriteLine($"CompletedRunCount={completedRunCount}");
                output.WriteLine($"LogicalStepCount={logicalStepCount}");
                output.WriteLine($"MaximumObservedPodCount={effectiveMaximumObservedPodCount}");
                output.WriteLine($"MaximumObservedRuntimeCount={effectiveMaximumObservedRuntimeCount}");
                output.WriteLine($"MaximumSharedQueuedRunCount={observation.MaximumSharedQueuedRunCount}");
                output.WriteLine($"ConfiguredFullCapacityReached={configuredFullCapacityReached.ToString().ToLowerInvariant()}");
                output.WriteLine($"ObservedFullCapacityWithQueuedRuns={observation.ObservedFullCapacityWithQueuedRuns.ToString().ToLowerInvariant()}");
                output.WriteLine($"ScaleOutRequestCount={scaleOutRequests.Select(request => request.RequestId).Distinct(StringComparer.Ordinal).Count()}");
                output.WriteLine($"DuplicateDispatchCount={duplicateDispatchCount}");
                output.WriteLine($"LostRunCount={lostRunCount}");
                output.WriteLine($"FailedRunCount={failedRunCount}");
                output.WriteLine($"PodFailureInjected={injectPodFailure.ToString().ToLowerInvariant()}");
                output.WriteLine($"RecoveryRedispatchBindingCount={recoveryRedispatchBindingCount}");
                if (podFailureProof is not null)
                {
                    output.WriteLine($"PodFailureId={podFailureProof.FailureId}");
                    output.WriteLine($"FailedPodUid={podFailureProof.FailedPodUid}");
                    output.WriteLine($"FailedPodName={podFailureProof.FailedPodName}");
                    output.WriteLine($"ReplacementPodUid={podFailureProof.ReplacementPodUid}");
                    output.WriteLine($"FailedPodRuntimeCount={podFailureProof.FailedRuntimeInstanceIds.Count}");
                    output.WriteLine($"ReplacementRuntimeCount={podFailureProof.ReplacementRuntimeInstanceIds.Count}");
                    output.WriteLine($"RecoveredSharedRunCount={podFailureProof.RecoveredSharedRunIds.Count}");
                }
                output.WriteLine($"SubmissionDuration={submissionDuration}");
                output.WriteLine($"DrainDuration={drainDuration}");
                output.WriteLine($"TotalDuration={totalDuration}");
                output.WriteLine($"ScenarioTotalDuration={scenarioTotalDuration}");
                output.WriteLine($"ExecutionsPerSecond={executionsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}");
                output.WriteLine($"LogicalStepsPerSecond={logicalStepsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}");
                output.WriteLine($"ReplayProofCount={replayProofs.Count}");
                output.WriteLine($"ExecutionLedgerEntryCount={executionLedgerEntries.Count}");
                output.WriteLine($"ControlPlaneLedgerEntryCount={controlPlaneLedgerEntries.Count}");
                output.WriteLine($"RuntimeLifecycleLedgerEntryCount={runtimeLifecycleLedgerEntries.Count}");
                output.WriteLine($"RuntimeHostCreationLedgerEntryCount={runtimeHostCreationLedgerEntryCount}");
                output.WriteLine($"DispatchedSharedRunLedgerCount={dispatchedSharedRunCount}");
                output.WriteLine($"RawStepCompletedLedgerEntryCount={stepCompletionLedgerProof.RawStepCompletedEntryCount}");
                output.WriteLine($"DistinctLogicalStepCompletedLedgerCount={stepCompletionLedgerProof.DistinctLogicalStepCompletedCount}");
                output.WriteLine($"RecoveryCoveredDuplicateStepCompletedLedgerEntryCount={stepCompletionLedgerProof.DuplicateStepCompletedEntryCount}");
                output.WriteLine($"RecoveryForensicsCount={recoveryForensics.Count}");
                output.WriteLine($"ComparableP35ExecutionCount={comparableP35ExecutionCount}");
                output.WriteLine($"ComparableP35LogicalStepCount={comparableP35LogicalStepCount}");

                output.WriteLine(string.Empty);
                output.WriteLine("Safety:");
                output.WriteLine("  QueueDrained='true'");
                output.WriteLine("  ExactDispatchValidated='true'");
                output.WriteLine("  DagCompletionValidated='true'");
                output.WriteLine("  ReplayValidated='true'");
                output.WriteLine("  LedgerValidated='true'");
                output.WriteLine("  LogicalStepLedgerIdentityValidated='true'");
                output.WriteLine("  DuplicateStepLedgerEvidenceOutsideRecoveryDetected='false'");
                output.WriteLine("  TraceValidated='true'");
                output.WriteLine(
                    injectPodFailure
                        ? "  ExactPodRecoveryValidated='true'"
                        : "  RecoveryContaminationDetected='false'");
                output.WriteLine("  DuplicateDispatchDetected='false'");
                output.WriteLine("  LostRunDetected='false'");
                output.WriteLine("  FailedRunDetected='false'");
                output.WriteLine("  PodCapacityExceeded='false'");
                output.WriteLine("  RuntimeCapacityExceeded='false'");

                WriteTimingSummary();
                output.WriteLine(string.Empty);
                }
                finally
                {
                    observationCancellation.Cancel();

                    await observationTask
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await CaptureBoundedCapacityFailureDiagnosticsAsync(
                        controlPlaneId,
                        poolId,
                        exception)
                    .ConfigureAwait(false);

                throw;
            }
            finally
            {
                totalStopwatch.Stop();

                await OnCrashRecoveryScenarioCompletedAsync(controlPlaneId)
                    .ConfigureAwait(false);
            }
        }

        private async Task ExecuteReusableBoundedCapacityPodFailureProductionScenarioCoreAsync(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            bool injectChildRuntimeFailure,
            bool waitForExternalPodDeletion = false,
            int childDepth = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIterationCount);
            ArgumentOutOfRangeException.ThrowIfNegative(childDepth);

            if (executionCycleCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(executionCycleCount),
                    executionCycleCount,
                    "The warm-pool reuse proof requires at least two sequential execution cycles.");
            }

            if (injectChildRuntimeFailure)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(
                    maximumPodCount,
                    3);
                ArgumentOutOfRangeException.ThrowIfLessThan(
                    runtimeCountPerPod,
                    2);

                if (submissionIterationCount < 2)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(submissionIterationCount),
                        submissionIterationCount,
                        "The final hierarchical KubernetesPool proof requires at least two full-capacity waves so the last configured wave can be deferred until after child-runtime recovery.");
                }
            }

            const int stepCount = 50;
            const int maximumAdmissionAttemptCount = 8;

            var parentLogicalStepCount =
                checked(stepCount + (childDepth > 0 ? 1 : 0));

            var runsPerIteration =
                checked(maximumPodCount * runtimeCountPerPod);

            var submittedRunCountPerCycle =
                checked(runsPerIteration * submissionIterationCount);

            var logicalStepCountPerCycle =
                checked(submittedRunCountPerCycle * parentLogicalStepCount);

            var maximumRuntimeCapacity =
                checked(maximumPodCount * runtimeCountPerPod);

            var totalSubmittedRunCount =
                checked(submittedRunCountPerCycle * executionCycleCount);

            var totalLogicalStepCount =
                checked(logicalStepCountPerCycle * executionCycleCount);

            var maximumConcurrentMcpSubmissions =
                Math.Clamp(
                    maximumRuntimeCapacity,
                    4,
                    16);

            var boundedCapacityProfile =
                boundedCapacityProfileFactory(
                    maximumPodCount,
                    runtimeCountPerPod);

            var baseScenario =
                ProductionRuntimeScenarioFactory
                    .CreateSingleTenantDedicatedRuntimeModeScenario();

            var templateTenant =
                baseScenario.Tenants.Single();

            var tenant =
                templateTenant with
                {
                    TenantId = "tenant-kubernetes-pool-machine-limit",
                    TenantGroupId = "tenant-kubernetes-pool-machine-limit-group",
                    RuntimeMode = ProductionTenantRuntimeMode.Shared,
                    ExpectDedicatedRuntimePrefix = false,
                    RuntimeInstanceIdPrefix = "kubernetes-pool-machine-limit-runtime",
                    MaxRuntimeInstances = maximumRuntimeCapacity,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = totalSubmittedRunCount,
                        StepCount = stepCount,
                        DelayMs = 750,
                        FlakyStepInterval = 0,
                        EnableRetention = false,
                        ChildDepth = childDepth
                    }
                };

            var scenario =
                baseScenario with
                {
                    Name = injectChildRuntimeFailure
                        ? $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-full-failure-production"
                        : $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-warm-reuse-pod-failure-production",
                    ControlPlaneIdPrefix = injectChildRuntimeFailure
                        ? $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-full-failure"
                        : $"{boundedCapacityProfile.ProviderName}-kubernetes-runtime-pool-warm-reuse-pod-failure",
                    Tenants = new[] { tenant },
                    PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                    ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                    SubmitMode = ProductionRuntimeSubmitMode.QueueFirst,
                    ScaleOutTimeout = TimeSpan.FromMinutes(5),
                    DispatchTimeout = TimeSpan.FromMinutes(15),
                    CompletionTimeout = TimeSpan.FromMinutes(60),
                    Assertions = new ProductionRuntimeScenarioAssertionOptions
                    {
                        AssertAllRunsCompleted = true,
                        AssertTenantIsolation = true,
                        AssertScaleOut = true,
                        AssertMaxRuntimeInstances = true,
                        AssertLedger = true,
                        AssertTrace = true,
                        AssertReplayReport = true,
                        AssertReplayLedger = true,
                        AssertReplayTrace = true
                    }
                };

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var poolId =
                ResolvePoolId(controlPlaneId);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver
                    .ResolveRuntimeHostAssemblyPath();

            var settings =
                boundedCapacityProfile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            settings["Tests:UseCapturingLedgerRecorder"] = "false";
            settings["Tests:UseMongoRuntimeLifecycleJournal"] = "true";
            settings["AiRuntimeRecoveryForensics:StrictPersistence"] = "true";

            await using var dataStoreTrafficObserver =
                await ProductionDataStoreTrafficObserver
                    .StartAsync(output)
                    .ConfigureAwait(false);

            await OnCrashRecoveryScenarioStartingAsync(controlPlaneId)
                .ConfigureAwait(false);

            var totalStopwatch =
                Stopwatch.StartNew();

            output.WriteLine(
                $"# {boundedCapacityProfile.LogPrefix} WARM REUSE PRODUCTION PROOF");
            output.WriteLine(
                injectChildRuntimeFailure
                    ? waitForExternalPodDeletion
                        ? "Executive proof: one bounded Kubernetes Runtime Pool executes repeated production cycles, kills one exact in-Pod runtime after durable progress while its Pod and siblings survive, then exposes one distinct fully busy Pod and waits for an operator to force-delete it externally before recovery, warm reuse, and final cleanup."
                        : "Executive proof: one bounded Kubernetes Runtime Pool executes repeated production cycles, kills one exact in-Pod runtime after durable progress while its Pod and siblings survive, then force-deletes one distinct busy Pod, reuses the converged capacity, and cleans only after the final cycle."
                    : "Executive proof: one bounded Kubernetes Runtime Pool executes repeated production cycles, survives one forced busy-Pod deletion per cycle, reuses the surviving and replacement Pods in the next cycle, and cleans physical capacity only after the final cycle.");
            output.WriteLine(string.Empty);
            output.WriteLine("Scenario contract:");
            output.WriteLine("  - [ON] One control plane and one GenericMcpServerTestHost remain alive for every cycle.");
            output.WriteLine("  - [ON] Cycle N+1 starts from the exact final Pod UIDs and runtime identities produced by cycle N.");
            output.WriteLine("  - [ON] No intermediate cycle invokes Runtime Pool cleanup.");
            output.WriteLine(
                injectChildRuntimeFailure
                    ? waitForExternalPodDeletion
                        ? $"  - [ON] Every cycle kills one exact child runtime after at least {FinalScenarioKillAfterCompletedStepCount} completed steps, preserves its Pod and siblings, then waits for an operator to force-delete one distinct fully busy Pod."
                        : $"  - [ON] Every cycle kills one exact child runtime after at least {FinalScenarioKillAfterCompletedStepCount} completed steps, preserves its Pod and siblings, then force-deletes one distinct fully busy Pod."
                    : "  - [ON] Every cycle force-deletes one fully busy Pod and recovers exactly its assigned work.");
            output.WriteLine(
                $"  - [ON] Every submitted parent DAG completes exactly {parentLogicalStepCount} logical steps; ChildDepth='{childDepth}' composes the nested Child DAG contract before terminal completion.");
            output.WriteLine("  - [ON] Every submitted parent run passes replay, ledger, trace, and exact recovery-forensics proof.");
            output.WriteLine("  - [ON] Deterministic Pod cleanup executes once, after the final cycle.");
            output.WriteLine(string.Empty);
            output.WriteLine("Workload summary:");
            output.WriteLine($"  MaximumConfiguredPodCount='{maximumPodCount}'");
            output.WriteLine($"  RuntimeCountPerPod='{runtimeCountPerPod}'");
            output.WriteLine($"  MaximumRuntimeCapacity='{maximumRuntimeCapacity}'");
            output.WriteLine($"  SubmissionIterationCountPerCycle='{submissionIterationCount}'");
            output.WriteLine($"  ExecutionCycleCount='{executionCycleCount}'");
            output.WriteLine($"  SubmittedRunCountPerCycle='{submittedRunCountPerCycle}'");
            output.WriteLine($"  TotalSubmittedRunCount='{totalSubmittedRunCount}'");
            output.WriteLine($"  LogicalStepCountPerCycle='{logicalStepCountPerCycle}'");
            output.WriteLine($"  TotalLogicalStepCount='{totalLogicalStepCount}'");
            output.WriteLine($"  ChildDepth='{childDepth}'");
            output.WriteLine($"  ParentLogicalStepCount='{parentLogicalStepCount}'");
            output.WriteLine($"  ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"  PoolId='{poolId}'");
            output.WriteLine($"  InjectChildRuntimeFailure='{injectChildRuntimeFailure}'");
            output.WriteLine($"  KillAfterCompletedStepCount='{(injectChildRuntimeFailure ? FinalScenarioKillAfterCompletedStepCount : 0)}'");
            output.WriteLine($"  PodFailureTrigger='{(waitForExternalPodDeletion ? "external-manual" : "automatic")}'");
            output.WriteLine("  CleanupPolicy='after-final-cycle-only'");

            try
            {
                await using var host =
                    new GenericMcpServerTestHost(settings);

                var configuredPodCreationExecutor =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolPodCreationExecutor>();

                Assert.IsType<
                    AiRuntimePoolPodCreationExecutor>(
                        configuredPodCreationExecutor);

                var configuredRuntimePoolOptions =
                    host.Services
                        .GetRequiredService<
                            IOptions<AiKubernetesRuntimePoolOptions>>()
                        .Value;

                Assert.Equal(
                    maximumPodCount,
                    configuredRuntimePoolOptions.MaximumPodCount);

                var physicalPodInventory =
                    host.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodInventory>();

                var initialPhysicalPodCount =
                    await physicalPodInventory
                        .CountRuntimePoolPodsAsync(
                            configuredRuntimePoolOptions.Namespace,
                            configuredRuntimePoolOptions.PoolId)
                        .ConfigureAwait(false);

                Assert.Equal(0, initialPhysicalPodCount);

                var registry =
                    host.Services.GetRequiredService<
                        IAiRuntimeInstanceRegistry>();

                var sharedRunStore =
                    host.Services.GetRequiredService<
                        IAiSharedRunStore>();

                var sharedQueue =
                    host.Services.GetRequiredService<
                        IAiSharedQueue>();

                var scaleOutRequestStore =
                    host.Services.GetRequiredService<
                        IAiRuntimeScaleOutRequestStore>();

                var runExecutionIndex =
                    host.Services.GetRequiredService<
                        IAiRuntimeRunExecutionIndex>();

                var dagStore =
                    host.Services.GetRequiredService<
                        IAiDagExecutionStore>();

                var forensicsQueryService =
                    host.Services.GetRequiredService<
                        IAiRuntimeRecoveryForensicsQueryService>();

                var redisConnection =
                    host.Services.GetRequiredService<
                        IConnectionMultiplexer>();

                using var submissionHttpClient =
                    host.CreateClient();

                submissionHttpClient.Timeout =
                    TimeSpan.FromMinutes(15);

                await WaitForBoundedCapacityScaleOutWatcherReadyAsync(
                        host.Services,
                        controlPlaneId)
                    .ConfigureAwait(false);

                var cycleProofs =
                    new List<BoundedCapacityWarmReuseCycleProof>(
                        executionCycleCount);

                BoundedCapacityWarmReuseCycleProof? previousCycleProof = null;

                for (var cycleNumber = 1;
                     cycleNumber <= executionCycleCount;
                     cycleNumber++)
                {
                    var cycleStopwatch =
                        Stopwatch.StartNew();

                    output.WriteLine(string.Empty);
                    output.WriteLine(
                        $"# WARM REUSE CYCLE {cycleNumber}/{executionCycleCount}");

                    BoundedCapacityPoolMembershipSnapshot? warmStartMembership = null;

                    if (previousCycleProof is not null)
                    {
                        warmStartMembership =
                            await WaitForBoundedCapacityPoolMembershipAsync(
                                    registry,
                                    poolId,
                                    maximumPodCount,
                                    runtimeCountPerPod,
                                    requireAvailableCapacity: true,
                                    TimeSpan.FromMinutes(2))
                                .ConfigureAwait(false);

                        RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                            previousCycleProof.FinalPodUids,
                            warmStartMembership.PodUids,
                            $"Cycle {cycleNumber} warm Pod reuse");

                        RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                            previousCycleProof.FinalRuntimeInstanceIds,
                            warmStartMembership.RuntimeInstanceIds,
                            $"Cycle {cycleNumber} warm runtime reuse");

                        var reusedPhysicalPodCount =
                            await physicalPodInventory
                                .CountRuntimePoolPodsAsync(
                                    configuredRuntimePoolOptions.Namespace,
                                    configuredRuntimePoolOptions.PoolId)
                                .ConfigureAwait(false);

                        Assert.Equal(
                            maximumPodCount,
                            reusedPhysicalPodCount);

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM POOL REUSE] " +
                            $"Cycle='{cycleNumber}', " +
                            $"ReusedPodCount='{warmStartMembership.PodUids.Count}', " +
                            $"ReusedRuntimeCount='{warmStartMembership.RuntimeInstanceIds.Count}', " +
                            "ColdStart='false', CleanupSincePreviousCycle='false'.");
                    }
                    else
                    {
                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM POOL REUSE] " +
                            "Cycle='1', ColdStart='true', InitialPhysicalPodCount='0'.");
                    }

                    var ledgerTimelineFromUtc =
                        DateTimeOffset.UtcNow.AddSeconds(-5);

                    var observation =
                        new BoundedCapacityMachineLimitObservation(
                            maximumPodCount,
                            runtimeCountPerPod,
                            maximumRuntimeCapacity);

                    using var observationCancellation =
                        new CancellationTokenSource();

                    var observationTask =
                        ObserveBoundedCapacityAsync(
                            registry,
                            sharedRunStore,
                            sharedQueue,
                            scaleOutRequestStore,
                            controlPlaneId,
                            poolId,
                            tenant.TenantId,
                            observation,
                            observationCancellation.Token);

                    try
                    {
                        var cycleSubmissionMcp =
                            await McpRbacTestClientHelper
                                .CreateConfiguredClientAsync(
                                    host,
                                    submissionHttpClient,
                                    boundedCapacityProfile.RequestedBy,
                                    tenantId: tenant.TenantId,
                                    tenantGroupId: tenant.TenantGroupId)
                                .ConfigureAwait(false);

                        var deferPodFailureWave =
                            injectChildRuntimeFailure;
                        var initialSubmissionIterationCount =
                            deferPodFailureWave
                                ? submissionIterationCount - 1
                                : submissionIterationCount;

                        var admissionProof =
                            await RuntimePoolProductionCycleExecutor
                                .SubmitQueueFirstWavesAsync(
                                    cycleSubmissionMcp,
                                    tenant,
                                    scenario.Name,
                                    controlPlaneId,
                                    boundedCapacityProfile.RequestedBy,
                                    boundedCapacityProfile.Source,
                                    runsPerIteration,
                                    initialSubmissionIterationCount,
                                    maximumConcurrentMcpSubmissions,
                                    maximumAdmissionAttemptCount,
                                    cycleNumber,
                                    startingIterationNumber: 1)
                                .ConfigureAwait(false);

                        var admissionTooManyRequestsRetryCount =
                            admissionProof.TooManyRequestsRetryCount;

                        var submittedSharedRunIds =
                            admissionProof.SharedRunIds;
                        IReadOnlySet<string> podFailureCandidateSharedRunIds =
                            submittedSharedRunIds;

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE MCP ADMISSION INITIAL] " +
                            $"Cycle='{cycleNumber}', " +
                            $"SubmittedRunCount='{submittedSharedRunIds.Count}', " +
                            $"FullCapacityWaveCount='{initialSubmissionIterationCount}', " +
                            $"ConfiguredFullCapacityWaveCount='{submissionIterationCount}', " +
                            $"DeferredPodFailureWaveCount='{(deferPodFailureWave ? 1 : 0)}', " +
                            $"MaximumConcurrentSubmissions='{maximumConcurrentMcpSubmissions}', " +
                            $"TooManyRequestsRetryCount='{Volatile.Read(ref admissionTooManyRequestsRetryCount)}'.");

                        var preFailureMembership =
                            await WaitForBoundedCapacityPoolMembershipAsync(
                                    registry,
                                    poolId,
                                    maximumPodCount,
                                    runtimeCountPerPod,
                                    requireAvailableCapacity: false,
                                    TimeSpan.FromMinutes(10),
                                    hardTimeout: TimeSpan.FromMinutes(20))
                                .ConfigureAwait(false);

                        if (warmStartMembership is not null)
                        {
                            RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                                warmStartMembership.PodUids,
                                preFailureMembership.PodUids,
                                $"Cycle {cycleNumber} pre-failure Pod reuse");

                            RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                                warmStartMembership.RuntimeInstanceIds,
                                preFailureMembership.RuntimeInstanceIds,
                                $"Cycle {cycleNumber} pre-failure runtime reuse");
                        }

                        BoundedCapacityChildRuntimeFailureTarget?
                            childRuntimeFailureTarget = null;
                        RealRuntimeCrashFailedRuntimeRecoveryProof?
                            childRuntimeRecoveryProof = null;
                        IReadOnlyList<AiRuntimeRecoveryForensicsReadModel>
                            childRuntimeRecoveryForensics =
                                Array.Empty<AiRuntimeRecoveryForensicsReadModel>();
                        var podFailureStartMembership =
                            preFailureMembership;
                        var excludedPodUids =
                            new HashSet<string>(StringComparer.Ordinal);
                        ProductionCrashCheckpointGate?
                            podFailureCrashGate = null;

                        if (injectChildRuntimeFailure)
                        {
                            childRuntimeFailureTarget =
                                await WaitForBoundedCapacityBusyChildRuntimeFailureTargetAsync(
                                        registry,
                                        sharedRunStore,
                                        runExecutionIndex,
                                        submittedSharedRunIds,
                                        controlPlaneId,
                                        poolId,
                                        tenant.TenantId,
                                        runtimeCountPerPod,
                                        maximumRuntimeCapacity,
                                        TimeSpan.FromMinutes(10))
                                    .ConfigureAwait(false);

                            var childInventory =
                                CreateBoundedCapacityChildRuntimeFailureInventory(
                                    tenant,
                                    cycleSubmissionMcp,
                                    childRuntimeFailureTarget);

                            observation.MarkIntentionalFailedRuntimeInstance(
                                childRuntimeFailureTarget.Runtime.RuntimeInstanceId);

                            childRuntimeRecoveryProof =
                                await ProductionRealRuntimeCrashRecoveryTestHelpers
                                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                                        output,
                                        CreateRuntimePoolChildProcessControl(
                                            registry,
                                            poolId,
                                            boundedCapacityProfile.LogPrefix),
                                        registry,
                                        runExecutionIndex,
                                        sharedRunStore,
                                        sharedQueue,
                                        dagStore,
                                        childInventory,
                                        minimumCompletedStepsBeforeKill:
                                            FinalScenarioKillAfterCompletedStepCount,
                                        progressTimeout: TimeSpan.FromMinutes(3),
                                        unsafeTimeout: TimeSpan.FromMinutes(3),
                                        requeueTimeout: TimeSpan.FromMinutes(2),
                                        redispatchTimeout: TimeSpan.FromMinutes(3),
                                        executionResolveTimeout:
                                            TimeSpan.FromMinutes(2),
                                        observationMode:
                                            ProductionRecoveryObservationMode.Polling,
                                        runtimeTenantOwnershipAssertion:
                                            AssertRuntimeBelongsToTenantAsync)
                                    .ConfigureAwait(false);

                            childRuntimeRecoveryForensics =
                                await ProductionRealRuntimeCrashRecoveryTestHelpers
                                    .AssertRecoveredInventoryForensicsAsync(
                                        output,
                                        forensicsQueryService,
                                        childRuntimeRecoveryProof,
                                        TimeSpan.FromMinutes(3))
                                    .ConfigureAwait(false);

                            var childRecoveredWork =
                                Assert.Single(childRuntimeRecoveryProof.RecoveredWorks);
                            var childRecoveryRuntime =
                                await GetRequiredRuntimeSnapshotAsync(
                                        registry,
                                        childRecoveredWork.ReplacementRuntimeInstanceId)
                                    .ConfigureAwait(false);

                            Assert.False(
                                string.IsNullOrWhiteSpace(
                                    childRecoveryRuntime.HostId));
                            excludedPodUids.Add(
                                childRuntimeFailureTarget.Runtime.HostId!);
                            excludedPodUids.Add(
                                childRecoveryRuntime.HostId!);

                            var childReplacementMembership =
                                await WaitForBoundedCapacityPoolMembershipAsync(
                                        registry,
                                        poolId,
                                        maximumPodCount,
                                        runtimeCountPerPod,
                                        requireAvailableCapacity: false,
                                        TimeSpan.FromMinutes(3))
                                    .ConfigureAwait(false);

                            await AssertExactBoundedCapacityChildRuntimeReplacementAsync(
                                    registry,
                                    poolId,
                                    preFailureMembership,
                                    childReplacementMembership,
                                    childRuntimeFailureTarget,
                                    childRuntimeRecoveryProof,
                                    runtimeCountPerPod,
                                    cycleNumber,
                                    boundedCapacityProfile.LogPrefix)
                                .ConfigureAwait(false);

                            podFailureStartMembership =
                                childReplacementMembership;

                            if (deferPodFailureWave)
                            {
                                output.WriteLine(
                                    $"[{boundedCapacityProfile.LogPrefix} HIERARCHICAL FAILURE GATE] " +
                                    $"Cycle='{cycleNumber}', " +
                                    "State='waiting-for-initial-child-failure-workload-drain', " +
                                    $"InitialWaveCount='{initialSubmissionIterationCount}', " +
                                    $"DeferredWaveNumber='{submissionIterationCount}'.");

                                _ = await WaitForSubmittedRunsToCompleteAsync(
                                        sharedRunStore,
                                        runExecutionIndex,
                                        dagStore,
                                        submittedSharedRunIds,
                                        controlPlaneId,
                                        tenant.TenantId,
                                        observation,
                                        scenario.CompletionTimeout,
                                        TimeSpan.FromMinutes(5),
                                        useDagExecutionCompletion: childDepth > 0)
                                    .ConfigureAwait(false);

                                podFailureStartMembership =
                                    await WaitForBoundedCapacityPoolMembershipAsync(
                                            registry,
                                            poolId,
                                            maximumPodCount,
                                            runtimeCountPerPod,
                                            requireAvailableCapacity: true,
                                            TimeSpan.FromMinutes(3))
                                        .ConfigureAwait(false);

                                output.WriteLine(
                                    $"[{boundedCapacityProfile.LogPrefix} HIERARCHICAL FAILURE GATE] " +
                                    $"Cycle='{cycleNumber}', " +
                                    "State='initial-child-failure-workload-drained-capacity-reconverged', " +
                                    $"AvailableRuntimeCount='{podFailureStartMembership.RuntimeInstanceIds.Count}'.");

                                var boundaryFailureFillerRunCount =
                                    checked(
                                        runsPerIteration -
                                        runtimeCountPerPod);
                                var boundaryFailureTargetRunStartNumber =
                                    checked(boundaryFailureFillerRunCount + 1);

                                if (boundaryFailureFillerRunCount > 0)
                                {
                                    var boundaryFailureFillerMcp =
                                        await McpRbacTestClientHelper
                                            .CreateConfiguredClientAsync(
                                                host,
                                                submissionHttpClient,
                                                boundedCapacityProfile.RequestedBy,
                                                tenantId: tenant.TenantId,
                                                tenantGroupId: tenant.TenantGroupId)
                                            .ConfigureAwait(false);

                                    var boundaryFailureFillerAdmission =
                                        await RuntimePoolProductionCycleExecutor
                                            .SubmitQueueFirstWavesAsync(
                                                boundaryFailureFillerMcp,
                                                tenant,
                                                scenario.Name,
                                                controlPlaneId,
                                                boundedCapacityProfile.RequestedBy,
                                                boundedCapacityProfile.Source,
                                                runsPerIteration:
                                                    boundaryFailureFillerRunCount,
                                                submissionIterationCount: 1,
                                                maximumConcurrentSubmissions:
                                                    Math.Min(
                                                        maximumConcurrentMcpSubmissions,
                                                        Math.Clamp(
                                                            boundaryFailureFillerRunCount,
                                                            4,
                                                            16)),
                                                maximumAdmissionAttemptCount:
                                                    maximumAdmissionAttemptCount,
                                                cycleNumber: cycleNumber,
                                                startingIterationNumber:
                                                    submissionIterationCount,
                                                admissionBackpressureTimeout:
                                                    TimeSpan.FromMinutes(
                                                        BoundaryFailureAdmissionBackpressureTimeoutMinutes),
                                                startingRunNumber: 1)
                                            .ConfigureAwait(false);

                                    admissionProof =
                                        RuntimePoolProductionCycleExecutor
                                            .CombineAdmissionProofs(
                                                admissionProof,
                                                boundaryFailureFillerAdmission);
                                    submittedSharedRunIds =
                                        admissionProof.SharedRunIds;
                                    admissionTooManyRequestsRetryCount =
                                        admissionProof.TooManyRequestsRetryCount;

                                    output.WriteLine(
                                        $"[{boundedCapacityProfile.LogPrefix} HIERARCHICAL FAILURE FILLER] " +
                                        $"Cycle='{cycleNumber}', " +
                                        $"WaveNumber='{submissionIterationCount}', " +
                                        $"SubmittedRunCount='{boundaryFailureFillerAdmission.SharedRunIds.Count}', " +
                                        "CrashCheckpoint='none', " +
                                        "Placement='unconstrained', " +
                                        $"TooManyRequestsRetryCount='{boundaryFailureFillerAdmission.TooManyRequestsRetryCount}'.");

                                    _ = await WaitForSubmittedRunsToCompleteAsync(
                                            sharedRunStore,
                                            runExecutionIndex,
                                            dagStore,
                                            boundaryFailureFillerAdmission.SharedRunIds,
                                            controlPlaneId,
                                            tenant.TenantId,
                                            observation,
                                            scenario.CompletionTimeout,
                                            TimeSpan.FromMinutes(5),
                                            useDagExecutionCompletion: childDepth > 0)
                                        .ConfigureAwait(false);
                                }

                                podFailureStartMembership =
                                    await WaitForBoundedCapacityPoolMembershipAsync(
                                            registry,
                                            poolId,
                                            maximumPodCount,
                                            runtimeCountPerPod,
                                            requireAvailableCapacity: true,
                                            TimeSpan.FromMinutes(3))
                                        .ConfigureAwait(false);

                                var boundaryFailureRuntimes =
                                    (await registry
                                            .ListAsync(includeStopped: false)
                                            .ConfigureAwait(false))
                                        .Where(
                                            runtime =>
                                                StringComparer.Ordinal.Equals(
                                                    runtime.PoolId,
                                                    poolId) &&
                                                podFailureStartMembership
                                                    .RuntimeInstanceIds
                                                    .Contains(
                                                        runtime.RuntimeInstanceId) &&
                                                runtime.Status ==
                                                    AiRuntimeInstanceStatus.Ready &&
                                                runtime.CanAcceptRun &&
                                                !string.IsNullOrWhiteSpace(
                                                    runtime.HostId))
                                        .ToArray();

                                Assert.Equal(
                                    runsPerIteration,
                                    boundaryFailureRuntimes.Length);

                                var boundaryFailureTargetPodMembers =
                                    boundaryFailureRuntimes
                                        .GroupBy(
                                            runtime => runtime.HostId!,
                                            StringComparer.Ordinal)
                                        .Where(
                                            pod =>
                                                excludedPodUids.Contains(
                                                    pod.Key) == false &&
                                                pod.Count() == runtimeCountPerPod)
                                        .OrderBy(
                                            pod => pod.Key,
                                            StringComparer.Ordinal)
                                        .Select(
                                            pod => pod
                                                .OrderBy(
                                                    runtime =>
                                                        runtime.RuntimeInstanceId,
                                                    StringComparer.Ordinal)
                                                .ToArray())
                                        .FirstOrDefault();

                                if (boundaryFailureTargetPodMembers is null)
                                {
                                    throw new InvalidOperationException(
                                        "No distinct fully available Pod remained for the deterministic boundary failure wave.");
                                }

                                var boundaryFailureTargetRuntimeInstanceIds =
                                    boundaryFailureTargetPodMembers
                                        .Select(
                                            runtime =>
                                                runtime.RuntimeInstanceId)
                                        .ToArray();

                                Assert.Equal(
                                    runtimeCountPerPod,
                                    boundaryFailureTargetRuntimeInstanceIds.Length);
                                Assert.Equal(
                                    runtimeCountPerPod,
                                    boundaryFailureTargetRuntimeInstanceIds
                                        .Distinct(StringComparer.Ordinal)
                                        .Count());

                                output.WriteLine(
                                    $"[{boundedCapacityProfile.LogPrefix} HIERARCHICAL FAILURE TARGET] " +
                                    $"Cycle='{cycleNumber}', " +
                                    $"TargetPodUid='{boundaryFailureTargetPodMembers[0].HostId}', " +
                                    $"TargetRuntimeCount='{runtimeCountPerPod}', " +
                                    $"CompletedFillerRunCount='{boundaryFailureFillerRunCount}', " +
                                    $"TargetRunStartNumber='{boundaryFailureTargetRunStartNumber}'.");

                                podFailureCrashGate =
                                    await ProductionCrashCheckpointGate
                                        .ArmAsync(
                                            redisConnection,
                                            output,
                                            controlPlaneId,
                                            tenant.TenantId,
                                            $"{scenario.Name}-cycle-{cycleNumber:000}-boundary-wave-{submissionIterationCount:000}",
                                            checkpointStepIndex:
                                                FinalScenarioKillAfterCompletedStepCount + 1,
                                            stateTtl:
                                                TimeSpan.FromMinutes(
                                                    BoundaryFailureCrashCheckpointStateTtlMinutes))
                                        .ConfigureAwait(false);

                                RuntimePoolProductionCycleAdmissionProof
                                    podFailureAdmission;

                                try
                                {
                                    var boundaryFailureTargetMcp =
                                        await McpRbacTestClientHelper
                                            .CreateConfiguredClientAsync(
                                                host,
                                                submissionHttpClient,
                                                boundedCapacityProfile.RequestedBy,
                                                tenantId: tenant.TenantId,
                                                tenantGroupId: tenant.TenantGroupId)
                                            .ConfigureAwait(false);

                                    podFailureAdmission =
                                        await RuntimePoolProductionCycleExecutor
                                            .SubmitQueueFirstWavesAsync(
                                                boundaryFailureTargetMcp,
                                                tenant,
                                                scenario.Name,
                                                controlPlaneId,
                                                boundedCapacityProfile.RequestedBy,
                                                boundedCapacityProfile.Source,
                                                runsPerIteration:
                                                    runtimeCountPerPod,
                                                submissionIterationCount: 1,
                                                maximumConcurrentSubmissions: 1,
                                                maximumAdmissionAttemptCount:
                                                    maximumAdmissionAttemptCount,
                                                cycleNumber: cycleNumber,
                                                startingIterationNumber:
                                                    submissionIterationCount,
                                                crashCheckpoint:
                                                    podFailureCrashGate.Definition,
                                                admissionBackpressureTimeout:
                                                    TimeSpan.FromMinutes(
                                                        BoundaryFailureAdmissionBackpressureTimeoutMinutes),
                                                placementFactory:
                                                    (_, runNumber) =>
                                                        new AiRunPlacementDirective
                                                        {
                                                            Target =
                                                                new AiRunPlacementTarget
                                                                {
                                                                    RuntimeInstanceId =
                                                                        boundaryFailureTargetRuntimeInstanceIds[
                                                                            runNumber -
                                                                            boundaryFailureTargetRunStartNumber]
                                                                },
                                                            Requirement =
                                                                AiRunPlacementRequirement.Required,
                                                            Fallback =
                                                                AiRunPlacementFallback.Reject
                                                        },
                                                startingRunNumber:
                                                    boundaryFailureTargetRunStartNumber)
                                            .ConfigureAwait(false);

                                    await podFailureCrashGate
                                        .WaitUntilReachedAsync(
                                            TimeSpan.FromMinutes(3))
                                        .ConfigureAwait(false);
                                }
                                catch
                                {
                                    await podFailureCrashGate
                                        .ReleaseAsync()
                                        .ConfigureAwait(false);
                                    podFailureCrashGate = null;
                                    throw;
                                }

                                var podFailureTargetAdmissionResults =
                                    podFailureAdmission
                                        .Results
                                        .ToArray();

                                Assert.Equal(
                                    runtimeCountPerPod,
                                    podFailureTargetAdmissionResults.Length);
                                Assert.All(
                                    podFailureTargetAdmissionResults,
                                    result => Assert.False(
                                        string.IsNullOrWhiteSpace(
                                            result.SharedRunId)));

                                podFailureCandidateSharedRunIds =
                                    podFailureTargetAdmissionResults
                                        .Select(result => result.SharedRunId!)
                                        .ToHashSet(StringComparer.Ordinal);

                                Assert.Equal(
                                    runtimeCountPerPod,
                                    podFailureCandidateSharedRunIds.Count);

                                admissionProof =
                                    RuntimePoolProductionCycleExecutor
                                        .CombineAdmissionProofs(
                                            admissionProof,
                                            podFailureAdmission);
                                submittedSharedRunIds =
                                    admissionProof.SharedRunIds;
                                admissionTooManyRequestsRetryCount =
                                    admissionProof.TooManyRequestsRetryCount;

                                output.WriteLine(
                                    $"[{boundedCapacityProfile.LogPrefix} WARM POD FAILURE WAVE] " +
                                    $"Cycle='{cycleNumber}', " +
                                    $"WaveNumber='{submissionIterationCount}', " +
                                    $"SubmittedRunCount='{boundaryFailureFillerRunCount + podFailureAdmission.SharedRunIds.Count}', " +
                                    $"CompletedFillerRunCount='{boundaryFailureFillerRunCount}', " +
                                    $"TargetCheckpointRunCount='{podFailureAdmission.SharedRunIds.Count}', " +
                                    $"ReusedRuntimeCapacity='{maximumRuntimeCapacity}', " +
                                    $"EligibleDistinctPodCount='{maximumPodCount - excludedPodUids.Count}', " +
                                    $"TooManyRequestsRetryCount='{podFailureAdmission.TooManyRequestsRetryCount}'.");
                            }
                        }

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            submittedSharedRunIds.Count);

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE MCP ADMISSION CONSOLIDATED] " +
                            $"Cycle='{cycleNumber}', " +
                            $"SubmittedRunCount='{submittedSharedRunIds.Count}', " +
                            $"FullCapacityWaveCount='{submissionIterationCount}', " +
                            $"RunsPerWave='{runsPerIteration}', " +
                            $"PodFailureCandidateRunCount='{podFailureCandidateSharedRunIds.Count}', " +
                            $"TooManyRequestsRetryCount='{Volatile.Read(ref admissionTooManyRequestsRetryCount)}'.");

                        Assert.True(
                            excludedPodUids.Count < maximumPodCount,
                            "The final KubernetesPool proof excluded every Pod boundary before the distinct Pod failure could be selected.");

                        BoundedCapacityPodFailureProof podFailureProof;

                        try
                        {
                            podFailureProof =
                                await InjectBoundedCapacityPodFailureAsync(
                                        host.Services,
                                        registry,
                                        sharedRunStore,
                                        runExecutionIndex,
                                        podFailureCandidateSharedRunIds,
                                        tenant,
                                        controlPlaneId,
                                        poolId,
                                        runtimeCountPerPod,
                                        maximumRuntimeCapacity,
                                        observation,
                                        TimeSpan.FromMinutes(10),
                                        excludedPodUids:
                                            excludedPodUids,
                                        boundaryFailureCrashGate:
                                            podFailureCrashGate,
                                        waitForExternalPodDeletion:
                                            waitForExternalPodDeletion,
                                        externalFailureCycleNumber:
                                            waitForExternalPodDeletion
                                                ? cycleNumber
                                                : null,
                                        useDagExecutionCompletion:
                                            childDepth > 0)
                                    .ConfigureAwait(false);
                        }
                        finally
                        {
                            if (podFailureCrashGate is not null)
                            {
                                // Idempotent safety release also covers target-selection
                                // or recovery failures before the exact Pod termination.
                                await podFailureCrashGate
                                    .ReleaseAsync()
                                    .ConfigureAwait(false);
                            }
                        }

                        Assert.Empty(
                            (childRuntimeRecoveryProof?.RecoveredWorks
                                 .Select(work => work.Original.SharedRunId) ??
                             Array.Empty<string>())
                                .Intersect(
                                    podFailureProof.RecoveredSharedRunIds,
                                    StringComparer.Ordinal));

                        Assert.Contains(
                            podFailureProof.FailedPodUid,
                            podFailureStartMembership.PodUids);

                        Assert.DoesNotContain(
                            podFailureProof.ReplacementPodUid,
                            podFailureStartMembership.PodUids);

                        var finalRuns =
                            await WaitForSubmittedRunsToCompleteAsync(
                                    sharedRunStore,
                                    runExecutionIndex,
                                    dagStore,
                                    submittedSharedRunIds,
                                    controlPlaneId,
                                    tenant.TenantId,
                                    observation,
                                    scenario.CompletionTimeout,
                                    TimeSpan.FromMinutes(5),
                                    useDagExecutionCompletion: childDepth > 0)
                                .ConfigureAwait(false);

                        await Task.WhenAll(
                                finalRuns.Select(
                                    run =>
                                        ProductionRecoveryWaitHelpers
                                            .WaitForDagCompletedStepCountAsync(
                                                dagStore,
                                                run.ExecutionId,
                                                parentLogicalStepCount,
                                                TimeSpan.FromMinutes(2))))
                            .ConfigureAwait(false);

                        using var runtimeStatusProofHttpClient =
                            host.CreateClient();

                        runtimeStatusProofHttpClient.Timeout =
                            TimeSpan.FromMinutes(15);

                        var runtimeStatusProofMcp =
                            await McpRbacTestClientHelper
                                .CreateConfiguredClientAsync(
                                    host,
                                    runtimeStatusProofHttpClient,
                                    boundedCapacityProfile.RequestedBy,
                                    tenantId: tenant.TenantId,
                                    tenantGroupId: tenant.TenantGroupId)
                                .ConfigureAwait(false);

                        IReadOnlyList<AiRuntimeQueueControlPlaneResult>
                            finalRuntimeStatuses;

                        if (childDepth == 0)
                        {
                            finalRuntimeStatuses =
                                await McpTestWaitHelpers
                                    .WaitForTerminalRuntimeRunStatusesAsync(
                                        runtimeStatusProofMcp,
                                        finalRuns
                                            .Select(run => run.SharedRun)
                                            .ToArray(),
                                        timeout: scenario.CompletionTimeout)
                                    .ConfigureAwait(false);

                            Assert.Equal(
                                submittedRunCountPerCycle,
                                finalRuntimeStatuses.Count);

                            Assert.All(
                                finalRuntimeStatuses,
                                finalRuntimeStatus =>
                                {
                                    Assert.True(
                                        finalRuntimeStatus.Success,
                                        finalRuntimeStatus.FailureReason ??
                                        finalRuntimeStatus.Message);

                                    Assert.True(
                                        string.Equals(
                                            finalRuntimeStatus.RunState?.Status,
                                            "completed",
                                            StringComparison.OrdinalIgnoreCase),
                                        $"Warm reuse runtime work did not complete. Cycle='{cycleNumber}', RuntimeInstanceId='{finalRuntimeStatus.RuntimeInstanceId}', RunId='{finalRuntimeStatus.RunId}', ExecutionId='{finalRuntimeStatus.ExecutionId ?? finalRuntimeStatus.RunState?.ExecutionId}', Status='{finalRuntimeStatus.RunState?.Status}'.");
                                });
                        }
                        else
                        {
                            finalRuntimeStatuses =
                                await ProductionChildDagScenarioHelpers
                                    .WaitForDurableParentCompletionAsync(
                                        runtimeStatusProofMcp,
                                        dagStore,
                                        finalRuns
                                            .Select(run => run.SharedRun)
                                            .ToArray(),
                                        scenario.CompletionTimeout)
                                    .ConfigureAwait(false);

                            Assert.Equal(
                                submittedRunCountPerCycle,
                                finalRuntimeStatuses.Count);

                            output.WriteLine(
                                $"[{boundedCapacityProfile.LogPrefix} CHILD DAG TERMINAL PROOF] " +
                                $"Cycle='{cycleNumber}', " +
                                $"ChildDepth='{childDepth}', " +
                                $"CompletedExecutionCount='{finalRuns.Count}', " +
                                "Proof='authoritative-dag-execution-record', " +
                                "RootLocalRunContract='waiting-physical-attempt-released-capacity'.");
                        }

                        observationCancellation.Cancel();

                        await observationTask
                            .ConfigureAwait(false);

                        observation.ThrowIfViolated();

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            finalRuns
                                .Select(run => run.LocalRunId)
                                .Distinct(StringComparer.Ordinal)
                                .Count());

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            finalRuns
                                .Select(run => run.ExecutionId)
                                .Distinct(StringComparer.Ordinal)
                                .Count());

                        var childRecoveryForensicsIds =
                            childRuntimeRecoveryForensics
                                .Select(record => record.ForensicsId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
                                .ToHashSet(StringComparer.Ordinal);
                        var cycleRecoveryForensicsIds =
                            childRecoveryForensicsIds
                                .Concat(podFailureProof.RecoveryForensicsIds)
                                .ToHashSet(StringComparer.Ordinal);

                        foreach (var forensicsId in
                                 podFailureProof.RecoveryForensicsIds
                                     .OrderBy(value => value, StringComparer.Ordinal))
                        {
                            var record =
                                await forensicsQueryService
                                    .GetByForensicsIdAsync(forensicsId)
                                    .ConfigureAwait(false);

                            Assert.NotNull(record);

                            var exactRecord = record!;

                            Assert.Equal(forensicsId, exactRecord.ForensicsId);
                            Assert.Equal(
                                podFailureProof.FailureId,
                                exactRecord.RuntimeFailureIncidentId);
                            Assert.Contains(
                                exactRecord.SharedRunId!,
                                podFailureProof.RecoveredSharedRunIds);
                            Assert.Contains(
                                exactRecord.ExecutionId,
                                podFailureProof.ImpactedExecutionIds);
                        }

                        observation.ObserveFinalDispatchBindings(
                            finalRuns.Select(run => run.SharedRun));

                        var duplicateDispatchBindings =
                            observation.GetDuplicateDispatchBindings(
                                submittedSharedRunIds);

                        var childRecoveredSharedRunIds =
                            childRuntimeRecoveryProof?.RecoveredWorks
                                .Select(work => work.Original.SharedRunId)
                                .ToHashSet(StringComparer.Ordinal) ??
                            new HashSet<string>(StringComparer.Ordinal);
                        var recoveredSharedRunIds =
                            childRecoveredSharedRunIds
                                .Concat(podFailureProof.RecoveredSharedRunIds)
                                .ToHashSet(StringComparer.Ordinal);
                        var childRecoveredExecutionIds =
                            childRuntimeRecoveryProof is null
                                ? new HashSet<string>(StringComparer.Ordinal)
                                : childRuntimeRecoveryProof.RecoveredWorks
                                    .Select(
                                        work =>
                                            work.RecoveredExecutionId ??
                                            work.Original.ExecutionId)
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Cast<string>()
                                    .ToHashSet(StringComparer.Ordinal);
                        var recoveredExecutionIds =
                            childRecoveredExecutionIds
                                .Concat(podFailureProof.ImpactedExecutionIds)
                                .ToHashSet(StringComparer.Ordinal);

                        var unexpectedDuplicateDispatchBindings =
                            duplicateDispatchBindings
                                .Where(
                                    item =>
                                        item.Value.Count > 1 &&
                                        (!recoveredSharedRunIds.Contains(item.Key) ||
                                         item.Value.Count > 2))
                                .ToDictionary(
                                    item => item.Key,
                                    item => item.Value,
                                    StringComparer.Ordinal);

                        var duplicateDispatchCount =
                            unexpectedDuplicateDispatchBindings.Sum(
                                item => Math.Max(0, item.Value.Count - 1));

                        Assert.Equal(0, duplicateDispatchCount);
                        Assert.Equal(
                            recoveredSharedRunIds.Count,
                            duplicateDispatchBindings.Count(
                                item =>
                                    recoveredSharedRunIds.Contains(item.Key) &&
                                    item.Value.Count == 2));

                        var finalMembership =
                            await WaitForBoundedCapacityPoolMembershipAsync(
                                    registry,
                                    poolId,
                                    maximumPodCount,
                                    runtimeCountPerPod,
                                    requireAvailableCapacity: true,
                                    TimeSpan.FromMinutes(2))
                                .ConfigureAwait(false);

                        var expectedFinalPodUids =
                            podFailureStartMembership.PodUids
                                .Where(
                                    podUid => !StringComparer.Ordinal.Equals(
                                        podUid,
                                        podFailureProof.FailedPodUid))
                                .Append(podFailureProof.ReplacementPodUid)
                                .ToHashSet(StringComparer.Ordinal);

                        RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                            expectedFinalPodUids,
                            finalMembership.PodUids,
                            $"Cycle {cycleNumber} exact replacement Pod topology");

                        var expectedFinalRuntimeInstanceIds =
                            podFailureStartMembership.RuntimeInstanceIds
                                .Where(
                                    runtimeInstanceId =>
                                        !podFailureProof.FailedRuntimeInstanceIds.Contains(
                                            runtimeInstanceId))
                                .Concat(
                                    podFailureProof.ReplacementRuntimeInstanceIds)
                                .ToHashSet(StringComparer.Ordinal);

                        RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                            expectedFinalRuntimeInstanceIds,
                            finalMembership.RuntimeInstanceIds,
                            $"Cycle {cycleNumber} exact replacement runtime topology");

                        var finalQueueItems =
                            (await sharedQueue
                                    .ListAsync(includeTerminal: false)
                                    .ConfigureAwait(false))
                                .Where(
                                    item => StringComparer.Ordinal.Equals(
                                        item.ControlPlaneId,
                                        controlPlaneId))
                                .ToArray();

                        Assert.Empty(finalQueueItems);

                        using var forensicsProofHttpClient =
                            host.CreateClient();

                        forensicsProofHttpClient.Timeout =
                            TimeSpan.FromMinutes(2);

                        var forensicsProofMcp =
                            await McpRbacTestClientHelper
                                .CreateConfiguredClientAsync(
                                    host,
                                    forensicsProofHttpClient,
                                    boundedCapacityProfile.RequestedBy,
                                    tenantId: tenant.TenantId,
                                    tenantGroupId: tenant.TenantGroupId)
                                .ConfigureAwait(false);

                        foreach (var forensicsId in
                                 cycleRecoveryForensicsIds
                                     .OrderBy(value => value, StringComparer.Ordinal))
                        {
                            var exactResult =
                                await SearchRuntimeRecoveryForensicsWithBackpressureAsync(
                                        forensicsProofMcp,
                                        new AiRuntimeRecoveryForensicsQuery
                                        {
                                            ForensicsId = forensicsId,
                                            Limit = 1
                                        },
                                        maximumAttemptCount: 8)
                                    .ConfigureAwait(false);

                            var exactRecord =
                                Assert.Single(exactResult.Items);

                            Assert.Equal(forensicsId, exactRecord.ForensicsId);

                            if (podFailureProof.RecoveryForensicsIds.Contains(forensicsId))
                            {
                                Assert.Equal(
                                    podFailureProof.FailureId,
                                    exactRecord.RuntimeFailureIncidentId);
                            }
                        }

                        var scaleOutRequests =
                            observation.GetScaleOutRequests(
                                submittedSharedRunIds);

                        if (cycleNumber == 1)
                        {
                            Assert.True(
                                scaleOutRequests.Count > 0,
                                "The cold-start cycle must capture the scale-out requests that created the initial bounded Runtime Pool capacity.");
                        }

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE SCALE-OUT OBSERVATION] " +
                            $"Cycle='{cycleNumber}', " +
                            $"ScaleOutRequestCount='{scaleOutRequests.Count}', " +
                            $"ColdStart='{(cycleNumber == 1).ToString().ToLowerInvariant()}'.");

                        using var replayProofHttpClient =
                            host.CreateClient();

                        replayProofHttpClient.Timeout =
                            TimeSpan.FromMinutes(15);

                        var replayProofMcp =
                            await McpRbacTestClientHelper
                                .CreateConfiguredClientAsync(
                                    host,
                                    replayProofHttpClient,
                                    boundedCapacityProfile.RequestedBy,
                                    tenantId: tenant.TenantId,
                                    tenantGroupId: tenant.TenantGroupId)
                                .ConfigureAwait(false);

                        var phase5TooManyRequestsRetryCount = 0;

                        void ObservePhase5BackpressureRetry(
                            string operationName,
                            int attempt,
                            TimeSpan retryDelay)
                        {
                            _ = operationName;
                            _ = attempt;
                            _ = retryDelay;

                            Interlocked.Increment(
                                ref phase5TooManyRequestsRetryCount);
                        }

                        var replayProofs =
                            await RecoveredExecutionReplayProofAssertions
                                .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                                    replayProofMcp,
                                    tenant.TenantId,
                                    finalRuntimeStatuses,
                                    boundedCapacityProfile.RequestedBy,
                                    boundedCapacityProfile.Source,
                                    ObservePhase5BackpressureRetry)
                                .ConfigureAwait(false);

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            replayProofs.Count);

                        var ledgerTimelineToUtc =
                            DateTimeOffset.UtcNow.AddSeconds(5);

                        var executionLedgerEntries =
                            new List<AiDecisionLedgerEntry>();

                        foreach (var executionIdBatch in replayProofs
                                     .Select(proof => proof.ExecutionId)
                                     .Distinct(StringComparer.Ordinal)
                                     .Chunk(8))
                        {
                            var currentBatch =
                                await Task.WhenAll(
                                        executionIdBatch.Select(
                                            executionId =>
                                                McpBackpressureRetryHelper
                                                    .ExecuteAsync(
                                                        () => replayProofMcp.QueryLedgerAsync(
                                                            new AiDecisionLedgerQuery
                                                            {
                                                                ExecutionId = executionId,
                                                                TimestampFromUtc = ledgerTimelineFromUtc,
                                                                TimestampToUtc = ledgerTimelineToUtc
                                                            }),
                                                        $"observability.ledger.warm-reuse.execution:{cycleNumber}:{executionId}",
                                                        onRetry: ObservePhase5BackpressureRetry)))
                                    .ConfigureAwait(false);

                            Assert.All(
                                currentBatch,
                                entries => Assert.NotEmpty(entries));

                            executionLedgerEntries.AddRange(
                                currentBatch.SelectMany(entries => entries));
                        }

                        var controlPlaneLedgerEntries =
                            new List<AiDecisionLedgerEntry>();

                        foreach (var sharedRunIdBatch in submittedSharedRunIds
                                     .OrderBy(value => value, StringComparer.Ordinal)
                                     .Chunk(8))
                        {
                            var currentBatch =
                                await Task.WhenAll(
                                        sharedRunIdBatch.Select(
                                            sharedRunId =>
                                                McpBackpressureRetryHelper
                                                    .ExecuteAsync(
                                                        () => replayProofMcp.QueryLedgerAsync(
                                                            new AiDecisionLedgerQuery
                                                            {
                                                                ExecutionId =
                                                                    $"control-plane-run:{sharedRunId}",
                                                                TimestampFromUtc = ledgerTimelineFromUtc,
                                                                TimestampToUtc = ledgerTimelineToUtc
                                                            }),
                                                        $"observability.ledger.warm-reuse.control-plane-run:{cycleNumber}:{sharedRunId}",
                                                        onRetry: ObservePhase5BackpressureRetry)))
                                    .ConfigureAwait(false);

                            Assert.All(
                                currentBatch,
                                entries => Assert.NotEmpty(entries));

                            controlPlaneLedgerEntries.AddRange(
                                currentBatch.SelectMany(entries => entries));
                        }

                        var runtimeLifecycleLedgerEntries =
                            new List<AiDecisionLedgerEntry>();

                        var childReplacementRuntimeInstanceIds =
                            podFailureStartMembership.RuntimeInstanceIds
                                .Except(
                                    preFailureMembership.RuntimeInstanceIds,
                                    StringComparer.Ordinal);
                        var assignedRuntimeInstanceIds =
                            finalRuns
                                .Select(run => run.SharedRun.AssignedRuntimeInstanceId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
                                .Concat(
                                    childRuntimeFailureTarget is null
                                        ? Array.Empty<string>()
                                        : new[]
                                        {
                                            childRuntimeFailureTarget.Runtime.RuntimeInstanceId
                                        })
                                .Concat(childReplacementRuntimeInstanceIds)
                                .Concat(podFailureProof.FailedRuntimeInstanceIds)
                                .Concat(podFailureProof.ReplacementRuntimeInstanceIds)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToArray();

                        foreach (var runtimeInstanceIdBatch in
                                 assignedRuntimeInstanceIds.Chunk(8))
                        {
                            var currentBatch =
                                await Task.WhenAll(
                                        runtimeInstanceIdBatch.Select(
                                            runtimeInstanceId =>
                                                McpBackpressureRetryHelper
                                                    .ExecuteAsync(
                                                        () => replayProofMcp.QueryLedgerAsync(
                                                            new AiDecisionLedgerQuery
                                                            {
                                                                ExecutionId =
                                                                    $"control-plane-runtime-instance:{runtimeInstanceId}",
                                                                TimestampFromUtc = ledgerTimelineFromUtc,
                                                                TimestampToUtc = ledgerTimelineToUtc
                                                            }),
                                                        $"observability.ledger.warm-reuse.runtime-instance:{cycleNumber}:{runtimeInstanceId}",
                                                        onRetry: ObservePhase5BackpressureRetry)))
                                    .ConfigureAwait(false);

                            runtimeLifecycleLedgerEntries.AddRange(
                                currentBatch.SelectMany(entries => entries));
                        }

                        var combinedLedgerEntries =
                            executionLedgerEntries
                                .Concat(controlPlaneLedgerEntries)
                                .Concat(runtimeLifecycleLedgerEntries)
                                .DistinctBy(entry => entry.EntryId)
                                .OrderBy(entry => entry.TimestampUtc)
                                .ThenBy(entry => entry.Sequence)
                                .ToArray();

                        var stepCompletedLedgerCount =
                            executionLedgerEntries.Count(
                                entry => string.Equals(
                                    entry.EventType,
                                    AiEngineEvents.Step.Completed,
                                    StringComparison.OrdinalIgnoreCase));

                        var stepCompletionLedgerProof =
                            RuntimePoolProductionCycleExecutor
                                .AssertLogicalStepCompletionEvidence(
                                    executionLedgerEntries,
                                    replayProofs
                                        .Select(proof => proof.ExecutionId)
                                        .ToHashSet(StringComparer.Ordinal),
                                    recoveredExecutionIds,
                                    parentLogicalStepCount,
                                    $"Warm reuse cycle {cycleNumber} logical step completion ledger proof");

                        var dispatchLedgerProof =
                            RuntimePoolProductionCycleExecutor
                                .AssertDurableDispatchEvidence(
                                    submittedSharedRunIds,
                                    recoveredSharedRunIds,
                                    controlPlaneLedgerEntries,
                                    $"Warm reuse cycle {cycleNumber} durable dispatch ledger proof");

                        var dispatchedSharedRunCount =
                            dispatchLedgerProof
                                .DurableDispatchProvenSharedRunIds
                                .Count;

                        Assert.Equal(
                            stepCompletedLedgerCount,
                            stepCompletionLedgerProof.RawStepCompletedEntryCount);

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE STEP LEDGER PROOF] " +
                            $"Cycle='{cycleNumber}', " +
                            $"ExpectedLogicalStepCount='{logicalStepCountPerCycle}', " +
                            $"DistinctLogicalStepCompletedCount='{stepCompletionLedgerProof.DistinctLogicalStepCompletedCount}', " +
                            $"RawStepCompletedEntryCount='{stepCompletionLedgerProof.RawStepCompletedEntryCount}', " +
                            $"RecoveryCoveredDuplicateEntryCount='{stepCompletionLedgerProof.DuplicateStepCompletedEntryCount}', " +
                            $"DuplicateEvidenceExecutionIds='{string.Join(",", stepCompletionLedgerProof.DuplicateEvidenceExecutionIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE DISPATCH LEDGER PROOF] " +
                            $"Cycle='{cycleNumber}', " +
                            $"SubmittedRunCount='{submittedRunCountPerCycle}', " +
                            $"InitialDispatchSucceededCount='{dispatchLedgerProof.InitialDispatchSucceededSharedRunIds.Count}', " +
                            $"RecoveryCoveredMissingInitialDispatchCount='{dispatchLedgerProof.RecoveryCoveredSharedRunIds.Count}', " +
                            $"DurableDispatchProvenCount='{dispatchedSharedRunCount}', " +
                            $"RecoveryCoveredSharedRunIds='{string.Join(",", dispatchLedgerProof.RecoveryCoveredSharedRunIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                        if (cycleNumber == 1)
                        {
                            Assert.Contains(
                                controlPlaneLedgerEntries,
                                entry => entry.EventType.Contains(
                                    "runtime-scale-out-request-watch.succeeded",
                                    StringComparison.OrdinalIgnoreCase));
                        }

                        Assert.Contains(
                            combinedLedgerEntries,
                            entry => entry.EventType.StartsWith(
                                "control.recovery.",
                                StringComparison.OrdinalIgnoreCase));

                        ProductionTenantLedgerSummaryOutput.Write(
                            output,
                            $"WARM REUSE CYCLE {cycleNumber} TENANT LEDGER SUMMARY",
                            new[]
                            {
                                new ProductionTenantLedgerSummary(
                                    tenant.TenantId,
                                    assignedRuntimeInstanceIds,
                                    replayProofs
                                        .Select(proof => proof.ExecutionId)
                                        .Distinct(StringComparer.Ordinal)
                                        .ToArray(),
                                    combinedLedgerEntries)
                            },
                            maxLedgerEntriesPerTenant: 30,
                            maxEventTypeRowsPerTenant: 20,
                            maxLedgerEntriesPerExecution: 15);

                        Assert.Equal(
                            recoveredSharedRunIds.Count,
                            cycleRecoveryForensicsIds.Count);

                        cycleStopwatch.Stop();

                        var cycleProof =
                            new BoundedCapacityWarmReuseCycleProof(
                                cycleNumber,
                                warmStartMembership?.PodUids ??
                                    new HashSet<string>(StringComparer.Ordinal),
                                warmStartMembership?.RuntimeInstanceIds ??
                                    new HashSet<string>(StringComparer.Ordinal),
                                preFailureMembership.PodUids,
                                preFailureMembership.RuntimeInstanceIds,
                                finalMembership.PodUids,
                                finalMembership.RuntimeInstanceIds,
                                submittedSharedRunIds,
                                finalRuns
                                    .Select(run => run.ExecutionId)
                                    .ToHashSet(StringComparer.Ordinal),
                                cycleRecoveryForensicsIds,
                                podFailureProof.FailureId,
                                podFailureProof.FailedPodUid,
                                podFailureProof.ReplacementPodUid,
                                replayProofs.Count,
                                stepCompletedLedgerCount,
                                Volatile.Read(ref phase5TooManyRequestsRetryCount),
                                cycleStopwatch.Elapsed);

                        cycleProofs.Add(cycleProof);
                        previousCycleProof = cycleProof;

                        output.WriteLine(string.Empty);
                        output.WriteLine(
                            $"# WARM REUSE CYCLE {cycleNumber} PROOF");
                        output.WriteLine($"CycleNumber='{cycleNumber}'");
                        output.WriteLine($"SubmittedRunCount='{submittedSharedRunIds.Count}'");
                        output.WriteLine($"CompletedRunCount='{finalRuns.Count}'");
                        output.WriteLine($"LogicalStepCount='{logicalStepCountPerCycle}'");
                        output.WriteLine($"PreFailurePodCount='{preFailureMembership.PodUids.Count}'");
                        output.WriteLine($"FinalPodCount='{finalMembership.PodUids.Count}'");
                        output.WriteLine($"FinalRuntimeCount='{finalMembership.RuntimeInstanceIds.Count}'");
                        output.WriteLine($"ChildRuntimeFailureInjected='{injectChildRuntimeFailure}'");
                        output.WriteLine($"PodFailureTrigger='{(waitForExternalPodDeletion ? "external-manual" : "automatic")}'");
                        output.WriteLine($"FailedChildRuntimeInstanceId='{childRuntimeFailureTarget?.Runtime.RuntimeInstanceId ?? string.Empty}'");
                        output.WriteLine($"ChildRuntimeParentPodUid='{childRuntimeFailureTarget?.Runtime.HostId ?? string.Empty}'");
                        output.WriteLine($"ChildRuntimeRecoveredSharedRunCount='{childRecoveredSharedRunIds.Count}'");
                        output.WriteLine($"FailedPodUid='{podFailureProof.FailedPodUid}'");
                        output.WriteLine($"ReplacementPodUid='{podFailureProof.ReplacementPodUid}'");
                        output.WriteLine($"PodRecoveredSharedRunCount='{podFailureProof.RecoveredSharedRunIds.Count}'");
                        output.WriteLine($"RecoveredSharedRunCount='{recoveredSharedRunIds.Count}'");
                        output.WriteLine($"ReplayProofCount='{replayProofs.Count}'");
                        output.WriteLine($"RawStepCompletedLedgerEntryCount='{stepCompletionLedgerProof.RawStepCompletedEntryCount}'");
                        output.WriteLine($"DistinctLogicalStepCompletedLedgerCount='{stepCompletionLedgerProof.DistinctLogicalStepCompletedCount}'");
                        output.WriteLine($"RecoveryCoveredDuplicateStepCompletedLedgerEntryCount='{stepCompletionLedgerProof.DuplicateStepCompletedEntryCount}'");
                        output.WriteLine($"Phase5TooManyRequestsRetryCount='{Volatile.Read(ref phase5TooManyRequestsRetryCount)}'");
                        output.WriteLine($"CycleDuration='{cycleStopwatch.Elapsed}'");
                        output.WriteLine("CleanupExecuted='false'");
                    }
                    finally
                    {
                        observationCancellation.Cancel();

                        await observationTask
                            .ConfigureAwait(false);
                    }
                }

                Assert.Equal(
                    executionCycleCount,
                    cycleProofs.Count);

                for (var cycleIndex = 1;
                     cycleIndex < cycleProofs.Count;
                     cycleIndex++)
                {
                    var previous = cycleProofs[cycleIndex - 1];
                    var current = cycleProofs[cycleIndex];

                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        previous.FinalPodUids,
                        current.WarmStartPodUids,
                        $"Cycle {previous.CycleNumber} to {current.CycleNumber} Pod reuse");

                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        previous.FinalRuntimeInstanceIds,
                        current.WarmStartRuntimeInstanceIds,
                        $"Cycle {previous.CycleNumber} to {current.CycleNumber} runtime reuse");
                }

                var allSubmittedSharedRunIds =
                    cycleProofs
                        .SelectMany(cycle => cycle.SubmittedSharedRunIds)
                        .ToArray();

                var allExecutionIds =
                    cycleProofs
                        .SelectMany(cycle => cycle.ExecutionIds)
                        .ToArray();

                var allRecoveryForensicsIds =
                    cycleProofs
                        .SelectMany(cycle => cycle.RecoveryForensicsIds)
                        .ToArray();

                Assert.Equal(
                    totalSubmittedRunCount,
                    allSubmittedSharedRunIds.Length);

                Assert.Equal(
                    totalSubmittedRunCount,
                    allSubmittedSharedRunIds
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                Assert.Equal(
                    totalSubmittedRunCount,
                    allExecutionIds.Length);

                Assert.Equal(
                    totalSubmittedRunCount,
                    allExecutionIds
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                var expectedRecoveredRunCountPerCycle =
                    runtimeCountPerPod +
                    (injectChildRuntimeFailure ? 1 : 0);

                Assert.Equal(
                    checked(
                        expectedRecoveredRunCountPerCycle *
                        executionCycleCount),
                    allRecoveryForensicsIds.Length);

                Assert.Equal(
                    allRecoveryForensicsIds.Length,
                    allRecoveryForensicsIds
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                Assert.Equal(
                    executionCycleCount,
                    cycleProofs
                        .Select(cycle => cycle.FailureId)
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                var finalPhysicalPodCount =
                    await physicalPodInventory
                        .CountRuntimePoolPodsAsync(
                            configuredRuntimePoolOptions.Namespace,
                            configuredRuntimePoolOptions.PoolId)
                        .ConfigureAwait(false);

                Assert.Equal(
                    maximumPodCount,
                    finalPhysicalPodCount);

                totalStopwatch.Stop();

                output.WriteLine(string.Empty);
                output.WriteLine(
                    $"# {boundedCapacityProfile.LogPrefix} WARM REUSE PRODUCTION SUMMARY");
                output.WriteLine($"ExecutionCycleCount='{executionCycleCount}'");
                output.WriteLine($"MaximumConfiguredPodCount='{maximumPodCount}'");
                output.WriteLine($"RuntimeCountPerPod='{runtimeCountPerPod}'");
                output.WriteLine($"MaximumRuntimeCapacity='{maximumRuntimeCapacity}'");
                output.WriteLine($"TotalSubmittedRunCount='{totalSubmittedRunCount}'");
                output.WriteLine($"TotalCompletedRunCount='{allExecutionIds.Length}'");
                output.WriteLine($"TotalLogicalStepCount='{totalLogicalStepCount}'");
                output.WriteLine($"ChildRuntimeFailureInjected='{injectChildRuntimeFailure}'");
                output.WriteLine($"KillAfterCompletedStepCount='{(injectChildRuntimeFailure ? FinalScenarioKillAfterCompletedStepCount : 0)}'");
                output.WriteLine($"ForcedChildRuntimeKillCount='{(injectChildRuntimeFailure ? executionCycleCount : 0)}'");
                output.WriteLine($"PodFailureTrigger='{(waitForExternalPodDeletion ? "external-manual" : "automatic")}'");
                output.WriteLine($"ForcedPodDeletionCount='{(waitForExternalPodDeletion ? 0 : executionCycleCount)}'");
                output.WriteLine($"ExternalPodDeletionCount='{(waitForExternalPodDeletion ? executionCycleCount : 0)}'");
                output.WriteLine($"RecoveredSharedRunCount='{expectedRecoveredRunCountPerCycle * executionCycleCount}'");
                output.WriteLine($"RecoveryForensicsProofCount='{allRecoveryForensicsIds.Length}'");
                output.WriteLine($"FinalPhysicalPodCountBeforeCleanup='{finalPhysicalPodCount}'");
                output.WriteLine($"ScenarioTotalDuration='{totalStopwatch.Elapsed}'");
                output.WriteLine("WarmPoolReusedBetweenCycles='true'");
                output.WriteLine("IntermediateCleanupExecuted='false'");
                output.WriteLine("FinalCleanupPending='true'");
                output.WriteLine("DuplicateDispatchDetected='false'");
                output.WriteLine("LostRunDetected='false'");
                output.WriteLine("PodCapacityExceeded='false'");
                output.WriteLine("RuntimeCapacityExceeded='false'");
            }
            catch (Exception exception)
            {
                await CaptureBoundedCapacityFailureDiagnosticsAsync(
                        controlPlaneId,
                        poolId,
                        exception)
                    .ConfigureAwait(false);

                throw;
            }
            finally
            {
                totalStopwatch.Stop();

                output.WriteLine(
                    $"[{boundedCapacityProfile.LogPrefix} WARM REUSE FINAL CLEANUP] " +
                    $"ControlPlaneId='{controlPlaneId}', " +
                    $"PoolId='{poolId}', " +
                    $"ExecutionCycleCount='{executionCycleCount}', " +
                    "CleanupTrigger='final-cycle-completed'.");

                await OnCrashRecoveryScenarioCompletedAsync(controlPlaneId)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates the transport-neutral physical controller used to kill one exact runtime process
        /// inside a Kubernetes Runtime Pool Pod without deleting that Pod.
        /// </summary>
        protected IAiRuntimeHostProcessControl CreateRuntimePoolChildProcessControl(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            string logPrefix)
        {
            return new KubernetesRuntimePoolChildProcessControl(
                registry,
                poolId,
                output,
                logPrefix);
        }

        private static async Task<BoundedCapacityChildRuntimeFailureTarget>
            WaitForBoundedCapacityBusyChildRuntimeFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string poolId,
                string tenantId,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity,
                TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastRuntimeCount = 0;
            var lastRunningCount = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var runtimes =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime =>
                                StringComparer.Ordinal.Equals(
                                    runtime.PoolId,
                                    poolId) &&
                                StringComparer.Ordinal.Equals(
                                    runtime.ControlPlaneId,
                                    controlPlaneId) &&
                                !string.IsNullOrWhiteSpace(runtime.HostId) &&
                                !string.IsNullOrWhiteSpace(
                                    runtime.KubernetesPodName) &&
                                !string.IsNullOrWhiteSpace(
                                    runtime.KubernetesNamespace))
                        .ToArray();

                lastRuntimeCount = runtimes.Length;

                if (runtimes.Length == maximumRuntimeCapacity)
                {
                    var runtimesById =
                        runtimes.ToDictionary(
                            runtime => runtime.RuntimeInstanceId,
                            StringComparer.Ordinal);
                    var membersByHostId =
                        runtimes
                            .GroupBy(
                                runtime => runtime.HostId!,
                                StringComparer.Ordinal)
                            .Where(group => group.Count() == runtimeCountPerPod)
                            .ToDictionary(
                                group => group.Key,
                                group => group
                                    .OrderBy(
                                        runtime => runtime.RuntimeInstanceId,
                                        StringComparer.Ordinal)
                                    .ToArray(),
                                StringComparer.Ordinal);
                    var sharedRuns =
                        await ReadExactSubmittedSharedRunsAsync(
                                sharedRunStore,
                                submittedSharedRunIds,
                                controlPlaneId,
                                tenantId)
                            .ConfigureAwait(false);

                    lastRunningCount = 0;

                    foreach (var sharedRun in sharedRuns
                                 .Where(
                                     run =>
                                         !string.IsNullOrWhiteSpace(
                                             run.AssignedRuntimeInstanceId) &&
                                         !string.IsNullOrWhiteSpace(
                                             run.LocalRunId))
                                 .OrderByDescending(run => run.UpdatedAtUtc))
                    {
                        if (!runtimesById.TryGetValue(
                                sharedRun.AssignedRuntimeInstanceId!,
                                out var runtime) ||
                            !membersByHostId.TryGetValue(
                                runtime.HostId!,
                                out var hostMembers) ||
                            runtime.ProcessId.GetValueOrDefault() <= 0)
                        {
                            continue;
                        }

                        var index =
                            await runExecutionIndex
                                .GetAsync(sharedRun.LocalRunId!)
                                .ConfigureAwait(false);
                        var executionId =
                            index?.ExecutionId ?? sharedRun.ExecutionId;

                        if (index is null ||
                            string.IsNullOrWhiteSpace(executionId) ||
                            !string.Equals(
                                index.Status,
                                "running",
                                StringComparison.OrdinalIgnoreCase) ||
                            !StringComparer.Ordinal.Equals(
                                index.RuntimeInstanceId,
                                runtime.RuntimeInstanceId))
                        {
                            continue;
                        }

                        lastRunningCount++;

                        var siblingRuntimeInstanceIds =
                            hostMembers
                                .Where(
                                    member => !StringComparer.Ordinal.Equals(
                                        member.RuntimeInstanceId,
                                        runtime.RuntimeInstanceId))
                                .Select(member => member.RuntimeInstanceId)
                                .ToHashSet(StringComparer.Ordinal);

                        if (siblingRuntimeInstanceIds.Count !=
                            runtimeCountPerPod - 1)
                        {
                            continue;
                        }

                        return new BoundedCapacityChildRuntimeFailureTarget(
                            runtime,
                            new BoundedCapacityRunObservation(
                                sharedRun,
                                RuntimeIndexExists: true,
                                RuntimeIndexStatus: index.Status,
                                RuntimeIndexRuntimeInstanceId:
                                    index.RuntimeInstanceId,
                                RuntimeIndexExecutionId: executionId,
                                RuntimeIndexCompletedAtUtc:
                                    index.CompletedAtUtc,
                                DagExecutionStatus: null,
                                UseDagExecutionCompletion: false),
                            siblingRuntimeInstanceIds);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"A busy child runtime was not observed before the final KubernetesPool failure injection. RuntimeCount='{lastRuntimeCount}', RunningRunCount='{lastRunningCount}', ExpectedRuntimeCount='{maximumRuntimeCapacity}', RuntimeCountPerPod='{runtimeCountPerPod}'.");
        }

        private static async Task<IReadOnlyList<AiSharedRunRecord>>
            ReadExactSubmittedSharedRunsAsync(
                IAiSharedRunStore sharedRunStore,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId)
        {
            var recordsById =
                (await sharedRunStore
                        .ListAsync(
                            includeCancelled: true,
                            includeCompleted: true,
                            includeFailed: true)
                        .ConfigureAwait(false))
                    .Where(
                        run => submittedSharedRunIds.Contains(run.SharedRunId))
                    .ToDictionary(
                        run => run.SharedRunId,
                        StringComparer.Ordinal);

            foreach (var sharedRunId in submittedSharedRunIds)
            {
                if (recordsById.ContainsKey(sharedRunId))
                {
                    continue;
                }

                var exact =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (exact is not null)
                {
                    recordsById[sharedRunId] = exact;
                }
            }

            return recordsById.Values
                .Where(
                    run =>
                        StringComparer.Ordinal.Equals(
                            run.ControlPlaneId,
                            controlPlaneId) &&
                        StringComparer.Ordinal.Equals(
                            run.ExecutionContextSnapshot.TenantId,
                            tenantId))
                .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                .ToArray();
        }

        private static RealRuntimeCrashAssignedWorkInventoryProof
            CreateBoundedCapacityChildRuntimeFailureInventory(
                ProductionTenantScenarioDefinition tenant,
                McpTestClient mcp,
                BoundedCapacityChildRuntimeFailureTarget target)
        {
            var executionId =
                target.ActiveRun.ResolvedExecutionId
                ?? throw new InvalidOperationException(
                    "The selected KubernetesPool child runtime run has no durable ExecutionId.");
            var localRunId =
                target.ActiveRun.SharedRun.LocalRunId
                ?? throw new InvalidOperationException(
                    "The selected KubernetesPool child runtime run has no LocalRunId.");

            return new RealRuntimeCrashAssignedWorkInventoryProof
            {
                Tenant = tenant,
                Mcp = mcp,
                RuntimeInstanceId = target.Runtime.RuntimeInstanceId,
                Works = new[]
                {
                    new RealRuntimeCrashWorkProof
                    {
                        Kind = RealRuntimeCrashWorkKind.InFlightExecution,
                        SharedRun = target.ActiveRun.SharedRun,
                        SharedRunId = target.ActiveRun.SharedRun.SharedRunId,
                        LocalRunId = localRunId,
                        ExecutionId = executionId,
                        PipelineName =
                            target.ActiveRun.SharedRun.PipelineKey ??
                            target.ActiveRun.SharedRun.RunRequest.PipelineName
                    }
                }
            };
        }

        private async Task
            AssertExactBoundedCapacityChildRuntimeReplacementAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                BoundedCapacityPoolMembershipSnapshot initialMembership,
                BoundedCapacityPoolMembershipSnapshot replacementMembership,
                BoundedCapacityChildRuntimeFailureTarget target,
                RealRuntimeCrashFailedRuntimeRecoveryProof recoveryProof,
                int runtimeCountPerPod,
                int cycleNumber,
                string logPrefix)
        {
            RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                initialMembership.PodUids,
                replacementMembership.PodUids,
                $"Cycle {cycleNumber} child runtime parent Pod survival");

            Assert.DoesNotContain(
                target.Runtime.RuntimeInstanceId,
                replacementMembership.RuntimeInstanceIds);
            Assert.True(
                target.SiblingRuntimeInstanceIds.IsSubsetOf(
                    replacementMembership.RuntimeInstanceIds),
                $"{logPrefix} cycle {cycleNumber} changed one or more sibling runtime identities during one child replacement.");

            var replacementRuntimeInstanceIds =
                replacementMembership.RuntimeInstanceIds
                    .Except(
                        initialMembership.RuntimeInstanceIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);
            var replacementRuntimeInstanceId =
                Assert.Single(replacementRuntimeInstanceIds);
            var replacementRuntime =
                await GetRequiredRuntimeSnapshotAsync(
                        registry,
                        replacementRuntimeInstanceId)
                    .ConfigureAwait(false);

            AssertRuntimePoolIdentity(replacementRuntime, poolId);
            Assert.Equal(target.Runtime.HostId, replacementRuntime.HostId);
            Assert.Equal(
                target.Runtime.KubernetesPodName,
                replacementRuntime.KubernetesPodName);
            Assert.Equal(
                target.Runtime.KubernetesNamespace,
                replacementRuntime.KubernetesNamespace);
            var currentPodMembers =
                (await registry
                        .ListAsync(includeStopped: false)
                        .ConfigureAwait(false))
                    .Where(
                        runtime =>
                            StringComparer.Ordinal.Equals(
                                runtime.PoolId,
                                poolId) &&
                            StringComparer.Ordinal.Equals(
                                runtime.HostId,
                                target.Runtime.HostId))
                    .ToArray();

            Assert.Equal(runtimeCountPerPod, currentPodMembers.Length);
            Assert.Single(recoveryProof.RecoveredWorks);
            Assert.Equal(
                target.Runtime.RuntimeInstanceId,
                recoveryProof.FailedInventory.RuntimeInstanceId);

            output.WriteLine(
                $"[{logPrefix} CHILD RUNTIME REPLACEMENT] Cycle='{cycleNumber}', PodUid='{target.Runtime.HostId}', PodName='{target.Runtime.KubernetesPodName}', FailedRuntimeInstanceId='{target.Runtime.RuntimeInstanceId}', ReplacementRuntimeInstanceId='{replacementRuntimeInstanceId}', PreservedSiblingCount='{target.SiblingRuntimeInstanceIds.Count}', ParentPodSurvived='true'.");
        }

        private sealed class KubernetesRuntimePoolChildProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly string poolId;
            private readonly ITestOutputHelper output;
            private readonly string logPrefix;

            public KubernetesRuntimePoolChildProcessControl(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                ITestOutputHelper output,
                string logPrefix)
            {
                this.registry = registry;
                this.poolId = poolId;
                this.output = output;
                this.logPrefix = logPrefix;
            }

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    await GetRequiredRuntimeSnapshotAsync(
                            this.registry,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                AssertRuntimePoolIdentity(snapshot, this.poolId);
                Assert.True(snapshot.ProcessId.HasValue);

                this.output.WriteLine(
                    $"[{this.logPrefix} KUBERNETES RUNTIME POOL CHILD PROCESS KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ProcessId='{snapshot.ProcessId}'.");

                var result =
                    await RunKubectlAsync(
                            cancellationToken,
                            "exec",
                            snapshot.KubernetesPodName!,
                            "--namespace",
                            snapshot.KubernetesNamespace!,
                            "--container",
                            "runtime-pool",
                            "--",
                            "sh",
                            "-c",
                            string.Concat(
                                "kill -9 ",
                                snapshot.ProcessId.Value.ToString(
                                    CultureInfo.InvariantCulture)))
                        .ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The in-Pod runtime process could not be killed. StandardError=",
                            result.StandardError));
                }

                return true;
            }
        }

        private async Task<BoundedCapacityPodFailureProof>
            InjectBoundedCapacityPodFailureAsync(
                IServiceProvider services,
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IReadOnlySet<string> submittedSharedRunIds,
                ProductionTenantScenarioDefinition tenant,
                string controlPlaneId,
                string poolId,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity,
                BoundedCapacityMachineLimitObservation observation,
                TimeSpan timeout,
                IReadOnlySet<string>? excludedPodUids = null,
                ProductionCrashCheckpointGate? boundaryFailureCrashGate = null,
                bool waitForExternalPodDeletion = false,
                int? externalFailureCycleNumber = null,
                bool useDagExecutionCompletion = false)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRuntimeCapacity);
            ArgumentNullException.ThrowIfNull(observation);

            var dagStore =
                services.GetRequiredService<IAiDagExecutionStore>();

            var target =
                await WaitForBoundedCapacityBusyPodFailureTargetAsync(
                        registry,
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        useDagExecutionCompletion,
                        controlPlaneId,
                        poolId,
                        tenant.TenantId,
                        submittedSharedRunIds,
                        runtimeCountPerPod,
                        maximumRuntimeCapacity,
                        timeout,
                        excludedPodUids)
                    .ConfigureAwait(false);

            var primaryRuntime =
                target.Members
                    .OrderBy(
                        member => member.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .First();

            AssertRuntimePoolIdentity(primaryRuntime, poolId);

            var failedRuntimeInstanceIds =
                target.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            var impactedSharedRunIds =
                target.ActiveRuns
                    .Select(run => run.SharedRun.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);

            var impactedExecutionIds =
                target.ActiveRuns
                    .Select(run => run.ResolvedExecutionId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);

            Assert.All(
                target.ActiveRuns,
                run => Assert.False(
                    string.IsNullOrWhiteSpace(run.SharedRun.LocalRunId)));

            var recoveryForensicsIds =
                target.ActiveRuns
                    .Select(
                        run => string.Join(
                            ":",
                            "runtime-recovery",
                            run.ResolvedExecutionId,
                            run.SharedRun.SharedRunId,
                            run.SharedRun.LocalRunId))
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(runtimeCountPerPod, failedRuntimeInstanceIds.Count);
            Assert.Equal(runtimeCountPerPod, impactedSharedRunIds.Count);
            Assert.Equal(runtimeCountPerPod, impactedExecutionIds.Count);
            Assert.Equal(runtimeCountPerPod, recoveryForensicsIds.Count);
            Assert.Equal(
                (maximumRuntimeCapacity / runtimeCountPerPod) - 1,
                target.SurvivingHostIds.Count);

            output.WriteLine(
                $"[{profile.LogPrefix} BOUNDED CAPACITY POD FAILURE TARGET] " +
                $"PodUid='{primaryRuntime.HostId}', " +
                $"PodName='{primaryRuntime.KubernetesPodName}', " +
                $"RuntimeCount='{failedRuntimeInstanceIds.Count}', " +
                $"ActiveRunCount='{impactedSharedRunIds.Count}', " +
                $"SurvivingPodCount='{target.SurvivingHostIds.Count}'.");

            var membershipEnumerator =
                services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodMembershipEnumerator>();

            var failedMembership =
                await membershipEnumerator
                    .EnumerateAsync(
                        poolId,
                        primaryRuntime.HostId!)
                    .ConfigureAwait(false);

            Assert.Equal(
                runtimeCountPerPod,
                failedMembership.Members.Count);
            Assert.All(
                failedMembership.Members,
                member => Assert.Contains(
                    member.RuntimeInstanceId,
                    failedRuntimeInstanceIds));

            observation.MarkIntentionalFailedPodGeneration(
                primaryRuntime.HostId!,
                failedRuntimeInstanceIds);

            try
            {
                if (waitForExternalPodDeletion)
                {
                    if (!externalFailureCycleNumber.HasValue)
                    {
                        throw new InvalidOperationException(
                            "An external Pod failure requires the active warm-reuse cycle number.");
                    }

                    await this
                        .WaitForExternalPodDeletionAsync(
                            primaryRuntime,
                            externalFailureCycleNumber.Value,
                            runtimeCountPerPod,
                            TimeSpan.FromMinutes(
                                ExternalBoundaryFailureWaitTimeoutMinutes))
                        .ConfigureAwait(false);
                }
                else
                {
                    var deleteResult =
                        await RunKubectlAsync(
                                CancellationToken.None,
                                "delete",
                                "pod",
                                primaryRuntime.KubernetesPodName!,
                                "--namespace",
                                primaryRuntime.KubernetesNamespace!,
                                "--grace-period=0",
                                "--force",
                                "--wait=true",
                                "--timeout=90s")
                            .ConfigureAwait(false);

                    if (deleteResult.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            string.Concat(
                                "The bounded-capacity Kubernetes Runtime Pool Pod could not be force-deleted. StandardError=",
                                deleteResult.StandardError));
                    }
                }
            }
            finally
            {
                if (boundaryFailureCrashGate is not null)
                {
                    // Keep the deferred failure wave frozen only through the
                    // exact Pod termination. Healthy Pods and replacement work
                    // resume from the same durable released checkpoint state.
                    await boundaryFailureCrashGate
                        .ReleaseAsync()
                        .WaitAsync(timeout)
                        .ConfigureAwait(false);
                }
            }

            var failureId =
                string.Concat(
                    "bounded-capacity-pod-failure-",
                    primaryRuntime.HostId);

            var coordinator =
                services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator>();

            var recovery =
                await coordinator
                    .RecoverAsync(
                        new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                        {
                            FailureId = failureId,
                            PoolId = poolId,
                            PodUid = primaryRuntime.HostId!,
                            ClaimedBy =
                                string.Concat("mcp-", profile.ProviderName, "-kubernetes-runtime-pool-bounded-capacity-scenario"),
                            FailureMessage =
                                waitForExternalPodDeletion
                                    ? "Externally forced busy Kubernetes Runtime Pool Pod deletion in the bounded-capacity recovery proof."
                                    : "Forced busy Kubernetes Runtime Pool Pod deletion in the bounded-capacity recovery proof.",
                            HostStartTemplate =
                                CreateBoundedCapacityPodRecoveryHostStartTemplate(
                                    primaryRuntime,
                                    tenant,
                                    controlPlaneId,
                                    poolId,
                                    maximumRuntimeCapacity)
                        },
                        CancellationToken.None)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                recovery.Status);
            Assert.Equal(failureId, recovery.FailureId);
            Assert.Equal(poolId, recovery.PoolId);
            Assert.Equal(primaryRuntime.HostId, recovery.FailedPodUid);
            Assert.Equal(
                runtimeCountPerPod,
                recovery.Suppression.Suppressions.Count);
            Assert.Equal(
                runtimeCountPerPod,
                recovery.ClaimedAssignedWork.Inventory.RuntimeInventories.Count);
            Assert.NotNull(recovery.Replacement);
            Assert.NotNull(recovery.Recovery);

            var replacement = recovery.Replacement!;
            var recoveryExecution = recovery.Recovery!;

            Assert.NotEqual(
                primaryRuntime.HostId,
                replacement.ReplacementPodUid);
            Assert.Equal(
                runtimeCountPerPod,
                replacement.Membership.Members.Count);
            Assert.DoesNotContain(
                replacement.Membership.Members,
                member => failedRuntimeInstanceIds.Contains(
                    member.RuntimeInstanceId));

            Assert.Equal(runtimeCountPerPod, recoveryExecution.MemberCount);
            Assert.Equal(
                recoveryExecution.CandidateCount,
                recoveryExecution.Outcomes.Count);

            // A warm runtime can retain a failed local attempt that has already
            // been superseded by newer shared-run ownership. The durable index
            // may enumerate it, but it must remain rejected and unchanged.
            var currentFailureTargetLocalRunIds =
                target.ActiveRuns
                    .Select(run => run.SharedRun.LocalRunId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                runtimeCountPerPod,
                currentFailureTargetLocalRunIds.Count);

            var currentFailureTargetOutcomes =
                recoveryExecution.Outcomes
                    .Where(
                        outcome =>
                            currentFailureTargetLocalRunIds.Contains(
                                outcome.Candidate.LocalRunId))
                    .ToArray();

            var supersededFailedCandidateOutcomes =
                recoveryExecution.Outcomes
                    .Where(
                        outcome =>
                            !currentFailureTargetLocalRunIds.Contains(
                                outcome.Candidate.LocalRunId))
                    .ToArray();

            Assert.Equal(
                runtimeCountPerPod,
                currentFailureTargetOutcomes.Length);

            Assert.All(
                currentFailureTargetOutcomes,
                outcome =>
                {
                    Assert.True(
                        outcome.Transition.Accepted,
                        $"Current busy-Pod work was not accepted for recovery. LocalRunId='{outcome.Candidate.LocalRunId}', SharedRunId='{outcome.Candidate.SharedRunId}', ExecutionId='{outcome.Candidate.ExecutionId}', Reason='{outcome.Transition.Reason}'.");
                    Assert.True(
                        outcome.Transition.Changed,
                        $"Current busy-Pod work did not change durable recovery state. LocalRunId='{outcome.Candidate.LocalRunId}', SharedRunId='{outcome.Candidate.SharedRunId}', ExecutionId='{outcome.Candidate.ExecutionId}', Reason='{outcome.Transition.Reason}'.");
                    Assert.Contains(
                        outcome.Candidate.SharedRunId!,
                        impactedSharedRunIds);
                    Assert.Contains(
                        outcome.Candidate.ExecutionId!,
                        impactedExecutionIds);
                });

            Assert.All(
                supersededFailedCandidateOutcomes,
                outcome =>
                {
                    Assert.True(
                        string.Equals(
                            outcome.Candidate.Status,
                            "failed",
                            StringComparison.OrdinalIgnoreCase),
                        $"Only a superseded failed local attempt may remain in the durable runtime index beside the exact current failure target. LocalRunId='{outcome.Candidate.LocalRunId}', Status='{outcome.Candidate.Status}', SharedRunId='{outcome.Candidate.SharedRunId}', ExecutionId='{outcome.Candidate.ExecutionId}'.");
                    Assert.NotNull(outcome.Ownership);
                    Assert.False(
                        outcome.Ownership!.CanRecover,
                        $"A non-target durable candidate unexpectedly remained recoverable. LocalRunId='{outcome.Candidate.LocalRunId}', SharedRunId='{outcome.Candidate.SharedRunId}', ExecutionId='{outcome.Candidate.ExecutionId}', OwnershipReason='{outcome.Ownership.Reason}'.");
                    Assert.False(
                        outcome.Transition.Accepted,
                        $"A superseded failed local attempt was unexpectedly accepted for recovery. LocalRunId='{outcome.Candidate.LocalRunId}', SharedRunId='{outcome.Candidate.SharedRunId}', ExecutionId='{outcome.Candidate.ExecutionId}', Reason='{outcome.Transition.Reason}'.");
                    Assert.False(outcome.Transition.Changed);
                    Assert.Equal("none", outcome.Transition.Action);
                    Assert.True(
                        string.Equals(
                            outcome.Transition.Reason,
                            "ownership-not-resolved",
                            StringComparison.Ordinal) ||
                        string.Equals(
                            outcome.Transition.Reason,
                            "ownership-not-recoverable",
                            StringComparison.Ordinal),
                        $"A superseded failed local attempt was rejected for an unexpected reason. LocalRunId='{outcome.Candidate.LocalRunId}', Reason='{outcome.Transition.Reason}'.");
                });

            Assert.Equal(
                checked(
                    runtimeCountPerPod +
                    supersededFailedCandidateOutcomes.Length),
                recoveryExecution.CandidateCount);
            Assert.Equal(runtimeCountPerPod, recoveryExecution.AcceptedCount);
            Assert.Equal(runtimeCountPerPod, recoveryExecution.ChangedCount);
            Assert.Equal(
                supersededFailedCandidateOutcomes.Length,
                recoveryExecution.RejectedCount);

            var recoveredSharedRunIds =
                currentFailureTargetOutcomes
                    .Select(outcome => outcome.Transition.SharedRunId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(runtimeCountPerPod, recoveredSharedRunIds.Count);
            Assert.All(
                recoveredSharedRunIds,
                sharedRunId => Assert.Contains(
                    sharedRunId,
                    impactedSharedRunIds));

            await AssertSurvivingHostsRemainReadyAsync(
                    registry,
                    poolId,
                    target.SurvivingHostIds,
                    timeout)
                .ConfigureAwait(false);

            var replacementRuntimeInstanceIds =
                replacement.Membership.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                runtimeCountPerPod,
                replacementRuntimeInstanceIds.Count);

            output.WriteLine(
                $"[{profile.LogPrefix} BOUNDED CAPACITY POD RECOVERY] " +
                $"FailureId='{failureId}', " +
                $"FailedPodUid='{primaryRuntime.HostId}', " +
                $"ReplacementPodUid='{replacement.ReplacementPodUid}', " +
                $"FailedRuntimeCount='{failedRuntimeInstanceIds.Count}', " +
                $"RecoveredSharedRunCount='{recoveredSharedRunIds.Count}', " +
                $"CandidateCount='{recoveryExecution.CandidateCount}', " +
                $"AcceptedCount='{recoveryExecution.AcceptedCount}', " +
                $"ChangedCount='{recoveryExecution.ChangedCount}', " +
                $"RejectedCount='{recoveryExecution.RejectedCount}', " +
                $"SupersededFailedCandidateCount='{supersededFailedCandidateOutcomes.Length}', " +
                $"SupersededFailedLocalRunIds='{string.Join(",", supersededFailedCandidateOutcomes.Select(outcome => outcome.Candidate.LocalRunId))}'.");

            return new BoundedCapacityPodFailureProof(
                failureId,
                primaryRuntime.HostId!,
                primaryRuntime.KubernetesPodName!,
                replacement.ReplacementPodUid,
                failedRuntimeInstanceIds,
                replacementRuntimeInstanceIds,
                recoveredSharedRunIds,
                impactedExecutionIds,
                recoveryForensicsIds);
        }

        private async Task WaitForExternalPodDeletionAsync(
            AiRuntimeInstanceSnapshot primaryRuntime,
            int cycleNumber,
            int runtimeCountPerPod,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(primaryRuntime);

            var podName =
                !string.IsNullOrWhiteSpace(primaryRuntime.KubernetesPodName)
                    ? primaryRuntime.KubernetesPodName!
                    : throw new InvalidOperationException(
                        "The external Pod failure target does not expose a Kubernetes Pod name.");
            var namespaceName =
                !string.IsNullOrWhiteSpace(primaryRuntime.KubernetesNamespace)
                    ? primaryRuntime.KubernetesNamespace!
                    : throw new InvalidOperationException(
                        "The external Pod failure target does not expose a Kubernetes namespace.");
            var expectedPodUid =
                !string.IsNullOrWhiteSpace(primaryRuntime.HostId)
                    ? primaryRuntime.HostId!
                    : throw new InvalidOperationException(
                        "The external Pod failure target does not expose its immutable Pod UID.");
            var command =
                $"kubectl delete pod {podName} --namespace {namespaceName} --grace-period=0 --force";

            var signalPath =
                ManualExternalFailureGateSignal.ArmKubernetesPod(
                    cycleNumber,
                    expectedPodUid,
                    podName,
                    namespaceName,
                    command);

            output.WriteLine(
                $"[{profile.LogPrefix} EXTERNAL POD FAILURE ARMED] Cycle='{cycleNumber}', PodUid='{expectedPodUid}', PodName='{podName}', Namespace='{namespaceName}', RuntimeCount='{runtimeCountPerPod}', PowerShellWatchCommand='{ManualExternalFailureGateSignal.KubernetesPowerShellWatchCommand}', SignalFile='{signalPath}'.");
            output.WriteLine(
                $"[{profile.LogPrefix} WAITING-FOR-EXTERNAL-POD-DELETION] Cycle='{cycleNumber}', PodUid='{expectedPodUid}', PodName='{podName}', Command='{command}', PowerShellWatchCommand='{ManualExternalFailureGateSignal.KubernetesPowerShellWatchCommand}', Timeout='{timeout}', SignalFile='{signalPath}'.");

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            string? lastError = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var result =
                    await RunKubectlAsync(
                            CancellationToken.None,
                            "get",
                            "pod",
                            podName,
                            "--namespace",
                            namespaceName,
                            "--ignore-not-found=true",
                            "--output=jsonpath={.metadata.uid}")
                        .ConfigureAwait(false);

                if (result.ExitCode == 0)
                {
                    var observedPodUid = result.StandardOutput.Trim();

                    if (string.IsNullOrWhiteSpace(observedPodUid) ||
                        !StringComparer.Ordinal.Equals(
                            observedPodUid,
                            expectedPodUid))
                    {
                        ManualExternalFailureGateSignal.MarkObserved(
                            signalPath,
                            $"PodUid={expectedPodUid};PodName={podName}");
                        output.WriteLine(
                            $"[{profile.LogPrefix} EXTERNAL POD FAILURE OBSERVED] Cycle='{cycleNumber}', FailedPodUid='{expectedPodUid}', PodName='{podName}', ObservedPodUid='{observedPodUid}', State='exact-incarnation-gone', SignalFile='{signalPath}'.");
                        return;
                    }
                }
                else
                {
                    lastError = result.StandardError.Trim();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The externally selected Kubernetes Runtime Pool Pod was not deleted within '{timeout}'. PodUid='{expectedPodUid}', PodName='{podName}', Namespace='{namespaceName}', Command='{command}', LastKubectlError='{lastError}'.");
        }

        private static async Task<BoundedCapacityBusyPodFailureTarget>
            WaitForBoundedCapacityBusyPodFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IAiDagExecutionStore dagStore,
                bool useDagExecutionCompletion,
                string controlPlaneId,
                string poolId,
                string tenantId,
                IReadOnlySet<string> submittedSharedRunIds,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity,
                TimeSpan timeout,
                IReadOnlySet<string>? excludedPodUids = null)
        {
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentNullException.ThrowIfNull(dagStore);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastRuntimeCount = 0;
            var lastBusyPodCount = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var runtimes =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime => StringComparer.Ordinal.Equals(
                                runtime.PoolId,
                                poolId))
                        .ToArray();

                lastRuntimeCount = runtimes.Length;

                if (runtimes.Length == maximumRuntimeCapacity)
                {
                    var runtimeGroups =
                        runtimes
                            .Where(
                                runtime =>
                                    !string.IsNullOrWhiteSpace(runtime.HostId) &&
                                    !string.IsNullOrWhiteSpace(runtime.KubernetesPodName) &&
                                    !string.IsNullOrWhiteSpace(runtime.KubernetesNamespace))
                            .GroupBy(
                                runtime => runtime.HostId!,
                                StringComparer.Ordinal)
                            .Where(group => group.Count() == runtimeCountPerPod)
                            .OrderByDescending(
                                group => group.Max(
                                    runtime => runtime.RegisteredAtUtc))
                            .ToArray();

                    var busyGroups =
                        runtimeGroups
                            .Where(
                                group =>
                                    excludedPodUids?.Contains(group.Key) != true &&
                                    group.All(
                                        runtime =>
                                            runtime.Status == AiRuntimeInstanceStatus.Ready &&
                                            runtime.RunningRunCount == 1 &&
                                            runtime.ActiveRunCount == 1 &&
                                            !runtime.CanAcceptRun))
                            .ToArray();

                    lastBusyPodCount = busyGroups.Length;

                    if (busyGroups.Length > 0)
                    {
                        var sharedRuns =
                            await ReadExactSubmittedSharedRunsAsync(
                                    sharedRunStore,
                                    submittedSharedRunIds,
                                    controlPlaneId,
                                    tenantId)
                                .ConfigureAwait(false);

                        foreach (var busyGroup in busyGroups)
                        {
                            var runtimeInstanceIds =
                                busyGroup
                                    .Select(runtime => runtime.RuntimeInstanceId)
                                    .ToHashSet(StringComparer.Ordinal);

                            var assignedRuns =
                                sharedRuns
                                    .Where(
                                        run =>
                                            !string.IsNullOrWhiteSpace(
                                                run.AssignedRuntimeInstanceId) &&
                                            runtimeInstanceIds.Contains(
                                                run.AssignedRuntimeInstanceId!) &&
                                            !string.IsNullOrWhiteSpace(
                                                run.LocalRunId))
                                    .ToArray();

                            var runObservations =
                                await Task.WhenAll(
                                        assignedRuns.Select(
                                            run =>
                                                ReadBoundedCapacityRunObservationAsync(
                                                    runExecutionIndex,
                                                    dagStore,
                                                    run,
                                                    useDagExecutionCompletion)))
                                    .ConfigureAwait(false);

                            var activeRunsByRuntimeInstanceId =
                                runObservations
                                    .Where(
                                        run =>
                                            !run.IsCompleted &&
                                            !run.IsRuntimeIndexTerminalFailure &&
                                            !string.IsNullOrWhiteSpace(
                                                run.ResolvedExecutionId) &&
                                            !string.IsNullOrWhiteSpace(
                                                run.RuntimeIndexRuntimeInstanceId) &&
                                            runtimeInstanceIds.Contains(
                                                run.RuntimeIndexRuntimeInstanceId!))
                                    .GroupBy(
                                        run => run.RuntimeIndexRuntimeInstanceId!,
                                        StringComparer.Ordinal)
                                    .ToDictionary(
                                        group => group.Key,
                                        group => group
                                            .OrderByDescending(
                                                run => run.SharedRun.UpdatedAtUtc)
                                            .First(),
                                        StringComparer.Ordinal);

                            if (runtimeInstanceIds.All(
                                    activeRunsByRuntimeInstanceId.ContainsKey))
                            {
                                var survivingHostIds =
                                    runtimeGroups
                                        .Where(
                                            group => !StringComparer.Ordinal.Equals(
                                                group.Key,
                                                busyGroup.Key))
                                        .Select(group => group.Key)
                                        .ToHashSet(StringComparer.Ordinal);

                                return new BoundedCapacityBusyPodFailureTarget(
                                    busyGroup
                                        .OrderBy(
                                            runtime => runtime.RuntimeInstanceId,
                                            StringComparer.Ordinal)
                                        .ToArray(),
                                    runtimeInstanceIds
                                        .OrderBy(
                                            runtimeInstanceId => runtimeInstanceId,
                                            StringComparer.Ordinal)
                                        .Select(
                                            runtimeInstanceId =>
                                                activeRunsByRuntimeInstanceId[
                                                    runtimeInstanceId])
                                        .ToArray(),
                                    survivingHostIds);
                            }
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"A fully busy bounded-capacity Pod was not observed before failure injection. RuntimeCount='{lastRuntimeCount}', BusyPodCount='{lastBusyPodCount}', ExpectedRuntimeCount='{maximumRuntimeCapacity}', RuntimeCountPerPod='{runtimeCountPerPod}'.");
        }

        private AiRuntimeHostStartRequest
            CreateBoundedCapacityPodRecoveryHostStartTemplate(
                AiRuntimeInstanceSnapshot snapshot,
                ProductionTenantScenarioDefinition tenant,
                string controlPlaneId,
                string poolId,
                int maximumRuntimeCapacity)
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        "bounded-capacity-pod-recovery-template-",
                        controlPlaneId),
                ControlPlaneId = controlPlaneId,
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                RuntimeInstanceIdPrefix = tenant.RuntimeInstanceIdPrefix,
                ProviderName = profile.ProviderName,
                TransportName = profile.ProviderName,
                TenantId = tenant.TenantId,
                TenantGroupId = tenant.TenantGroupId,
                IsolationMode = "Shared",
                PreferDedicatedCapacity = false,
                AllowSharedFallback = true,
                WorkerCountPerInstance = tenant.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance =
                    tenant.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = tenant.LocalQueueCapacity,
                MaxRuntimeInstances = maximumRuntimeCapacity,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-bounded-capacity-pod-recovery-",
                                controlPlaneId),
                        Project =
                            "mcp-kubernetes-runtime-pool-bounded-capacity-pod-recovery",
                        UserId = "system",
                        TenantId = tenant.TenantId,
                        TenantGroupId = tenant.TenantGroupId,
                        CurrentNamespace = "tests",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    },
                Metadata = new Dictionary<string, string>()
            };
        }

        private static async Task<BoundedCapacityPoolMembershipSnapshot>
            WaitForBoundedCapacityPoolMembershipAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                int expectedPodCount,
                int runtimeCountPerPod,
                bool requireAvailableCapacity,
                TimeSpan timeout,
                TimeSpan? hardTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The topology convergence timeout must be positive.");
            }

            if (hardTimeout.HasValue &&
                hardTimeout.Value < timeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hardTimeout),
                    hardTimeout,
                    "The topology convergence hard timeout cannot be shorter than the progress timeout.");
            }

            var expectedRuntimeCount =
                checked(expectedPodCount * runtimeCountPerPod);

            var startedAtUtc =
                DateTimeOffset.UtcNow;
            var hardDeadlineUtc =
                startedAtUtc.Add(hardTimeout ?? timeout);
            var progressDeadlineUtc =
                startedAtUtc.Add(timeout);

            var lastPodCount = 0;
            var lastRuntimeCount = 0;
            var lastReadyRuntimeCount = 0;
            var lastAvailableRuntimeCount = 0;
            var highestPodCount = 0;
            var highestRuntimeCount = 0;
            var highestReadyRuntimeCount = 0;
            var highestAvailableRuntimeCount = 0;

            while (DateTimeOffset.UtcNow < hardDeadlineUtc &&
                   DateTimeOffset.UtcNow < progressDeadlineUtc)
            {
                var runtimes =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime => StringComparer.Ordinal.Equals(
                                runtime.PoolId,
                                poolId))
                        .ToArray();

                var pods =
                    runtimes
                        .Where(runtime => !string.IsNullOrWhiteSpace(runtime.HostId))
                        .GroupBy(
                            runtime => runtime.HostId!,
                            StringComparer.Ordinal)
                        .ToArray();

                lastPodCount = pods.Length;
                lastRuntimeCount = runtimes.Length;
                lastReadyRuntimeCount = runtimes.Count(
                    runtime => runtime.Status == AiRuntimeInstanceStatus.Ready);
                lastAvailableRuntimeCount = runtimes.Count(
                    runtime => runtime.CanAcceptRun);

                var madeTopologyProgress =
                    lastPodCount > highestPodCount ||
                    lastRuntimeCount > highestRuntimeCount ||
                    lastReadyRuntimeCount > highestReadyRuntimeCount ||
                    lastAvailableRuntimeCount > highestAvailableRuntimeCount;

                if (madeTopologyProgress)
                {
                    highestPodCount = Math.Max(
                        highestPodCount,
                        lastPodCount);
                    highestRuntimeCount = Math.Max(
                        highestRuntimeCount,
                        lastRuntimeCount);
                    highestReadyRuntimeCount = Math.Max(
                        highestReadyRuntimeCount,
                        lastReadyRuntimeCount);
                    highestAvailableRuntimeCount = Math.Max(
                        highestAvailableRuntimeCount,
                        lastAvailableRuntimeCount);

                    var renewedProgressDeadlineUtc =
                        DateTimeOffset.UtcNow.Add(timeout);

                    progressDeadlineUtc =
                        renewedProgressDeadlineUtc < hardDeadlineUtc
                            ? renewedProgressDeadlineUtc
                            : hardDeadlineUtc;
                }

                var exactTopology =
                    pods.Length == expectedPodCount &&
                    runtimes.Length == expectedRuntimeCount &&
                    pods.All(pod => pod.Count() == runtimeCountPerPod) &&
                    runtimes.All(
                        runtime =>
                            runtime.Status == AiRuntimeInstanceStatus.Ready &&
                            !string.IsNullOrWhiteSpace(runtime.HostId));

                var availabilitySatisfied =
                    !requireAvailableCapacity ||
                    runtimes.All(runtime => runtime.CanAcceptRun);

                if (exactTopology && availabilitySatisfied)
                {
                    return new BoundedCapacityPoolMembershipSnapshot(
                        pods
                            .Select(pod => pod.Key)
                            .ToHashSet(StringComparer.Ordinal),
                        runtimes
                            .Select(runtime => runtime.RuntimeInstanceId)
                            .ToHashSet(StringComparer.Ordinal));
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "The bounded-capacity warm Runtime Pool did not converge to the required reusable topology. " +
                $"PoolId='{poolId}', ExpectedPodCount='{expectedPodCount}', LastPodCount='{lastPodCount}', " +
                $"ExpectedRuntimeCount='{expectedRuntimeCount}', LastRuntimeCount='{lastRuntimeCount}', " +
                $"LastReadyRuntimeCount='{lastReadyRuntimeCount}', LastAvailableRuntimeCount='{lastAvailableRuntimeCount}', " +
                $"HighestPodCount='{highestPodCount}', HighestRuntimeCount='{highestRuntimeCount}', " +
                $"HighestReadyRuntimeCount='{highestReadyRuntimeCount}', HighestAvailableRuntimeCount='{highestAvailableRuntimeCount}', " +
                $"ProgressTimeout='{timeout}', HardTimeout='{hardTimeout ?? timeout}', " +
                $"RequireAvailableCapacity='{requireAvailableCapacity}'.");
        }

        private sealed record BoundedCapacityPoolMembershipSnapshot(
            IReadOnlySet<string> PodUids,
            IReadOnlySet<string> RuntimeInstanceIds);

        private sealed record BoundedCapacityWarmReuseCycleProof(
            int CycleNumber,
            IReadOnlySet<string> WarmStartPodUids,
            IReadOnlySet<string> WarmStartRuntimeInstanceIds,
            IReadOnlySet<string> PreFailurePodUids,
            IReadOnlySet<string> PreFailureRuntimeInstanceIds,
            IReadOnlySet<string> FinalPodUids,
            IReadOnlySet<string> FinalRuntimeInstanceIds,
            IReadOnlySet<string> SubmittedSharedRunIds,
            IReadOnlySet<string> ExecutionIds,
            IReadOnlySet<string> RecoveryForensicsIds,
            string FailureId,
            string FailedPodUid,
            string ReplacementPodUid,
            int ReplayProofCount,
            int StepCompletedLedgerCount,
            int Phase5TooManyRequestsRetryCount,
            TimeSpan Duration);

        private sealed record BoundedCapacityChildRuntimeFailureTarget(
            AiRuntimeInstanceSnapshot Runtime,
            BoundedCapacityRunObservation ActiveRun,
            IReadOnlySet<string> SiblingRuntimeInstanceIds);

        private sealed record BoundedCapacityBusyPodFailureTarget(
            IReadOnlyList<AiRuntimeInstanceSnapshot> Members,
            IReadOnlyList<BoundedCapacityRunObservation> ActiveRuns,
            IReadOnlySet<string> SurvivingHostIds);

        private sealed record BoundedCapacityPodFailureProof(
            string FailureId,
            string FailedPodUid,
            string FailedPodName,
            string ReplacementPodUid,
            IReadOnlySet<string> FailedRuntimeInstanceIds,
            IReadOnlySet<string> ReplacementRuntimeInstanceIds,
            IReadOnlySet<string> RecoveredSharedRunIds,
            IReadOnlySet<string> ImpactedExecutionIds,
            IReadOnlySet<string> RecoveryForensicsIds);

        private async Task<AiRuntimeRecoveryForensicsQueryResult>
            SearchRuntimeRecoveryForensicsWithBackpressureAsync(
                McpTestClient mcp,
                AiRuntimeRecoveryForensicsQuery query,
                int maximumAttemptCount)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(query);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumAttemptCount);

            var retryDelay =
                TimeSpan.FromMilliseconds(100);

            for (var attempt = 1;
                 attempt <= maximumAttemptCount;
                 attempt++)
            {
                try
                {
                    return await mcp
                        .SearchRuntimeRecoveryForensicsAsync(query)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                    when (exception.StatusCode ==
                        HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= maximumAttemptCount)
                    {
                        throw new HttpRequestException(
                            $"MCP no-recovery forensics proof remained throttled after '{attempt}' attempts. ControlPlaneId='{query.ControlPlaneId}', TenantId='{query.TenantId}'.",
                            exception,
                            HttpStatusCode.TooManyRequests);
                    }

                    output.WriteLine(
                        $"[{profile.LogPrefix} BOUNDED CAPACITY NO-RECOVERY FORENSICS BACKPRESSURE] " +
                        $"Attempt='{attempt}', " +
                        $"DelayMs='{(long)retryDelay.TotalMilliseconds}', " +
                        $"ControlPlaneId='{query.ControlPlaneId}', " +
                        $"TenantId='{query.TenantId}'.");

                    await Task
                        .Delay(retryDelay)
                        .ConfigureAwait(false);

                    retryDelay =
                        TimeSpan.FromMilliseconds(
                            Math.Min(
                                retryDelay.TotalMilliseconds * 2,
                                2_000));
                }
            }

            throw new InvalidOperationException(
                "The bounded no-recovery forensics retry loop exited unexpectedly.");
        }

        private Task CaptureBoundedCapacityFailureDiagnosticsAsync(
            string controlPlaneId,
            string poolId,
            Exception exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(exception);

            states.TryGetValue(
                controlPlaneId,
                out var state);

            var trackedPods =
                state?.GetTrackedPods()
                ?? Array.Empty<TrackedPod>();

            return infrastructure.CaptureFailureDiagnosticsAsync(
                controlPlaneId,
                poolId,
                exception,
                trackedPods);
        }



        private async Task WaitForBoundedCapacityScaleOutWatcherReadyAsync(
            IServiceProvider services,
            string controlPlaneId)
        {
            var watcher =
                services
                    .GetServices<IHostedService>()
                    .OfType<AiRuntimeScaleOutRequestWatcherHostedService>()
                    .SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "The runtime scale-out request watcher hosted service is not registered.");

            await watcher
                .WaitUntilReadyAsync(TimeSpan.FromMinutes(1))
                .ConfigureAwait(false);

            Assert.Equal(
                controlPlaneId,
                watcher.ResolvedControlPlaneId);
        }

        private static async Task<IReadOnlyList<BoundedCapacityCompletedRun>>
            WaitForSubmittedRunsToCompleteAsync(
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IAiDagExecutionStore dagStore,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                BoundedCapacityMachineLimitObservation observation,
                TimeSpan timeout,
                TimeSpan noProgressTimeout,
                bool useDagExecutionCompletion = false)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(observation);

            if (noProgressTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(noProgressTimeout));
            }

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            var lastProgressAtUtc =
                DateTimeOffset.UtcNow;

            string? lastProgressSignature = null;

            string? lastDurableDagProgressSignature = null;

            IReadOnlyList<AiRuntimeRunExecutionIndexEntry> lastUnfinishedRuntimeRuns =
                Array.Empty<AiRuntimeRunExecutionIndexEntry>();

            var nextDurableDagProgressProbeAtUtc =
                DateTimeOffset.UtcNow;

            IReadOnlyList<BoundedCapacityRunObservation> lastObservations =
                Array.Empty<BoundedCapacityRunObservation>();

            var completedObservationsBySharedRunId =
                new Dictionary<string, BoundedCapacityRunObservation>(
                    StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                observation.ThrowIfViolated();

                var lastRuns =
                    await ReadExactSubmittedSharedRunsAsync(
                            sharedRunStore,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId)
                        .ConfigureAwait(false);

                lastObservations =
                    await Task.WhenAll(
                            lastRuns.Select(
                                run =>
                                {
                                    if (completedObservationsBySharedRunId.TryGetValue(
                                            run.SharedRunId,
                                            out var completedObservation))
                                    {
                                        return Task.FromResult(
                                            completedObservation with
                                            {
                                                SharedRun = run
                                            });
                                    }

                                    return ReadBoundedCapacityRunObservationAsync(
                                        runExecutionIndex,
                                        dagStore,
                                        run,
                                        useDagExecutionCompletion);
                                }))
                        .ConfigureAwait(false);

                foreach (var completedObservation in
                    lastObservations.Where(run => run.IsCompleted))
                {
                    completedObservationsBySharedRunId[
                        completedObservation.SharedRun.SharedRunId] =
                            completedObservation;
                }

                if (lastObservations.Count == submittedSharedRunIds.Count &&
                    lastObservations.All(
                        run => run.IsCompleted))
                {
                    return lastObservations
                        .Select(
                            run =>
                                new BoundedCapacityCompletedRun(
                                    run.SharedRun,
                                    run.SharedRun.LocalRunId!,
                                    run.ResolvedExecutionId!,
                                    run.RuntimeIndexCompletedAtUtc))
                        .ToArray();
                }

                var sharedStatusProgressSignature =
                    string.Join(
                        ",",
                        lastObservations
                            .GroupBy(run => run.SharedRun.Status)
                            .OrderBy(
                                group => group.Key.ToString(),
                                StringComparer.Ordinal)
                            .Select(
                                group =>
                                    $"{group.Key}:{group.Count()}"));

                var runtimeIndexStatusProgressSignature =
                    string.Join(
                        ",",
                        lastObservations
                            .GroupBy(
                                run => run.RuntimeIndexStatusLabel,
                                StringComparer.OrdinalIgnoreCase)
                            .OrderBy(
                                group => group.Key,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(
                                group =>
                                    $"{group.Key}:{group.Count()}"));

                var progressSignature =
                    string.Join(
                        "|",
                        lastObservations.Count,
                        sharedStatusProgressSignature,
                        runtimeIndexStatusProgressSignature,
                        lastObservations.Count(
                            run => !string.IsNullOrWhiteSpace(
                                run.SharedRun.AssignedRuntimeInstanceId)),
                        lastObservations.Count(
                            run => !string.IsNullOrWhiteSpace(
                                run.SharedRun.LocalRunId)),
                        lastObservations.Count(
                            run => !string.IsNullOrWhiteSpace(
                                run.ResolvedExecutionId)),
                        lastObservations.Count(run => run.IsCompleted),
                        observation.MaximumObservedPodCount,
                        observation.MaximumObservedRuntimeCount,
                        observation.MaximumSharedQueuedRunCount);

                var nowUtc =
                    DateTimeOffset.UtcNow;

                var durableDagProgressObserved =
                    false;

                if (nowUtc >= nextDurableDagProgressProbeAtUtc)
                {
                    var submittedExecutionIds =
                        lastObservations
                            .Where(
                                run =>
                                    !run.IsCompleted &&
                                    (useDagExecutionCompletion ||
                                     string.Equals(
                                         run.RuntimeIndexStatus,
                                         "running",
                                         StringComparison.OrdinalIgnoreCase)) &&
                                    !string.IsNullOrWhiteSpace(
                                        run.ResolvedExecutionId))
                            .Select(run => run.ResolvedExecutionId!)
                            .ToArray();

                    var unfinishedExecutionIds =
                        Array.Empty<string>();

                    if (useDagExecutionCompletion)
                    {
                        // A parked parent is intentionally absent from ListUnfinishedAsync. Child and nested-child
                        // executions, however, remain normal active runtime work. Include their durable ExecutionIds
                        // in the watchdog so real child progress prevents a false parent-only no-progress timeout.
                        var unfinishedRuntimeRuns =
                            await runExecutionIndex
                                .ListUnfinishedAsync()
                                .ConfigureAwait(false);

                        lastUnfinishedRuntimeRuns =
                            unfinishedRuntimeRuns
                                .Where(
                                    entry =>
                                        string.Equals(
                                            entry.ExecutionContextSnapshot.TenantId,
                                            tenantId,
                                            StringComparison.Ordinal))
                                .ToArray();

                        unfinishedExecutionIds =
                            lastUnfinishedRuntimeRuns
                                .Where(
                                    entry =>
                                        !string.IsNullOrWhiteSpace(
                                            entry.ExecutionId))
                                .Select(entry => entry.ExecutionId!)
                                .ToArray();
                    }

                    var activeExecutionIds =
                        submittedExecutionIds
                            .Concat(unfinishedExecutionIds)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(
                                executionId => executionId,
                                StringComparer.Ordinal)
                            .ToArray();

                    var durableDagProgressSignature =
                        await ProductionRecoveryWaitHelpers
                            .ReadDurableDagProgressSignatureAsync(
                                dagStore,
                                activeExecutionIds)
                            .ConfigureAwait(false);

                    if (lastDurableDagProgressSignature is not null &&
                        !StringComparer.Ordinal.Equals(
                            durableDagProgressSignature,
                            lastDurableDagProgressSignature))
                    {
                        durableDagProgressObserved = true;
                    }

                    lastDurableDagProgressSignature =
                        durableDagProgressSignature;

                    nextDurableDagProgressProbeAtUtc =
                        nowUtc.AddSeconds(5);
                }

                if (!StringComparer.Ordinal.Equals(
                        progressSignature,
                        lastProgressSignature) ||
                    durableDagProgressObserved)
                {
                    lastProgressSignature = progressSignature;
                    lastProgressAtUtc = nowUtc;
                }
                else if (
                    nowUtc - lastProgressAtUtc >=
                    noProgressTimeout)
                {
                    var sharedStatusBreakdown =
                        lastObservations.Count == 0
                            ? "(none)"
                            : string.Join(
                                ",",
                                lastObservations
                                    .GroupBy(run => run.SharedRun.Status)
                                    .OrderBy(
                                        group => group.Key.ToString(),
                                        StringComparer.Ordinal)
                                    .Select(
                                        group =>
                                            $"{group.Key}:{group.Count()}"));

                    var runtimeIndexStatusBreakdown =
                        lastObservations.Count == 0
                            ? "(none)"
                            : string.Join(
                                ",",
                                lastObservations
                                    .GroupBy(
                                        run => run.RuntimeIndexStatusLabel,
                                        StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(
                                        group => group.Key,
                                        StringComparer.OrdinalIgnoreCase)
                                    .Select(
                                        group =>
                                            $"{group.Key}:{group.Count()}"));

                    var runDiagnostics =
                        lastObservations.Count == 0
                            ? "(no submitted runs visible)"
                            : string.Join(
                                Environment.NewLine,
                                lastObservations
                                    .OrderBy(
                                        run => run.SharedRun.SharedRunId,
                                        StringComparer.Ordinal)
                                    .Select(
                                        run =>
                                            $"SharedRunId='{run.SharedRun.SharedRunId}', SharedRunStatus='{run.SharedRun.Status}', AssignedRuntimeInstanceId='{run.SharedRun.AssignedRuntimeInstanceId ?? string.Empty}', LocalRunId='{run.SharedRun.LocalRunId ?? string.Empty}', SharedRunExecutionId='{run.SharedRun.ExecutionId ?? string.Empty}', RuntimeIndexStatus='{run.RuntimeIndexStatus ?? string.Empty}', RuntimeIndexRuntimeInstanceId='{run.RuntimeIndexRuntimeInstanceId ?? string.Empty}', RuntimeIndexExecutionId='{run.RuntimeIndexExecutionId ?? string.Empty}', RuntimeIndexCompletedAtUtc='{run.RuntimeIndexCompletedAtUtc?.ToString("O") ?? string.Empty}', FailureReason='{run.SharedRun.FailureReason ?? string.Empty}'."));

                    var childAwareProgressDiagnostics =
                        string.Empty;

                    if (useDagExecutionCompletion)
                    {
                        var submittedExecutionIds =
                            lastObservations
                                .Select(run => run.ResolvedExecutionId)
                                .Where(
                                    executionId =>
                                        !string.IsNullOrWhiteSpace(
                                            executionId))
                                .Select(executionId => executionId!)
                                .ToHashSet(StringComparer.Ordinal);

                        var nonRootActiveExecutionIds =
                            lastUnfinishedRuntimeRuns
                                .Where(
                                    entry =>
                                        !string.IsNullOrWhiteSpace(
                                            entry.ExecutionId) &&
                                        !submittedExecutionIds.Contains(
                                            entry.ExecutionId!))
                                .Select(entry => entry.ExecutionId!)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(
                                    executionId => executionId,
                                    StringComparer.Ordinal)
                                .ToArray();

                        var unfinishedStatusBreakdown =
                            lastUnfinishedRuntimeRuns.Count == 0
                                ? "(none)"
                                : string.Join(
                                    ",",
                                    lastUnfinishedRuntimeRuns
                                        .GroupBy(
                                            entry =>
                                                entry.Status ??
                                                "(status-missing)",
                                            StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(
                                            group => group.Key,
                                            StringComparer.OrdinalIgnoreCase)
                                        .Select(
                                            group =>
                                                $"{group.Key}:{group.Count()}"));

                        childAwareProgressDiagnostics =
                            $"DurableDagProgressSignature='{lastDurableDagProgressSignature ?? "(not-probed)"}', " +
                            $"UnfinishedRuntimeRunCount='{lastUnfinishedRuntimeRuns.Count}', " +
                            $"UnfinishedRuntimeStatusBreakdown='{unfinishedStatusBreakdown}', " +
                            $"NonRootActiveExecutionCount='{nonRootActiveExecutionIds.Length}', " +
                            $"NonRootActiveExecutionIds='{string.Join(",", nonRootActiveExecutionIds)}'.";
                    }

                    throw new TimeoutException(
                        $"The bounded-capacity workload made no durable progress for '{noProgressTimeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastObservations.Count}', SharedStatusBreakdown='{sharedStatusBreakdown}', RuntimeIndexStatusBreakdown='{runtimeIndexStatusBreakdown}'." +
                        (string.IsNullOrWhiteSpace(childAwareProgressDiagnostics)
                            ? string.Empty
                            : Environment.NewLine + childAwareProgressDiagnostics) +
                        Environment.NewLine +
                        runDiagnostics);
                }

                var sharedRunTerminalFailure =
                    lastObservations.FirstOrDefault(
                        run =>
                            run.SharedRun.Status == AiSharedRunStatus.Failed ||
                            run.SharedRun.Status == AiSharedRunStatus.Rejected ||
                            run.SharedRun.Status == AiSharedRunStatus.Cancelled);

                if (sharedRunTerminalFailure is not null)
                {
                    throw new InvalidOperationException(
                        $"A bounded-capacity shared run terminated unsuccessfully. SharedRunId='{sharedRunTerminalFailure.SharedRun.SharedRunId}', SharedRunStatus='{sharedRunTerminalFailure.SharedRun.Status}', RuntimeIndexStatus='{sharedRunTerminalFailure.RuntimeIndexStatus ?? string.Empty}', FailureReason='{sharedRunTerminalFailure.SharedRun.FailureReason}'.");
                }

                var runtimeIndexTerminalFailure =
                    lastObservations.FirstOrDefault(
                        run => run.IsRuntimeIndexTerminalFailure);

                if (runtimeIndexTerminalFailure is not null)
                {
                    throw new InvalidOperationException(
                        $"A bounded-capacity runtime run terminated unsuccessfully. SharedRunId='{runtimeIndexTerminalFailure.SharedRun.SharedRunId}', LocalRunId='{runtimeIndexTerminalFailure.SharedRun.LocalRunId}', RuntimeIndexStatus='{runtimeIndexTerminalFailure.RuntimeIndexStatus}', ExecutionId='{runtimeIndexTerminalFailure.ResolvedExecutionId ?? string.Empty}', FailureReason='{runtimeIndexTerminalFailure.SharedRun.FailureReason ?? string.Empty}'.");
                }

                observation.ThrowIfViolated();

                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            observation.ThrowIfViolated();

            var completedCount =
                lastObservations.Count(run => run.IsCompleted);

            throw new TimeoutException(
                $"The bounded-capacity workload did not complete within '{timeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastObservations.Count}', RuntimeIndexCompleted='{completedCount}'.");
        }

        private static async Task<BoundedCapacityRunObservation>
            ReadBoundedCapacityRunObservationAsync(
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IAiDagExecutionStore dagStore,
                AiSharedRunRecord sharedRun,
                bool useDagExecutionCompletion)
        {
            AiRuntimeRunExecutionIndexEntry? indexEntry = null;

            if (!string.IsNullOrWhiteSpace(sharedRun.LocalRunId))
            {
                indexEntry = await runExecutionIndex
                    .GetAsync(sharedRun.LocalRunId)
                    .ConfigureAwait(false);
            }

            var resolvedExecutionId =
                !string.IsNullOrWhiteSpace(sharedRun.ExecutionId)
                    ? sharedRun.ExecutionId
                    : indexEntry?.ExecutionId;

            var dagRecord =
                !useDagExecutionCompletion ||
                string.IsNullOrWhiteSpace(resolvedExecutionId)
                    ? null
                    : await dagStore
                        .GetRecordAsync(resolvedExecutionId)
                        .ConfigureAwait(false);

            return new BoundedCapacityRunObservation(
                sharedRun,
                RuntimeIndexExists: indexEntry is not null,
                RuntimeIndexStatus: indexEntry?.Status,
                RuntimeIndexRuntimeInstanceId: indexEntry?.RuntimeInstanceId,
                RuntimeIndexExecutionId: indexEntry?.ExecutionId,
                RuntimeIndexCompletedAtUtc: indexEntry?.CompletedAtUtc,
                DagExecutionStatus: dagRecord?.Status,
                UseDagExecutionCompletion: useDagExecutionCompletion);
        }

        private sealed record BoundedCapacityCompletedRun(
            AiSharedRunRecord SharedRun,
            string LocalRunId,
            string ExecutionId,
            DateTimeOffset? CompletedAtUtc);

        private sealed record BoundedCapacityRunObservation(
            AiSharedRunRecord SharedRun,
            bool RuntimeIndexExists,
            string? RuntimeIndexStatus,
            string? RuntimeIndexRuntimeInstanceId,
            string? RuntimeIndexExecutionId,
            DateTimeOffset? RuntimeIndexCompletedAtUtc,
            AiExecutionStatus? DagExecutionStatus,
            bool UseDagExecutionCompletion)
        {
            public string? ResolvedExecutionId =>
                !string.IsNullOrWhiteSpace(SharedRun.ExecutionId)
                    ? SharedRun.ExecutionId
                    : RuntimeIndexExecutionId;

            public string RuntimeIndexStatusLabel =>
                RuntimeIndexExists
                    ? RuntimeIndexStatus ?? "(status-missing)"
                    : string.IsNullOrWhiteSpace(SharedRun.LocalRunId)
                        ? "(local-run-id-missing)"
                        : "(index-missing)";

            public bool IsCompleted =>
                UseDagExecutionCompletion
                    ? !string.IsNullOrWhiteSpace(ResolvedExecutionId) &&
                      DagExecutionStatus == AiExecutionStatus.Completed
                    : RuntimeIndexExists &&
                      !string.IsNullOrWhiteSpace(
                          SharedRun.AssignedRuntimeInstanceId) &&
                      !string.IsNullOrWhiteSpace(SharedRun.LocalRunId) &&
                      string.Equals(
                          RuntimeIndexStatus,
                          "completed",
                          StringComparison.OrdinalIgnoreCase) &&
                      !string.IsNullOrWhiteSpace(ResolvedExecutionId);

            public bool IsRuntimeIndexTerminalFailure =>
                RuntimeIndexExists &&
                (
                    string.Equals(
                        RuntimeIndexStatus,
                        "failed",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        RuntimeIndexStatus,
                        "rejected",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        RuntimeIndexStatus,
                        "cancelled",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        RuntimeIndexStatus,
                        "canceled",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static async Task ObserveBoundedCapacityAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            string controlPlaneId,
            string poolId,
            string tenantId,
            BoundedCapacityMachineLimitObservation observation,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var runtimes =
                        (await registry
                                .ListAsync(
                                    includeStopped: false,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false))
                            .Where(
                                runtime => StringComparer.Ordinal.Equals(
                                    runtime.PoolId,
                                    poolId))
                            .ToArray();

                    var sharedRuns =
                        (await sharedRunStore
                                .ListAsync(
                                    includeCancelled: true,
                                    includeCompleted: true,
                                    includeFailed: true,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false))
                            .Where(
                                run =>
                                    StringComparer.Ordinal.Equals(
                                        run.ControlPlaneId,
                                        controlPlaneId) &&
                                    StringComparer.Ordinal.Equals(
                                        run.ExecutionContextSnapshot.TenantId,
                                        tenantId))
                            .ToArray();

                    var queuedRunCount =
                        (await sharedQueue
                                .ListAsync(
                                    includeTerminal: false,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false))
                            .Count(
                                item => StringComparer.Ordinal.Equals(
                                    item.ControlPlaneId,
                                    controlPlaneId));

                    IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>
                        scaleOutRequests =
                            Array.Empty<AiRuntimeScaleOutRequestRecord>();

                    if (!observation.ScaleOutSnapshotCapturedAtFullCapacity)
                    {
                        scaleOutRequests =
                            await scaleOutRequestStore
                                .ListAsync(
                                    new AiRuntimeScaleOutRequestQuery
                                    {
                                        ControlPlaneId = controlPlaneId,
                                        TenantId = tenantId,
                                        MaxResults = 1000,
                                        IncludeExpired = true
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }

                    observation.Observe(
                        runtimes,
                        sharedRuns,
                        queuedRunCount,
                        scaleOutRequests);

                    await Task.Delay(
                            TimeSpan.FromMilliseconds(500),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                observation.RecordViolation(
                    $"Capacity observer failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private sealed class BoundedCapacityMachineLimitObservation
        {
            private readonly int maximumPodCount;
            private readonly int runtimeCountPerPod;
            private readonly int maximumRuntimeCapacity;
            private readonly Dictionary<string, HashSet<string>> dispatchBindingsBySharedRunId =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, AiRuntimeScaleOutRequestRecord> scaleOutRequestsById =
                new(StringComparer.Ordinal);
            private readonly object violationSync = new();
            private readonly object intentionalFailureSync = new();
            private readonly HashSet<string> intentionallyFailedHostIds =
                new(StringComparer.Ordinal);
            private readonly HashSet<string> intentionallyFailedRuntimeInstanceIds =
                new(StringComparer.Ordinal);
            private readonly List<string> violations = new();

            public BoundedCapacityMachineLimitObservation(
                int maximumPodCount,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity)
            {
                this.maximumPodCount = maximumPodCount;
                this.runtimeCountPerPod = runtimeCountPerPod;
                this.maximumRuntimeCapacity = maximumRuntimeCapacity;
            }

            public int MaximumObservedPodCount { get; private set; }

            public int MaximumObservedRuntimeCount { get; private set; }

            public int MaximumSharedQueuedRunCount { get; private set; }

            public bool ObservedFullCapacityWithQueuedRuns { get; private set; }

            public bool ScaleOutSnapshotCapturedAtFullCapacity { get; private set; }

            public IReadOnlyCollection<string> Violations
            {
                get
                {
                    lock (violationSync)
                    {
                        return violations.ToArray();
                    }
                }
            }

            public void MarkIntentionalFailedRuntimeInstance(
                string runtimeInstanceId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    runtimeInstanceId);

                lock (intentionalFailureSync)
                {
                    intentionallyFailedRuntimeInstanceIds.Add(
                        runtimeInstanceId);
                }
            }

            public void MarkIntentionalFailedPodGeneration(
                string hostId,
                IReadOnlyCollection<string> runtimeInstanceIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
                ArgumentNullException.ThrowIfNull(runtimeInstanceIds);

                lock (intentionalFailureSync)
                {
                    intentionallyFailedHostIds.Add(hostId);

                    foreach (var runtimeInstanceId in runtimeInstanceIds)
                    {
                        if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
                        {
                            intentionallyFailedRuntimeInstanceIds.Add(
                                runtimeInstanceId);
                        }
                    }
                }
            }

            public void Observe(
                IReadOnlyCollection<AiRuntimeInstanceSnapshot> runtimes,
                IReadOnlyCollection<AiSharedRunRecord> sharedRuns,
                int queuedRunCount,
                IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> scaleOutRequests)
            {
                HashSet<string> failedHostIds;
                HashSet<string> failedRuntimeInstanceIds;

                lock (intentionalFailureSync)
                {
                    failedHostIds =
                        new HashSet<string>(
                            intentionallyFailedHostIds,
                            StringComparer.Ordinal);
                    failedRuntimeInstanceIds =
                        new HashSet<string>(
                            intentionallyFailedRuntimeInstanceIds,
                            StringComparer.Ordinal);
                }

                var activeRuntimes =
                    runtimes
                        .Where(
                            runtime =>
                                !failedRuntimeInstanceIds.Contains(
                                    runtime.RuntimeInstanceId) &&
                                (string.IsNullOrWhiteSpace(runtime.HostId) ||
                                 !failedHostIds.Contains(runtime.HostId!)))
                        .ToArray();

                var runtimesByHost =
                    activeRuntimes
                        .Where(runtime => !string.IsNullOrWhiteSpace(runtime.HostId))
                        .GroupBy(
                            runtime => runtime.HostId!,
                            StringComparer.Ordinal)
                        .ToArray();

                var podCount =
                    runtimesByHost.Length;

                MaximumObservedPodCount =
                    Math.Max(MaximumObservedPodCount, podCount);

                MaximumObservedRuntimeCount =
                    Math.Max(MaximumObservedRuntimeCount, activeRuntimes.Length);

                MaximumSharedQueuedRunCount =
                    Math.Max(MaximumSharedQueuedRunCount, queuedRunCount);

                if (podCount > maximumPodCount)
                {
                    RecordViolation(
                        $"Observed Pod count '{podCount}' exceeded configured maximum '{maximumPodCount}'.");
                }

                if (activeRuntimes.Length > maximumRuntimeCapacity)
                {
                    RecordViolation(
                        $"Observed runtime count '{activeRuntimes.Length}' exceeded configured maximum '{maximumRuntimeCapacity}'.");
                }

                foreach (var host in runtimesByHost)
                {
                    if (host.Count() > runtimeCountPerPod)
                    {
                        RecordViolation(
                            $"HostId='{host.Key}' exposed '{host.Count()}' runtimes; maximum per Pod is '{runtimeCountPerPod}'.");
                    }
                }

                if (activeRuntimes.Length == maximumRuntimeCapacity && queuedRunCount > 0)
                {
                    ObservedFullCapacityWithQueuedRuns = true;
                }

                foreach (var scaleOutRequest in scaleOutRequests)
                {
                    scaleOutRequestsById[scaleOutRequest.RequestId] =
                        scaleOutRequest;
                }

                if (activeRuntimes.Length == maximumRuntimeCapacity &&
                    scaleOutRequestsById.Count > 0)
                {
                    ScaleOutSnapshotCapturedAtFullCapacity = true;
                }

                foreach (var run in sharedRuns)
                {
                    ObserveDispatchBinding(run);
                }
            }

            public void ObserveFinalDispatchBindings(
                IEnumerable<AiSharedRunRecord> finalRuns)
            {
                foreach (var run in finalRuns)
                {
                    ObserveDispatchBinding(run);
                }
            }

            public IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>
                GetScaleOutRequests(
                    IReadOnlySet<string> submittedSharedRunIds)
            {
                return scaleOutRequestsById
                    .Values
                    .Where(
                        request => submittedSharedRunIds.Contains(
                            request.SharedRunId))
                    .OrderBy(
                        request => request.RequestId,
                        StringComparer.Ordinal)
                    .ToArray();
            }

            public IReadOnlyDictionary<string, IReadOnlyCollection<string>>
                GetDuplicateDispatchBindings(
                    IReadOnlySet<string> submittedSharedRunIds)
            {
                return submittedSharedRunIds
                    .Where(
                        sharedRunId =>
                            dispatchBindingsBySharedRunId.ContainsKey(
                                sharedRunId))
                    .ToDictionary(
                        sharedRunId => sharedRunId,
                        sharedRunId =>
                            (IReadOnlyCollection<string>)
                                dispatchBindingsBySharedRunId[
                                    sharedRunId]
                                    .OrderBy(
                                        binding => binding,
                                        StringComparer.Ordinal)
                                    .ToArray(),
                        StringComparer.Ordinal);
            }

            public void RecordViolation(string violation)
            {
                lock (violationSync)
                {
                    if (!violations.Contains(violation, StringComparer.Ordinal))
                    {
                        violations.Add(violation);
                    }
                }
            }

            public void ThrowIfViolated()
            {
                string[] currentViolations;

                lock (violationSync)
                {
                    currentViolations = violations.ToArray();
                }

                if (currentViolations.Length == 0)
                {
                    return;
                }

                throw new InvalidOperationException(
                    string.Concat(
                        "The bounded-capacity observer detected an invariant violation: ",
                        string.Join(" | ", currentViolations)));
            }

            private void ObserveDispatchBinding(
                AiSharedRunRecord run)
            {
                if (string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId) ||
                    string.IsNullOrWhiteSpace(run.LocalRunId))
                {
                    return;
                }

                if (!dispatchBindingsBySharedRunId.TryGetValue(
                        run.SharedRunId,
                        out var bindings))
                {
                    bindings = new HashSet<string>(StringComparer.Ordinal);
                    dispatchBindingsBySharedRunId.Add(
                        run.SharedRunId,
                        bindings);
                }

                bindings.Add(
                    string.Concat(
                        run.AssignedRuntimeInstanceId,
                        "|",
                        run.LocalRunId));
            }
        }

        /// <inheritdoc />
        protected override Task OnCrashRecoveryScenarioStartingAsync(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            states.TryAdd(
                controlPlaneId,
                new RuntimePoolAllInOneFailureState());

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override async Task OnCrashRecoveryScenarioCompletedAsync(
            string controlPlaneId)
        {
            await CleanupControlPlanePodsAsync(
                    controlPlaneId)
                .ConfigureAwait(false);
        }
        protected string ResolvePoolId(
            string controlPlaneId)
        {
            return RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                profile.PoolIdPrefix,
                controlPlaneId);
        }

        protected Task AssertBoundedPhysicalPodCountAsync(
            RuntimePoolAllInOneFailureState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            return infrastructure.AssertBoundedPhysicalPodCountAsync(
                state.GetTrackedPods(),
                profile.CrashRecoveryPlan.MaximumPodCount);
        }



        protected static Task<HashSet<string>> WaitForActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            int expectedHostCount,
            TimeSpan timeout)
        {
            return KubernetesRuntimePoolProductionTopology.WaitForActiveHostIdsAsync(
                registry,
                poolId,
                expectedHostCount,
                timeout);
        }



        protected static Task AssertExactSiblingsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string hostId,
            IReadOnlyCollection<string> siblingRuntimeInstanceIds,
            TimeSpan timeout)
        {
            return KubernetesRuntimePoolProductionTopology.AssertExactSiblingsRemainReadyAsync(
                registry,
                hostId,
                siblingRuntimeInstanceIds,
                timeout);
        }

        protected static Task AssertSurvivingHostsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            IReadOnlySet<string> survivingHostIds,
            TimeSpan timeout)
        {
            return KubernetesRuntimePoolProductionTopology.AssertSurvivingHostsRemainReadyAsync(
                registry,
                poolId,
                survivingHostIds,
                timeout);
        }

        protected static Task<AiRuntimeInstanceSnapshot>
            GetRequiredRuntimeSnapshotAsync(
                IAiRuntimeInstanceRegistry registry,
                string runtimeInstanceId)
        {
            return KubernetesRuntimePoolProductionTopology.GetRequiredRuntimeSnapshotAsync(
                registry,
                runtimeInstanceId);
        }

        protected static void AssertRuntimePoolIdentity(
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            KubernetesRuntimePoolProductionTopology.AssertRuntimePoolIdentity(
                snapshot,
                poolId);
        }
        /// <summary>
        /// Performs a final best-effort cleanup for any scenario state left after the test method exits.
        /// </summary>
        /// <returns>A task that completes when all remaining tracked pools have been cleaned.</returns>
        protected async Task CleanupAllTrackedPodsAsync()
        {
            var controlPlaneIds =
                states.Keys.ToArray();

            var failures =
                new List<Exception>();

            foreach (var controlPlaneId in controlPlaneIds)
            {
                try
                {
                    await CleanupControlPlanePodsAsync(
                            controlPlaneId)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "At least one Kubernetes Runtime Pool scenario could not clean all of its Pods.",
                    failures);
            }
        }

        private async Task CleanupControlPlanePodsAsync(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var poolId = ResolvePoolId(controlPlaneId);

            states.TryGetValue(
                controlPlaneId,
                out var state);

            var trackedPods =
                state?.GetTrackedPods()
                ?? Array.Empty<TrackedPod>();

            await infrastructure
                .CleanupControlPlanePodsAsync(
                    controlPlaneId,
                    poolId,
                    trackedPods)
                .ConfigureAwait(false);

            states.TryRemove(
                controlPlaneId,
                out _);
        }



        protected static Task<KubectlResult> RunKubectlAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            return KubernetesRuntimePoolProductionInfrastructure.RunKubectlAsync(
                cancellationToken,
                arguments);
        }
        protected sealed class RuntimePoolAllInOneFailureState
        {
            private readonly TaskCompletionSource<bool> runtimeFailureCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ConcurrentDictionary<TrackedPod, byte> trackedPods =
                new();

            public Task RuntimeFailureCompletion =>
                runtimeFailureCompletion.Task;

            public string RuntimeFailureHostId { get; private set; } =
                string.Empty;

            public void SetRuntimeFailureHostId(
                string hostId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
                RuntimeFailureHostId = hostId;
            }

            public void CompleteRuntimeFailure()
            {
                runtimeFailureCompletion.TrySetResult(true);
            }

            public void FailRuntimeFailure(
                Exception exception)
            {
                runtimeFailureCompletion.TrySetException(exception);
            }

            public async Task TrackCurrentPoolPodsAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId)
            {
                var snapshots =
                    await registry
                        .ListAsync(includeStopped: true)
                        .ConfigureAwait(false);

                foreach (var snapshot in snapshots.Where(
                             snapshot =>
                                 StringComparer.Ordinal.Equals(
                                     snapshot.PoolId,
                                     poolId) &&
                                 !string.IsNullOrWhiteSpace(
                                     snapshot.KubernetesNamespace) &&
                                 !string.IsNullOrWhiteSpace(
                                     snapshot.KubernetesPodName)))
                {
                    TrackPod(
                        snapshot.KubernetesNamespace!,
                        snapshot.KubernetesPodName!);
                }
            }

            public void TrackPod(
                string @namespace,
                string podName)
            {
                trackedPods.TryAdd(
                    new TrackedPod(
                        @namespace,
                        podName),
                    0);
            }

            public IReadOnlyCollection<TrackedPod> GetTrackedPods()
            {
                return trackedPods.Keys.ToArray();
            }
        }
        protected internal sealed record TrackedPod(
            string Namespace,
            string PodName);

        protected internal sealed record KubectlResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);
    }
}
