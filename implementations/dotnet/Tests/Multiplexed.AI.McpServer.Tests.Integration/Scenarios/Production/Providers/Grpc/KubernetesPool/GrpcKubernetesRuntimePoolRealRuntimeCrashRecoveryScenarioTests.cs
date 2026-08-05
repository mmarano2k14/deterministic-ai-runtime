using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
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
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;
using Xunit.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Stores;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.KubernetesPool
{
    /// <summary>
    /// Proves runtime-process and whole-Pod recovery in one real bounded gRPC Kubernetes Runtime Pool scenario.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GrpcKubernetesRuntimePoolCrashRecoveryCollection
    {
        /// <summary>
        /// Gets the non-parallel collection name used by the destructive Kubernetes proof.
        /// </summary>
        public const string Name =
            "gRPC Kubernetes Runtime Pool crash recovery collection";
    }

    /// <summary>
    /// Provides shared real-Kubernetes process and Pod failure authority for gRPC Runtime Pool scenarios.
    /// </summary>
    public abstract class GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase :
        ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper output;
        private readonly IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile;
        private readonly ConcurrentDictionary<string, RuntimePoolAllInOneFailureState> states =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the bounded Runtime Pool scenario profile used by this proof.
        /// </summary>
        protected IRuntimePoolCrashRecoveryScenarioRuntimeProfile RuntimePoolProfile =>
            profile;

        /// <summary>
        /// Initializes a real gRPC Kubernetes Runtime Pool crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The bounded Runtime Pool scenario profile.</param>
        protected GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase(
            ITestOutputHelper output,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
            : base(
                output,
                profile)
        {
            this.output = output;
            this.profile = profile;
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
                executionCycleCount);
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
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
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
                    ? "grpc-kubernetes-runtime-pool-bounded-capacity-pod-failure-machine-limit"
                    : "grpc-kubernetes-runtime-pool-bounded-capacity-machine-limit";

            var controlPlaneIdPrefix =
                injectPodFailure
                    ? "grpc-kubernetes-runtime-pool-bounded-capacity-pod-failure"
                    : "grpc-kubernetes-runtime-pool-bounded-capacity-machine-limit";

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
                    "# GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY TIMING SUMMARY");

                foreach (var phaseTiming in phaseTimings)
                {
                    output.WriteLine(
                        $"  - {phaseTiming.Name}: {phaseTiming.Duration}");
                }

                output.WriteLine(
                    $"  - Scenario total: {totalStopwatch.Elapsed}");
            }

            output.WriteLine(
                "# GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY PRODUCTION PROOF");
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
                    IReadOnlyList<AiSharedRuntimeControllerResult> submissionResults;

                var submissionStopwatch =
                    Stopwatch.StartNew();

                try
                {
                    using var submissionGate =
                        new SemaphoreSlim(
                            maximumConcurrentMcpSubmissions,
                            maximumConcurrentMcpSubmissions);

                    async Task<AiSharedRuntimeControllerResult>
                        SubmitSingleRunWithBackpressureAsync(
                            AiSharedRuntimeControllerRequest request)
                    {
                        var retryDelay =
                            TimeSpan.FromMilliseconds(100);

                        for (var attempt = 1;
                             attempt <= maximumAdmissionAttemptCount;
                             attempt++)
                        {
                            try
                            {
                                var result =
                                    await mcp
                                        .SubmitManyRunsAsync(
                                            request,
                                            1)
                                        .ConfigureAwait(false);

                                return Assert.Single(result);
                            }
                            catch (HttpRequestException exception)
                                when (
                                    exception.StatusCode ==
                                        HttpStatusCode.TooManyRequests
                                    && attempt <
                                        maximumAdmissionAttemptCount)
                            {
                                Interlocked.Increment(
                                    ref admissionTooManyRequestsRetryCount);

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

                        throw new TimeoutException(
                            "MCP QueueFirst admission remained throttled " +
                            $"after '{maximumAdmissionAttemptCount}' attempts.");
                    }

                    var submissionTasks =
                        Enumerable
                            .Range(1, submissionIterationCount)
                            .SelectMany(
                                iteration =>
                                {
                                    var pipelineName =
                                        string.Concat(
                                            scenario.Name,
                                            "-wave-",
                                            iteration.ToString(
                                                "000",
                                                CultureInfo.InvariantCulture),
                                            "-",
                                            Guid.NewGuid().ToString("N"));

                                    return Enumerable
                                        .Range(1, runsPerIteration)
                                        .Select(
                                            async runNumber =>
                                            {
                                                var request =
                                                    CreateBoundedCapacitySubmitRequest(
                                                        tenant,
                                                        controlPlaneId,
                                                        pipelineName,
                                                        boundedCapacityProfile.RequestedBy,
                                                        boundedCapacityProfile.Source,
                                                        string.Concat(
                                                            controlPlaneId,
                                                            ":wave:",
                                                            iteration.ToString(
                                                                CultureInfo.InvariantCulture),
                                                            ":run:",
                                                            runNumber.ToString(
                                                                CultureInfo.InvariantCulture)));

                                                await submissionGate
                                                    .WaitAsync()
                                                    .ConfigureAwait(false);

                                                try
                                                {
                                                    return await
                                                        SubmitSingleRunWithBackpressureAsync(
                                                            request)
                                                        .ConfigureAwait(false);
                                                }
                                                finally
                                                {
                                                    submissionGate.Release();
                                                }
                                            });
                                })
                            .ToArray();

                    submissionResults =
                        await Task
                            .WhenAll(submissionTasks)
                            .ConfigureAwait(false);

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

                Assert.Equal(
                    submittedRunCount,
                    submissionResults.Count);

                Assert.All(
                    submissionResults,
                    result => Assert.True(
                        result.Success,
                        result.FailureReason ?? result.Message));

                var submittedSharedRunIds =
                    submissionResults
                        .Select(result => result.SharedRunId)
                        .Where(sharedRunId => !string.IsNullOrWhiteSpace(sharedRunId))
                        .Cast<string>()
                        .ToHashSet(StringComparer.Ordinal);

                Assert.Equal(
                    submittedRunCount,
                    submittedSharedRunIds.Count);

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
                    await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
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
                            "step.completed",
                            StringComparison.OrdinalIgnoreCase));

                var dispatchedSharedRunCount =
                    controlPlaneLedgerEntries
                        .Where(
                            entry => entry.EventType.Contains(
                                "remote-shared-run-dispatch.succeeded",
                                StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.CorrelationContext.RunId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .Count();

                Assert.Equal(
                    logicalStepCount,
                    stepCompletedLedgerCount);

                Assert.Equal(
                    submittedRunCount,
                    dispatchedSharedRunCount);

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
                output.WriteLine("# GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY MACHINE LIMIT");
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
                output.WriteLine($"StepCompletedLedgerCount={stepCompletedLedgerCount}");
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
            int executionCycleCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIterationCount);

            if (executionCycleCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(executionCycleCount),
                    executionCycleCount,
                    "The warm-pool reuse proof requires at least two sequential execution cycles.");
            }

            const int stepCount = 50;
            const int maximumAdmissionAttemptCount = 8;

            var runsPerIteration =
                checked(maximumPodCount * runtimeCountPerPod);

            var submittedRunCountPerCycle =
                checked(runsPerIteration * submissionIterationCount);

            var logicalStepCountPerCycle =
                checked(submittedRunCountPerCycle * stepCount);

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
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
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
                        EnableRetention = false
                    }
                };

            var scenario =
                baseScenario with
                {
                    Name =
                        "grpc-kubernetes-runtime-pool-warm-reuse-pod-failure-production",
                    ControlPlaneIdPrefix =
                        "grpc-kubernetes-runtime-pool-warm-reuse-pod-failure",
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

            static void AssertSameIdentitySet(
                IReadOnlySet<string> expected,
                IReadOnlySet<string> actual,
                string proofName)
            {
                var missing =
                    expected
                        .Except(actual, StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                var unexpected =
                    actual
                        .Except(expected, StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                Assert.True(
                    missing.Length == 0 && unexpected.Length == 0,
                    $"{proofName} identity mismatch. Missing='{string.Join(",", missing)}', Unexpected='{string.Join(",", unexpected)}'.");
            }

            await using var dataStoreTrafficObserver =
                await ProductionDataStoreTrafficObserver
                    .StartAsync(output)
                    .ConfigureAwait(false);

            await OnCrashRecoveryScenarioStartingAsync(controlPlaneId)
                .ConfigureAwait(false);

            var totalStopwatch =
                Stopwatch.StartNew();

            output.WriteLine(
                "# GRPC KUBERNETES RUNTIME POOL WARM REUSE PRODUCTION PROOF");
            output.WriteLine(
                "Executive proof: one bounded Kubernetes Runtime Pool executes repeated production cycles, survives one forced busy-Pod deletion per cycle, reuses the surviving and replacement Pods in the next cycle, and cleans physical capacity only after the final cycle.");
            output.WriteLine(string.Empty);
            output.WriteLine("Scenario contract:");
            output.WriteLine("  - [ON] One control plane and one GenericMcpServerTestHost remain alive for every cycle.");
            output.WriteLine("  - [ON] Cycle N+1 starts from the exact final Pod UIDs and runtime identities produced by cycle N.");
            output.WriteLine("  - [ON] No intermediate cycle invokes Runtime Pool cleanup.");
            output.WriteLine("  - [ON] Every cycle force-deletes one fully busy Pod and recovers exactly its assigned work.");
            output.WriteLine("  - [ON] Every run completes 50 steps and passes replay, ledger, trace, and exact recovery-forensics proof.");
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
            output.WriteLine($"  ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"  PoolId='{poolId}'");
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

                using var submissionHttpClient =
                    host.CreateClient();

                submissionHttpClient.Timeout =
                    TimeSpan.FromMinutes(15);

                var submissionMcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            host,
                            submissionHttpClient,
                            boundedCapacityProfile.RequestedBy,
                            tenantId: tenant.TenantId,
                            tenantGroupId: tenant.TenantGroupId)
                        .ConfigureAwait(false);

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

                        AssertSameIdentitySet(
                            previousCycleProof.FinalPodUids,
                            warmStartMembership.PodUids,
                            $"Cycle {cycleNumber} warm Pod reuse");

                        AssertSameIdentitySet(
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
                        var admissionTooManyRequestsRetryCount = 0;

                        async Task<AiSharedRuntimeControllerResult>
                            SubmitSingleRunWithBackpressureAsync(
                                AiSharedRuntimeControllerRequest request)
                        {
                            var retryDelay =
                                TimeSpan.FromMilliseconds(100);

                            for (var attempt = 1;
                                 attempt <= maximumAdmissionAttemptCount;
                                 attempt++)
                            {
                                try
                                {
                                    var result =
                                        await submissionMcp
                                            .SubmitManyRunsAsync(
                                                request,
                                                1)
                                            .ConfigureAwait(false);

                                    return Assert.Single(result);
                                }
                                catch (HttpRequestException exception)
                                    when (
                                        exception.StatusCode ==
                                            HttpStatusCode.TooManyRequests &&
                                        attempt < maximumAdmissionAttemptCount)
                                {
                                    Interlocked.Increment(
                                        ref admissionTooManyRequestsRetryCount);

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

                            throw new TimeoutException(
                                "MCP QueueFirst admission remained throttled " +
                                $"after '{maximumAdmissionAttemptCount}' attempts in warm reuse cycle '{cycleNumber}'.");
                        }

                        using var submissionGate =
                            new SemaphoreSlim(
                                maximumConcurrentMcpSubmissions,
                                maximumConcurrentMcpSubmissions);

                        var submissionTasks =
                            Enumerable
                                .Range(1, submissionIterationCount)
                                .SelectMany(
                                    iteration =>
                                    {
                                        var pipelineName =
                                            string.Concat(
                                                scenario.Name,
                                                "-cycle-",
                                                cycleNumber.ToString(
                                                    "000",
                                                    CultureInfo.InvariantCulture),
                                                "-wave-",
                                                iteration.ToString(
                                                    "000",
                                                    CultureInfo.InvariantCulture),
                                                "-",
                                                Guid.NewGuid().ToString("N"));

                                        return Enumerable
                                            .Range(1, runsPerIteration)
                                            .Select(
                                                async runNumber =>
                                                {
                                                    var request =
                                                        CreateBoundedCapacitySubmitRequest(
                                                            tenant,
                                                            controlPlaneId,
                                                            pipelineName,
                                                            boundedCapacityProfile.RequestedBy,
                                                            boundedCapacityProfile.Source,
                                                            string.Concat(
                                                                controlPlaneId,
                                                                ":cycle:",
                                                                cycleNumber.ToString(
                                                                    CultureInfo.InvariantCulture),
                                                                ":wave:",
                                                                iteration.ToString(
                                                                    CultureInfo.InvariantCulture),
                                                                ":run:",
                                                                runNumber.ToString(
                                                                    CultureInfo.InvariantCulture)));

                                                    await submissionGate
                                                        .WaitAsync()
                                                        .ConfigureAwait(false);

                                                    try
                                                    {
                                                        return await
                                                            SubmitSingleRunWithBackpressureAsync(
                                                                request)
                                                            .ConfigureAwait(false);
                                                    }
                                                    finally
                                                    {
                                                        submissionGate.Release();
                                                    }
                                                });
                                    })
                                .ToArray();

                        var submissionResults =
                            await Task
                                .WhenAll(submissionTasks)
                                .ConfigureAwait(false);

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            submissionResults.Length);

                        Assert.All(
                            submissionResults,
                            result => Assert.True(
                                result.Success,
                                result.FailureReason ?? result.Message));

                        var submittedSharedRunIds =
                            submissionResults
                                .Select(result => result.SharedRunId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
                                .ToHashSet(StringComparer.Ordinal);

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            submittedSharedRunIds.Count);

                        output.WriteLine(
                            $"[{boundedCapacityProfile.LogPrefix} WARM REUSE MCP ADMISSION] " +
                            $"Cycle='{cycleNumber}', " +
                            $"SubmittedRunCount='{submittedSharedRunIds.Count}', " +
                            $"MaximumConcurrentSubmissions='{maximumConcurrentMcpSubmissions}', " +
                            $"TooManyRequestsRetryCount='{Volatile.Read(ref admissionTooManyRequestsRetryCount)}'.");

                        var preFailureMembership =
                            await WaitForBoundedCapacityPoolMembershipAsync(
                                    registry,
                                    poolId,
                                    maximumPodCount,
                                    runtimeCountPerPod,
                                    requireAvailableCapacity: false,
                                    TimeSpan.FromMinutes(10))
                                .ConfigureAwait(false);

                        if (warmStartMembership is not null)
                        {
                            AssertSameIdentitySet(
                                warmStartMembership.PodUids,
                                preFailureMembership.PodUids,
                                $"Cycle {cycleNumber} pre-failure Pod reuse");

                            AssertSameIdentitySet(
                                warmStartMembership.RuntimeInstanceIds,
                                preFailureMembership.RuntimeInstanceIds,
                                $"Cycle {cycleNumber} pre-failure runtime reuse");
                        }

                        var podFailureProof =
                            await InjectBoundedCapacityPodFailureAsync(
                                    host.Services,
                                    registry,
                                    sharedRunStore,
                                    runExecutionIndex,
                                    tenant,
                                    controlPlaneId,
                                    poolId,
                                    runtimeCountPerPod,
                                    maximumRuntimeCapacity,
                                    observation,
                                    TimeSpan.FromMinutes(10))
                                .ConfigureAwait(false);

                        Assert.Contains(
                            podFailureProof.FailedPodUid,
                            preFailureMembership.PodUids);

                        Assert.DoesNotContain(
                            podFailureProof.ReplacementPodUid,
                            preFailureMembership.PodUids);

                        var finalRuns =
                            await WaitForSubmittedRunsToCompleteAsync(
                                    sharedRunStore,
                                    runExecutionIndex,
                                    submittedSharedRunIds,
                                    controlPlaneId,
                                    tenant.TenantId,
                                    observation,
                                    scenario.CompletionTimeout,
                                    TimeSpan.FromMinutes(5))
                                .ConfigureAwait(false);

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
                                    tenantGroupId: tenant.TenantGroupId)
                                .ConfigureAwait(false);

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

                        var unexpectedDuplicateDispatchBindings =
                            duplicateDispatchBindings
                                .Where(
                                    item =>
                                        item.Value.Count > 1 &&
                                        (!podFailureProof.RecoveredSharedRunIds.Contains(item.Key) ||
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
                            runtimeCountPerPod,
                            duplicateDispatchBindings.Count(
                                item =>
                                    podFailureProof.RecoveredSharedRunIds.Contains(item.Key) &&
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
                            preFailureMembership.PodUids
                                .Where(
                                    podUid => !StringComparer.Ordinal.Equals(
                                        podUid,
                                        podFailureProof.FailedPodUid))
                                .Append(podFailureProof.ReplacementPodUid)
                                .ToHashSet(StringComparer.Ordinal);

                        AssertSameIdentitySet(
                            expectedFinalPodUids,
                            finalMembership.PodUids,
                            $"Cycle {cycleNumber} exact replacement Pod topology");

                        var expectedFinalRuntimeInstanceIds =
                            preFailureMembership.RuntimeInstanceIds
                                .Where(
                                    runtimeInstanceId =>
                                        !podFailureProof.FailedRuntimeInstanceIds.Contains(
                                            runtimeInstanceId))
                                .Concat(
                                    podFailureProof.ReplacementRuntimeInstanceIds)
                                .ToHashSet(StringComparer.Ordinal);

                        AssertSameIdentitySet(
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
                                 podFailureProof.RecoveryForensicsIds
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

                            Assert.Equal(
                                podFailureProof.FailureId,
                                exactRecord.RuntimeFailureIncidentId);
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
                            await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
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

                        var assignedRuntimeInstanceIds =
                            finalRuns
                                .Select(run => run.SharedRun.AssignedRuntimeInstanceId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
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
                                    "step.completed",
                                    StringComparison.OrdinalIgnoreCase));

                        var dispatchedSharedRunCount =
                            controlPlaneLedgerEntries
                                .Where(
                                    entry => entry.EventType.Contains(
                                        "remote-shared-run-dispatch.succeeded",
                                        StringComparison.OrdinalIgnoreCase))
                                .Select(entry => entry.CorrelationContext.RunId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.Ordinal)
                                .Count();

                        Assert.Equal(
                            logicalStepCountPerCycle,
                            stepCompletedLedgerCount);

                        Assert.Equal(
                            submittedRunCountPerCycle,
                            dispatchedSharedRunCount);

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
                                podFailureProof.RecoveryForensicsIds,
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
                        output.WriteLine($"FailedPodUid='{podFailureProof.FailedPodUid}'");
                        output.WriteLine($"ReplacementPodUid='{podFailureProof.ReplacementPodUid}'");
                        output.WriteLine($"RecoveredSharedRunCount='{podFailureProof.RecoveredSharedRunIds.Count}'");
                        output.WriteLine($"ReplayProofCount='{replayProofs.Count}'");
                        output.WriteLine($"StepCompletedLedgerCount='{stepCompletedLedgerCount}'");
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

                    AssertSameIdentitySet(
                        previous.FinalPodUids,
                        current.WarmStartPodUids,
                        $"Cycle {previous.CycleNumber} to {current.CycleNumber} Pod reuse");

                    AssertSameIdentitySet(
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

                Assert.Equal(
                    checked(runtimeCountPerPod * executionCycleCount),
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
                    "# GRPC KUBERNETES RUNTIME POOL WARM REUSE PRODUCTION SUMMARY");
                output.WriteLine($"ExecutionCycleCount='{executionCycleCount}'");
                output.WriteLine($"MaximumConfiguredPodCount='{maximumPodCount}'");
                output.WriteLine($"RuntimeCountPerPod='{runtimeCountPerPod}'");
                output.WriteLine($"MaximumRuntimeCapacity='{maximumRuntimeCapacity}'");
                output.WriteLine($"TotalSubmittedRunCount='{totalSubmittedRunCount}'");
                output.WriteLine($"TotalCompletedRunCount='{allExecutionIds.Length}'");
                output.WriteLine($"TotalLogicalStepCount='{totalLogicalStepCount}'");
                output.WriteLine($"ForcedPodDeletionCount='{executionCycleCount}'");
                output.WriteLine($"RecoveredSharedRunCount='{runtimeCountPerPod * executionCycleCount}'");
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

        private async Task<BoundedCapacityPodFailureProof>
            InjectBoundedCapacityPodFailureAsync(
                IServiceProvider services,
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                ProductionTenantScenarioDefinition tenant,
                string controlPlaneId,
                string poolId,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity,
                BoundedCapacityMachineLimitObservation observation,
                TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRuntimeCapacity);
            ArgumentNullException.ThrowIfNull(observation);

            var target =
                await WaitForBoundedCapacityBusyPodFailureTargetAsync(
                        registry,
                        sharedRunStore,
                        runExecutionIndex,
                        controlPlaneId,
                        poolId,
                        tenant.TenantId,
                        runtimeCountPerPod,
                        maximumRuntimeCapacity,
                        timeout)
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
                                "mcp-grpc-kubernetes-runtime-pool-bounded-capacity-scenario",
                            FailureMessage =
                                "Forced busy Kubernetes Runtime Pool Pod deletion in the bounded-capacity recovery proof.",
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
            Assert.Equal(runtimeCountPerPod, recoveryExecution.CandidateCount);
            Assert.Equal(runtimeCountPerPod, recoveryExecution.AcceptedCount);
            Assert.Equal(runtimeCountPerPod, recoveryExecution.ChangedCount);
            Assert.Equal(0, recoveryExecution.RejectedCount);

            var recoveredSharedRunIds =
                recoveryExecution.Outcomes
                    .Where(outcome => outcome.Transition.Accepted)
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
                $"AcceptedCount='{recoveryExecution.AcceptedCount}', " +
                $"ChangedCount='{recoveryExecution.ChangedCount}', " +
                $"RejectedCount='{recoveryExecution.RejectedCount}'.");

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

        private static async Task<BoundedCapacityBusyPodFailureTarget>
            WaitForBoundedCapacityBusyPodFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                string controlPlaneId,
                string poolId,
                string tenantId,
                int runtimeCountPerPod,
                int maximumRuntimeCapacity,
                TimeSpan timeout)
        {
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
                                group => group.All(
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
                            (await sharedRunStore
                                    .ListAsync(
                                        includeCancelled: true,
                                        includeCompleted: true,
                                        includeFailed: true)
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
                                                    run)))
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

        private static AiRuntimeHostStartRequest
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
                ProviderName = "grpc",
                TransportName = "grpc",
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
                TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerPod);

            var expectedRuntimeCount =
                checked(expectedPodCount * runtimeCountPerPod);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            var lastPodCount = 0;
            var lastRuntimeCount = 0;
            var lastReadyRuntimeCount = 0;
            var lastAvailableRuntimeCount = 0;

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
                        "[GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY NO-RECOVERY FORENSICS BACKPRESSURE] " +
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

        private async Task CaptureBoundedCapacityFailureDiagnosticsAsync(
            string controlPlaneId,
            string poolId,
            Exception exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(exception);

            output.WriteLine(string.Empty);
            output.WriteLine(
                "# GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY FAILURE DIAGNOSTICS");
            output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"PoolId='{poolId}'");
            output.WriteLine($"ExceptionType='{exception.GetType().FullName}'");
            output.WriteLine($"ExceptionMessage='{exception.Message}'");

            states.TryGetValue(
                controlPlaneId,
                out var state);

            var trackedPods =
                state?.GetTrackedPods()
                ?? Array.Empty<TrackedPod>();

            IReadOnlyCollection<TrackedPod> discoveredPods;

            try
            {
                discoveredPods =
                    await DiscoverPoolPodsAsync(poolId)
                        .ConfigureAwait(false);
            }
            catch (Exception discoveryException)
            {
                output.WriteLine(
                    $"[DIAGNOSTIC DISCOVERY WARNING] Message='{discoveryException.Message}'.");

                discoveredPods =
                    Array.Empty<TrackedPod>();
            }

            var diagnosticPods =
                trackedPods
                    .Concat(discoveredPods)
                    .Distinct()
                    .OrderBy(
                        pod => pod.Namespace,
                        StringComparer.Ordinal)
                    .ThenBy(
                        pod => pod.PodName,
                        StringComparer.Ordinal)
                    .ToArray();

            output.WriteLine($"TrackedPodCount='{trackedPods.Count}'");
            output.WriteLine($"DiscoveredPodCount='{discoveredPods.Count}'");
            output.WriteLine($"DiagnosticPodCount='{diagnosticPods.Length}'");

            await WriteKubectlDiagnosticAsync(
                    "NAMESPACE POD INVENTORY",
                    "get",
                    "pods",
                    "--namespace",
                    KubernetesRuntimePoolScenarioConstants.Namespace,
                    "--output=wide")
                .ConfigureAwait(false);

            await WriteKubectlDiagnosticAsync(
                    "NAMESPACE POD EVENTS",
                    "get",
                    "events",
                    "--namespace",
                    KubernetesRuntimePoolScenarioConstants.Namespace,
                    "--field-selector",
                    "involvedObject.kind=Pod",
                    "--sort-by=.metadata.creationTimestamp")
                .ConfigureAwait(false);

            foreach (var pod in diagnosticPods)
            {
                await WriteKubectlDiagnosticAsync(
                        $"POD STATUS {pod.PodName}",
                        "get",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--output=wide")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD TERMINATION STATE {pod.PodName}",
                        "get",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--output=jsonpath={range .status.containerStatuses[*]}container={.name} ready={.ready} restartCount={.restartCount} state={.state} lastState={.lastState}{\"\\n\"}{end}")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD DESCRIPTION {pod.PodName}",
                        "describe",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace)
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD CURRENT LOGS {pod.PodName}",
                        "logs",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--all-containers=true",
                        "--tail=250")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD PREVIOUS LOGS {pod.PodName}",
                        "logs",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--all-containers=true",
                        "--previous",
                        "--tail=250")
                    .ConfigureAwait(false);
            }

            output.WriteLine(
                "# GRPC KUBERNETES RUNTIME POOL BOUNDED CAPACITY FAILURE DIAGNOSTICS END");
            output.WriteLine(string.Empty);
        }

        private async Task WriteKubectlDiagnosticAsync(
            string title,
            params string[] arguments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(arguments);

            output.WriteLine(string.Empty);
            output.WriteLine($"## {title}");
            output.WriteLine($"Command='kubectl {string.Join(" ", arguments)}'");

            try
            {
                var result =
                    await RunKubectlAsync(
                            CancellationToken.None,
                            arguments)
                        .ConfigureAwait(false);

                output.WriteLine($"ExitCode='{result.ExitCode}'");

                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    output.WriteLine(result.StandardOutput.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    output.WriteLine("[STANDARD ERROR]");
                    output.WriteLine(result.StandardError.TrimEnd());
                }
            }
            catch (Exception diagnosticException)
            {
                output.WriteLine(
                    $"[DIAGNOSTIC COMMAND WARNING] Message='{diagnosticException.Message}'.");
            }
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

        private static AiSharedRuntimeControllerRequest
            CreateBoundedCapacitySubmitRequest(
                ProductionTenantScenarioDefinition tenant,
                string controlPlaneId,
                string pipelineName,
                string requestedBy,
                string source,
                string correlationId)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

            var input =
                new Dictionary<string, object?>(
                    tenant.Run.Input,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                        tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                        tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["delayMs"] = tenant.Run.DelayMs,
                    ["stepCount"] = tenant.Run.StepCount
                };

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                        tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                        tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["runtimeInstanceIdPrefix"] =
                        tenant.RuntimeInstanceIdPrefix,
                    ["logicalControlPlaneId"] = controlPlaneId,
                    ["controlPlaneId"] = controlPlaneId,
                    ["control-plane.id"] = controlPlaneId,
                    ["controlplane.id"] = controlPlaneId,
                    ["runtime.controlPlaneId"] = controlPlaneId,
                    ["runtime.control-plane.id"] = controlPlaneId,
                    ["runtime.controlplane.id"] = controlPlaneId,
                    ["scenario.controlPlaneId"] = controlPlaneId,
                    ["scenario.control-plane.id"] = controlPlaneId,
                    ["scenario.controlplane.id"] = controlPlaneId,
                    ["scaleout.controlPlaneId"] = controlPlaneId,
                    ["scaleout.control-plane.id"] = controlPlaneId,
                    ["scaleout.controlplane.id"] = controlPlaneId
                };

            return new AiSharedRuntimeControllerRequest
            {
                Operation =
                    AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = tenant.TenantId,
                RequestedBy = requestedBy,
                Source = source,
                CorrelationId = correlationId,
                Metadata = metadata,
                RunRequest =
                    McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: tenant.Run.StepCount,
                        input: input,
                        enableRetention: tenant.Run.EnableRetention,
                        flakyStepInterval:
                            tenant.Run.FlakyStepInterval)
            };
        }

        private static async Task<IReadOnlyList<BoundedCapacityCompletedRun>>
            WaitForSubmittedRunsToCompleteAsync(
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                BoundedCapacityMachineLimitObservation observation,
                TimeSpan timeout,
                TimeSpan noProgressTimeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
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

            IReadOnlyList<BoundedCapacityRunObservation> lastObservations =
                Array.Empty<BoundedCapacityRunObservation>();

            var completedObservationsBySharedRunId =
                new Dictionary<string, BoundedCapacityRunObservation>(
                    StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                observation.ThrowIfViolated();

                var lastRuns =
                    (await sharedRunStore
                            .ListAsync(
                                includeCancelled: true,
                                includeCompleted: true,
                                includeFailed: true)
                            .ConfigureAwait(false))
                        .Where(
                            run =>
                                submittedSharedRunIds.Contains(run.SharedRunId) &&
                                StringComparer.Ordinal.Equals(
                                    run.ControlPlaneId,
                                    controlPlaneId) &&
                                StringComparer.Ordinal.Equals(
                                    run.ExecutionContextSnapshot.TenantId,
                                    tenantId))
                        .ToArray();

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
                                        run);
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

                if (!StringComparer.Ordinal.Equals(
                        progressSignature,
                        lastProgressSignature))
                {
                    lastProgressSignature = progressSignature;
                    lastProgressAtUtc = DateTimeOffset.UtcNow;
                }
                else if (
                    DateTimeOffset.UtcNow - lastProgressAtUtc >=
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

                    throw new TimeoutException(
                        $"The bounded-capacity workload made no durable progress for '{noProgressTimeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastObservations.Count}', SharedStatusBreakdown='{sharedStatusBreakdown}', RuntimeIndexStatusBreakdown='{runtimeIndexStatusBreakdown}'." +
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
                AiSharedRunRecord sharedRun)
        {
            if (string.IsNullOrWhiteSpace(sharedRun.LocalRunId))
            {
                return new BoundedCapacityRunObservation(
                    sharedRun,
                    RuntimeIndexExists: false,
                    RuntimeIndexStatus: null,
                    RuntimeIndexRuntimeInstanceId: null,
                    RuntimeIndexExecutionId: null,
                    RuntimeIndexCompletedAtUtc: null);
            }

            var indexEntry =
                await runExecutionIndex
                    .GetAsync(sharedRun.LocalRunId)
                    .ConfigureAwait(false);

            return indexEntry is null
                ? new BoundedCapacityRunObservation(
                    sharedRun,
                    RuntimeIndexExists: false,
                    RuntimeIndexStatus: null,
                    RuntimeIndexRuntimeInstanceId: null,
                    RuntimeIndexExecutionId: null,
                    RuntimeIndexCompletedAtUtc: null)
                : new BoundedCapacityRunObservation(
                    sharedRun,
                    RuntimeIndexExists: true,
                    RuntimeIndexStatus: indexEntry.Status,
                    RuntimeIndexRuntimeInstanceId:
                        indexEntry.RuntimeInstanceId,
                    RuntimeIndexExecutionId: indexEntry.ExecutionId,
                    RuntimeIndexCompletedAtUtc:
                        indexEntry.CompletedAtUtc);
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
            DateTimeOffset? RuntimeIndexCompletedAtUtc)
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
                RuntimeIndexExists &&
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
        protected override AiRunPlacementDirective? CreateRemainingInventoryRunPlacementDirective(
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return new AiRunPlacementDirective
            {
                Target = new AiRunPlacementTarget
                {
                    RuntimeInstanceId = runtimeInstanceId
                },
                Requirement = AiRunPlacementRequirement.Required,
                Fallback = AiRunPlacementFallback.Reject
            };
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

        /// <inheritdoc />
        protected override IAiRuntimeHostProcessControl ResolveProcessControl(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return UnsupportedRuntimePoolProcessControl.Instance;
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

        /// <inheritdoc />
        protected override async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecuteImpactedTenantFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var phase =
                context.RuntimePoolFailurePhase
                ?? throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool scenario requires an explicit physical failure phase.");

            var state =
                states.GetOrAdd(
                    context.ControlPlaneId,
                    _ => new RuntimePoolAllInOneFailureState());

            return phase.FailureKind switch
            {
                RuntimePoolCrashFailureKind.RuntimeProcess =>
                    await ExecuteRuntimeProcessFailureAsync(
                            context,
                            state)
                        .ConfigureAwait(false),

                RuntimePoolCrashFailureKind.KubernetesPod =>
                    await ExecutePodFailureAsync(
                            context,
                            state)
                        .ConfigureAwait(false),

                _ =>
                    throw new InvalidOperationException(
                        string.Concat(
                            "Unsupported Runtime Pool failure kind '",
                            phase.FailureKind,
                            "'."))
            };
        }

        private async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecuteRuntimeProcessFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context,
                RuntimePoolAllInOneFailureState state)
        {
            try
            {
                var poolId = ResolvePoolId(context.ControlPlaneId);
                var target =
                    await GetRequiredRuntimeSnapshotAsync(
                            context.Registry,
                            context.Inventory.RuntimeInstanceId)
                        .ConfigureAwait(false);

                AssertRuntimePoolIdentity(
                    target,
                    poolId);

                await state.TrackCurrentPoolPodsAsync(
                        context.Registry,
                        poolId)
                    .ConfigureAwait(false);

                var membershipEnumerator =
                    context.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodMembershipEnumerator>();

                var membership =
                    await membershipEnumerator
                        .EnumerateAsync(
                            poolId,
                            target.HostId!)
                        .ConfigureAwait(false);

                Assert.Equal(
                    profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                    membership.Members.Count);

                var siblingRuntimeInstanceIds =
                    membership.Members
                        .Where(
                            member =>
                                !StringComparer.Ordinal.Equals(
                                    member.RuntimeInstanceId,
                                    target.RuntimeInstanceId))
                        .Select(member => member.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                Assert.NotEmpty(siblingRuntimeInstanceIds);
                state.SetRuntimeFailureHostId(target.HostId!);

                var recovery =
                    await ExecuteAssignedInventoryFailureAsync(
                            context,
                            new KubernetesRuntimePoolChildProcessControl(
                                context.Registry,
                                poolId,
                                output))
                        .ConfigureAwait(false);

                await AssertExactSiblingsRemainReadyAsync(
                        context.Registry,
                        target.HostId!,
                        siblingRuntimeInstanceIds,
                        context.RedispatchTimeout)
                    .ConfigureAwait(false);

                await AssertBoundedPhysicalPodCountAsync(
                        state)
                    .ConfigureAwait(false);

                state.CompleteRuntimeFailure();
                return recovery;
            }
            catch (Exception exception)
            {
                state.FailRuntimeFailure(exception);
                throw;
            }
        }

        private async Task<RealRuntimeCrashFailedRuntimeRecoveryProof>
            ExecutePodFailureAsync(
                ProcessHostCrashRecoveryFailureExecutionContext context,
                RuntimePoolAllInOneFailureState state)
        {
            var hasPriorRuntimeFailure =
                profile.CrashRecoveryPlan.FailurePhases.Any(
                    phase =>
                        phase.FailureKind ==
                            RuntimePoolCrashFailureKind.RuntimeProcess);

            if (hasPriorRuntimeFailure)
            {
                await state.RuntimeFailureCompletion
                    .WaitAsync(TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);
            }

            var poolId = ResolvePoolId(context.ControlPlaneId);
            var target =
                await GetRequiredRuntimeSnapshotAsync(
                        context.Registry,
                        context.Inventory.RuntimeInstanceId)
                    .ConfigureAwait(false);

            AssertRuntimePoolIdentity(
                target,
                poolId);

            if (hasPriorRuntimeFailure)
            {
                Assert.NotEqual(
                    state.RuntimeFailureHostId,
                    target.HostId);
            }

            await state.TrackCurrentPoolPodsAsync(
                    context.Registry,
                    poolId)
                .ConfigureAwait(false);

            var membershipEnumerator =
                context.Services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodMembershipEnumerator>();

            var failedMembership =
                await membershipEnumerator
                    .EnumerateAsync(
                        poolId,
                        target.HostId!)
                    .ConfigureAwait(false);

            Assert.Equal(
                profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                failedMembership.Members.Count);

            var survivingHostIds =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        profile.CrashRecoveryPlan.InitialPodCount,
                        context.RedispatchTimeout)
                    .ConfigureAwait(false);

            Assert.Equal(
                profile.CrashRecoveryPlan.InitialPodCount,
                survivingHostIds.Count);

            survivingHostIds.Remove(target.HostId!);

            if (hasPriorRuntimeFailure)
            {
                Assert.Contains(
                    state.RuntimeFailureHostId,
                    survivingHostIds);
            }

            Assert.NotEmpty(survivingHostIds);

            var coordinator =
                context.Services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator>();

            var podControl =
                new KubernetesRuntimePoolPodFailureProcessControl(
                    target,
                    coordinator,
                    CreateHostStartTemplate(
                        context,
                        target,
                        poolId),
                    output);

            var recovery =
                await ExecuteAssignedInventoryFailureAsync(
                        context,
                        podControl)
                    .ConfigureAwait(false);

            var podRecovery =
                await podControl.RecoveryTask
                    .WaitAsync(
                        TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                podRecovery.Status);
            Assert.NotNull(podRecovery.Replacement);
            Assert.NotNull(podRecovery.Recovery);
            Assert.NotEqual(
                target.HostId,
                podRecovery.Replacement!.ReplacementPodUid);
            Assert.Equal(
                profile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                podRecovery.Replacement.Membership.Members.Count);

            var failedRuntimeInstanceIds =
                failedMembership.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.DoesNotContain(
                podRecovery.Replacement.Membership.Members,
                member =>
                    failedRuntimeInstanceIds.Contains(
                        member.RuntimeInstanceId));

            if (podRecovery.Replacement.HostStartResult.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostName,
                    out var replacementPodName) &&
                !string.IsNullOrWhiteSpace(replacementPodName))
            {
                state.TrackPod(
                    target.KubernetesNamespace!,
                    replacementPodName);
            }

            await AssertSurvivingHostsRemainReadyAsync(
                    context.Registry,
                    poolId,
                    survivingHostIds,
                    context.RedispatchTimeout)
                .ConfigureAwait(false);

            await AssertBoundedPhysicalPodCountAsync(
                    state)
                .ConfigureAwait(false);

            return recovery;
        }

        protected string ResolvePoolId(
            string controlPlaneId)
        {
            return RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                profile.PoolIdPrefix,
                controlPlaneId);
        }

        private async Task AssertBoundedPhysicalPodCountAsync(
            RuntimePoolAllInOneFailureState state)
        {
            var trackedPods = state.GetTrackedPods();

            var existenceResults =
                await Task.WhenAll(
                        trackedPods.Select(
                            trackedPod =>
                                RunKubectlAsync(
                                    CancellationToken.None,
                                    "get",
                                    "pod",
                                    trackedPod.PodName,
                                    "--namespace",
                                    trackedPod.Namespace,
                                    "--output=name")))
                    .ConfigureAwait(false);

            var existingPodCount =
                existenceResults.Count(result => result.ExitCode == 0);

            Assert.InRange(
                existingPodCount,
                1,
                profile.CrashRecoveryPlan.MaximumPodCount);
        }

        private static async Task<HashSet<string>> GetActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        protected static async Task<HashSet<string>> WaitForActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            int expectedHostCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfLessThan(expectedHostCount, 1);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            HashSet<string> activeHostIds =
                new(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                activeHostIds =
                    await GetActiveHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (activeHostIds.Count == expectedHostCount)
                {
                    return activeHostIds;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "The bounded Runtime Pool did not expose the expected active Pod count before failure injection. PoolId='",
                    poolId,
                    "', ExpectedHostCount='",
                    expectedHostCount,
                    "', ActualHostCount='",
                    activeHostIds.Count,
                    "'."));
        }

        private static async Task<HashSet<string>> GetSelectableHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        snapshot.CanAcceptRun &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static async Task AssertExactSiblingsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string hostId,
            IReadOnlyCollection<string> siblingRuntimeInstanceIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await Task.WhenAll(
                            siblingRuntimeInstanceIds.Select(
                                runtimeInstanceId =>
                                    registry.GetAsync(runtimeInstanceId)))
                        .ConfigureAwait(false);

                if (snapshots.All(
                        snapshot =>
                            snapshot is not null &&
                            StringComparer.Ordinal.Equals(
                                snapshot.HostId,
                                hostId) &&
                            snapshot.Status ==
                                AiRuntimeInstanceStatus.Ready))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "The healthy Runtime Pool siblings did not remain ready after the exact child-process kill.");
        }

        private static async Task AssertSurvivingHostsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            IReadOnlySet<string> survivingHostIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var activeHostIds =
                    await GetSelectableHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (survivingHostIds.All(activeHostIds.Contains))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "At least one healthy Runtime Pool Pod lost selectable membership during another Pod's recovery.");
        }

        protected static async Task<AiRuntimeInstanceSnapshot>
            GetRequiredRuntimeSnapshotAsync(
                IAiRuntimeInstanceRegistry registry,
                string runtimeInstanceId)
        {
            var snapshot =
                await registry
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            return snapshot
                ?? throw new InvalidOperationException(
                    string.Concat(
                        "Runtime instance '",
                        runtimeInstanceId,
                        "' was not found in the shared registry."));
        }

        private static void AssertRuntimePoolIdentity(
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            Assert.Equal(poolId, snapshot.PoolId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HostId));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesNamespace));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesPodName));
        }

        private static AiRuntimeHostStartRequest CreateHostStartTemplate(
            ProcessHostCrashRecoveryFailureExecutionContext context,
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            var tenantId =
                snapshot.TenantId
                ?? throw new InvalidOperationException(
                    "The failed Kubernetes Runtime Pool member must expose its first-class TenantId.");

            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        "mcp-pod-recovery-template-",
                        context.ControlPlaneId),
                ControlPlaneId = context.ControlPlaneId,
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                RuntimeInstanceIdPrefix =
                    string.Concat(poolId, "-runtime"),
                ProviderName = "grpc",
                TransportName = "grpc",
                TenantId = tenantId,
                TenantGroupId = snapshot.TenantGroupId,
                IsolationMode = "Dedicated",
                PreferDedicatedCapacity = true,
                AllowSharedFallback = true,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 2,
                MaxRuntimeInstances = 3,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-pod-recovery-",
                                context.ControlPlaneId,
                                "-",
                                tenantId),
                        Project =
                            "mcp-kubernetes-runtime-pool-crash-recovery",
                        UserId = "system",
                        TenantId = tenantId,
                        TenantGroupId = snapshot.TenantGroupId,
                        CurrentNamespace = "tests",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    },
                Metadata = new Dictionary<string, string>()
            };
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

            IReadOnlyCollection<TrackedPod> discoveredPods;

            try
            {
                discoveredPods =
                    await DiscoverPoolPodsAsync(poolId)
                        .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL CLEANUP DISCOVERY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");

                discoveredPods =
                    Array.Empty<TrackedPod>();
            }

            var podsToDelete =
                trackedPods
                    .Concat(discoveredPods)
                    .Distinct()
                    .ToArray();

            output.WriteLine(
                $"[GRPC KUBERNETES RUNTIME POOL SCENARIO CLEANUP START] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', PodCount='{podsToDelete.Length}'.");

            foreach (var trackedPod in podsToDelete)
            {
                try
                {
                    var deleteResult =
                        await RunKubectlAsync(
                                CancellationToken.None,
                                "delete",
                                "pod",
                                trackedPod.PodName,
                                "--namespace",
                                trackedPod.Namespace,
                                "--ignore-not-found=true",
                                "--grace-period=0",
                                "--force",
                                "--wait=true",
                                "--timeout=90s")
                            .ConfigureAwait(false);

                    if (deleteResult.ExitCode != 0)
                    {
                        output.WriteLine(
                            $"[GRPC KUBERNETES RUNTIME POOL CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', StandardError='{deleteResult.StandardError}'.");
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[GRPC KUBERNETES RUNTIME POOL CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', Message='{exception.Message}'.");
                }
            }

            var cleanupDeadline =
                DateTimeOffset.UtcNow.AddSeconds(90);

            IReadOnlyCollection<TrackedPod> remainingPods =
                Array.Empty<TrackedPod>();

            while (DateTimeOffset.UtcNow < cleanupDeadline)
            {
                try
                {
                    remainingPods =
                        await DiscoverPoolPodsAsync(poolId)
                            .ConfigureAwait(false);

                    if (remainingPods.Count == 0)
                    {
                        states.TryRemove(
                            controlPlaneId,
                            out _);

                        output.WriteLine(
                            $"[GRPC KUBERNETES RUNTIME POOL SCENARIO CLEANUP COMPLETE] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', RemainingPodCount='0'.");

                        return;
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[GRPC KUBERNETES RUNTIME POOL CLEANUP VERIFY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "Kubernetes Runtime Pool scenario cleanup left Pods behind. ControlPlaneId='",
                    controlPlaneId,
                    "', PoolId='",
                    poolId,
                    "', RemainingPods='",
                    string.Join(
                        ",",
                        remainingPods.Select(pod => pod.PodName)),
                    "'."));
        }

        private static async Task<IReadOnlyCollection<TrackedPod>>
            DiscoverPoolPodsAsync(
                string poolId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var result =
                await RunKubectlAsync(
                        CancellationToken.None,
                        "get",
                        "pods",
                        "--namespace",
                        KubernetesRuntimePoolScenarioConstants.Namespace,
                        "--output=json")
                    .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Runtime Pool Pods could not be listed for cleanup. StandardError=",
                        result.StandardError));
            }

            using var document =
                JsonDocument.Parse(result.StandardOutput);

            var pods =
                new List<TrackedPod>();

            if (!document.RootElement.TryGetProperty(
                    "items",
                    out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return pods;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty(
                        "metadata",
                        out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty(
                        "annotations",
                        out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Object ||
                    !annotations.TryGetProperty(
                        "multiplexed.ai/pool-id",
                        out var poolIdAnnotation) ||
                    poolIdAnnotation.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(
                        poolIdAnnotation.GetString(),
                        poolId) ||
                    !metadata.TryGetProperty(
                        "name",
                        out var podNameElement))
                {
                    continue;
                }

                var podName =
                    podNameElement.GetString();

                if (string.IsNullOrWhiteSpace(podName))
                {
                    continue;
                }

                var @namespace =
                    KubernetesRuntimePoolScenarioConstants.Namespace;

                if (metadata.TryGetProperty(
                        "namespace",
                        out var namespaceElement) &&
                    !string.IsNullOrWhiteSpace(namespaceElement.GetString()))
                {
                    @namespace = namespaceElement.GetString()!;
                }

                pods.Add(
                    new TrackedPod(
                        @namespace,
                        podName));
            }

            return pods;
        }

        private static async Task<KubectlResult> RunKubectlAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process =
                new System.Diagnostics.Process
                {
                    StartInfo = startInfo
                };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "kubectl could not be started.");
            }

            var standardOutputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            await process
                .WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);

            return new KubectlResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }

        private sealed class KubernetesRuntimePoolChildProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly string poolId;
            private readonly ITestOutputHelper output;

            public KubernetesRuntimePoolChildProcessControl(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                ITestOutputHelper output)
            {
                this.registry = registry;
                this.poolId = poolId;
                this.output = output;
            }

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    await GetRequiredRuntimeSnapshotAsync(
                            registry,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                AssertRuntimePoolIdentity(
                    snapshot,
                    poolId);
                Assert.True(snapshot.ProcessId.HasValue);

                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL PROCESS KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ProcessId='{snapshot.ProcessId}'.");

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

        private sealed class KubernetesRuntimePoolPodFailureProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly AiRuntimeInstanceSnapshot target;
            private readonly IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator coordinator;
            private readonly AiRuntimeHostStartRequest hostStartTemplate;
            private readonly ITestOutputHelper output;
            private Task<AiKubernetesRuntimePoolPodFailureRecoveryResult>?
                recoveryTask;
            private int executed;

            public KubernetesRuntimePoolPodFailureProcessControl(
                AiRuntimeInstanceSnapshot target,
                IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator coordinator,
                AiRuntimeHostStartRequest hostStartTemplate,
                ITestOutputHelper output)
            {
                this.target = target;
                this.coordinator = coordinator;
                this.hostStartTemplate = hostStartTemplate;
                this.output = output;
            }

            public Task<AiKubernetesRuntimePoolPodFailureRecoveryResult>
                RecoveryTask =>
                    recoveryTask
                    ?? throw new InvalidOperationException(
                        "Pod recovery has not been started.");

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref executed, 1) != 0)
                {
                    return false;
                }

                if (!StringComparer.Ordinal.Equals(
                        runtimeInstanceId,
                        target.RuntimeInstanceId))
                {
                    throw new InvalidOperationException(
                        "The Pod failure control received a different RuntimeInstanceId.");
                }

                output.WriteLine(
                    $"[GRPC KUBERNETES RUNTIME POOL POD KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{target.HostId}', PodName='{target.KubernetesPodName}'.");

                var deleteResult =
                    await RunKubectlAsync(
                            cancellationToken,
                            "delete",
                            "pod",
                            target.KubernetesPodName!,
                            "--namespace",
                            target.KubernetesNamespace!,
                            "--grace-period=0",
                            "--force",
                            "--wait=true",
                            "--timeout=90s")
                        .ConfigureAwait(false);

                if (deleteResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The Kubernetes Runtime Pool Pod could not be deleted. StandardError=",
                            deleteResult.StandardError));
                }

                recoveryTask =
                    coordinator.RecoverAsync(
                        new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                        {
                            FailureId =
                                string.Concat(
                                    "mcp-kubernetes-pod-failure-",
                                    target.HostId),
                            PoolId = target.PoolId!,
                            PodUid = target.HostId!,
                            ClaimedBy =
                                "mcp-grpc-kubernetes-runtime-pool-scenario",
                            FailureMessage =
                                "Forced Kubernetes Runtime Pool Pod deletion in the MCP recovery proof.",
                            HostStartTemplate = hostStartTemplate
                        },
                        CancellationToken.None);

                return true;
            }
        }

        private sealed class RuntimePoolAllInOneFailureState
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

        private sealed class UnsupportedRuntimePoolProcessControl :
            IAiRuntimeHostProcessControl
        {
            public static UnsupportedRuntimePoolProcessControl Instance { get; } =
                new();

            public Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Runtime Pool physical failures must be executed through the explicit failure-phase hook.");
            }
        }

        private sealed record TrackedPod(
            string Namespace,
            string PodName);

        private sealed record KubectlResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);
    }

    /// <summary>
    /// Executes the existing three-tenant all-in-one MCP crash-recovery proof against a real Kubernetes Runtime Pool.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolCrashRecovery")]
    public sealed class GrpcKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        /// <summary>
        /// Initializes the real gRPC Kubernetes Runtime Pool crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies in one scenario that a child-process kill preserves its siblings, a later Pod deletion
        /// suppresses the exact Pod membership, healthy Pods remain available, and all durable work recovers once.
        /// </summary>
        /// <returns>A task that completes when the all-in-one proof has finished.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_Should_Recover_Runtime_And_Pod_Failures_Without_Impacting_Safe_Tenant()
        {
            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Measures pure machine capacity with a bounded three-Pod Kubernetes Runtime Pool.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Pods.</param>
        /// <param name="runtimeCountPerPod">The number of runtimes hosted by every Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves.</param>
        /// <returns>A task that completes when the load has drained and the report has been written.</returns>
        [Theory]
        [InlineData(4, 6, 12)]
        public async Task Grpc_KubernetesPool_Should_Measure_Machine_Limit_With_Bounded_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            await ExecuteBoundedCapacityMachineLimitScenarioAsync(
                    maximumPodCount,
                    runtimeCountPerPod,
                    submissionIterationCount)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies the same bounded-capacity workload while one fully busy Runtime Pool Pod is force-deleted.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Pods.</param>
        /// <param name="runtimeCountPerPod">The number of runtimes hosted by every Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves.</param>
        /// <returns>A task that completes after exact Pod recovery, replay, ledger, trace, and cleanup proof.</returns>
        [Theory]
        [InlineData(3, 5, 5)]
        public async Task Grpc_KubernetesPool_Should_Recover_Bounded_Capacity_After_Forced_Pod_Deletion(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            await ExecuteBoundedCapacityPodFailureMachineLimitScenarioAsync(
                    maximumPodCount,
                    runtimeCountPerPod,
                    submissionIterationCount)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies repeated production cycles against one warm Runtime Pool.
        /// Every cycle force-deletes one fully busy Pod, but the surviving and replacement Pods
        /// remain alive and are reused by the next cycle; cleanup runs only after the final cycle.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Pods.</param>
        /// <param name="runtimeCountPerPod">The number of runtimes hosted by every Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential cycles executed against the same warm pool.</param>
        /// <returns>A task that completes after warm reuse, exact recovery, replay, ledger, trace, and final cleanup.</returns>
        [Theory]
        [InlineData(3, 5, 5, 2)]
        public async Task Grpc_KubernetesPool_Should_Reuse_Warm_Capacity_Across_Sequential_Production_Recovery_Cycles(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount)
        {
            await ExecuteReusableBoundedCapacityPodFailureProductionScenarioAsync(
                    maximumPodCount,
                    runtimeCountPerPod,
                    submissionIterationCount,
                    executionCycleCount)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a production-like mixed-admission Runtime Pool simulation before reusing
    /// the existing global crash-recovery scenario.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolExistingCapacityProduction")]
    public sealed class GrpcKubernetesRuntimePoolExistingCapacityProductionScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        private const int ProbeStepCount = 5;
        private const int ProbeDelayMs = 50;
        private readonly ConcurrentDictionary<string, byte> sharedRuntimeInstanceIds =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> inventoryRuntimeInstanceIdsByTenantId =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> productionPreludeScaleOutSharedRunIds =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes the production-like existing-capacity and crash-recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolExistingCapacityProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessScaleOutTimeoutOverride =>
            TimeSpan.FromMinutes(4);

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessProgressTimeoutOverride =>
            TimeSpan.FromMinutes(5);

        /// <inheritdoc />
        protected override bool UsesProductionTrafficPrelude => true;

        /// <inheritdoc />
        protected override bool WaitsForFirstInventoryScaleOutFulfillment => false;

        /// <inheritdoc />
        protected override IReadOnlyCollection<string> AdditionalControlPlaneLedgerSharedRunIds =>
            productionPreludeScaleOutSharedRunIds.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        /// <inheritdoc />
        protected override AiRunPlacementDirective? CreateFirstInventoryRunPlacementDirective(
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);

            if (!inventoryRuntimeInstanceIdsByTenantId.TryGetValue(
                    tenant.TenantId,
                    out var runtimeInstanceId) ||
                string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                throw new InvalidOperationException(
                    $"The production warm-capacity proof did not record a compatible first-inventory runtime. TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}'.");
            }

            return new AiRunPlacementDirective
            {
                Target = new AiRunPlacementTarget
                {
                    RuntimeInstanceId = runtimeInstanceId
                },
                Requirement = AiRunPlacementRequirement.Required,
                Fallback = AiRunPlacementFallback.Reject
            };
        }

        /// <summary>
        /// Proves that Dedicated, Hybrid, and Shared tenants warm their policy-compatible
        /// capacity and that a second traffic wave reuses those existing runtime identities
        /// without creating another Pod before the global crash-recovery proof begins.
        /// </summary>
        /// <returns>A task that completes when the complete production simulation converges.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_Should_Reuse_Existing_Admission_Visible_Capacity_Before_Global_Crash_Recovery()
        {
            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        protected override ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryScenario(
            bool includeSafeTenant = false)
        {
            var scenario =
                base.CreateRealRuntimeCrashRecoveryScenario(
                    includeSafeTenant: true);

            /*
             * Tenant A receives the runtime-process failure. Hybrid is safe here because
             * the existing pool manager replaces only the exact child process and preserves
             * the host configuration. Tenant B receives the whole-Pod failure and remains
             * Dedicated, preserving the already-proven Pod replacement template.
             */
            var tenantA =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-a")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Hybrid,
                    ExpectDedicatedRuntimePrefix = false
                };

            var tenantB =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-b")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Dedicated,
                    ExpectDedicatedRuntimePrefix = false
                };

            var safeTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-safe")) with
                {
                    RuntimeMode = ProductionTenantRuntimeMode.Shared,
                    ExpectDedicatedRuntimePrefix = false
                };

            return scenario with
            {
                Name =
                    "grpc-kubernetes-runtime-pool-existing-capacity-production",
                ControlPlaneIdPrefix =
                    "grpc-kubernetes-runtime-pool-existing-capacity-production",
                Tenants = new[]
                {
                    tenantA,
                    tenantB,
                    safeTenant
                }
            };
        }

        /// <inheritdoc />
        protected override bool IsSafeTenantRuntimeCapacityEligibleForImpactedRecovery(
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return sharedRuntimeInstanceIds.ContainsKey(
                runtimeInstanceId);
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

            if (tenant.RuntimeMode ==
                ProductionTenantRuntimeMode.Dedicated)
            {
                Assert.Equal(
                    tenant.TenantId,
                    snapshot.TenantId);

                return;
            }

            var usesKnownSharedCapacity =
                sharedRuntimeInstanceIds.ContainsKey(
                    runtimeInstanceId);

            if (tenant.RuntimeMode ==
                ProductionTenantRuntimeMode.Shared)
            {
                Assert.True(
                    usesKnownSharedCapacity,
                    $"Shared tenant '{tenant.TenantId}' recovered on runtime '{runtimeInstanceId}', which was not part of the admission-proven shared warm capacity.");

                return;
            }

            Assert.Equal(
                ProductionTenantRuntimeMode.Hybrid,
                tenant.RuntimeMode);

            Assert.True(
                StringComparer.Ordinal.Equals(
                    tenant.TenantId,
                    snapshot.TenantId) ||
                usesKnownSharedCapacity,
                $"Hybrid tenant '{tenant.TenantId}' recovered on runtime '{runtimeInstanceId}' owned by TenantId='{snapshot.TenantId ?? string.Empty}', which is neither tenant-owned nor part of the admission-proven shared warm capacity.");
        }

        /// <inheritdoc />
        protected override async Task ExecuteProductionTrafficPreludeAsync(
            ProcessHostProductionTrafficPreludeContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var orderedTenants =
                context.Tenants
                    .OrderBy(
                        tenant =>
                            GetRuntimeModeOrder(
                                tenant.Tenant.RuntimeMode))
                    .ToArray();

            Assert.Equal(3, orderedTenants.Length);
            Assert.Equal(
                new[]
                {
                    ProductionTenantRuntimeMode.Dedicated,
                    ProductionTenantRuntimeMode.Hybrid,
                    ProductionTenantRuntimeMode.Shared
                },
                orderedTenants
                    .Select(tenant => tenant.Tenant.RuntimeMode)
                    .ToArray());

            context.Output.WriteLine(string.Empty);
            context.Output.WriteLine(
                "# PRODUCTION TRAFFIC PRELUDE - MIXED ADMISSION AND EXISTING CAPACITY REUSE");
            context.Output.WriteLine(
                "[PASS TARGET] Warm Dedicated, Hybrid, and Shared capacity in policy order, validate the durable typed admission request, complete one traffic wave, then prove a second unpinned wave dispatches only to the already-existing RuntimeInstanceId and HostId sets without creating another Pod.");

            var scaleOutRequestStore =
                context.Services.GetRequiredService<
                    IAiRuntimeScaleOutRequestStore>();

            productionPreludeScaleOutSharedRunIds.Clear();

            var firstWave =
                new List<ProductionTrafficDispatchProof>(
                    orderedTenants.Length);

            foreach (var tenant in orderedTenants)
            {
                firstWave.Add(
                    await SubmitProbeAsync(
                            context,
                            scaleOutRequestStore,
                            tenant,
                            waveNumber: 1,
                            expectScaleOutRequest: true)
                        .ConfigureAwait(false));
            }

            await AssertProbeWaveCompletedAsync(
                    context,
                    firstWave)
                .ConfigureAwait(false);

            Assert.Equal(
                orderedTenants.Length,
                productionPreludeScaleOutSharedRunIds.Count);

            var poolId =
                ResolvePoolId(
                    context.ControlPlaneId);

            var warmHostIds =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var expectedWarmRuntimeCount =
                RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount *
                RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod;

            var warmSnapshots =
                await WaitForReadyPoolSnapshotsAsync(
                        context.Registry,
                        poolId,
                        expectedWarmRuntimeCount,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var warmRuntimeInstanceIds =
                warmSnapshots
                    .Select(snapshot => snapshot.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            var firstWaveHostsByMode =
                firstWave.ToDictionary(
                    dispatch => dispatch.TenantContext.Tenant.RuntimeMode,
                    dispatch => dispatch.Snapshot.HostId!,
                    EqualityComparer<ProductionTenantRuntimeMode>.Default);

            inventoryRuntimeInstanceIdsByTenantId.Clear();

            foreach (var dispatch in firstWave)
            {
                Assert.True(
                    inventoryRuntimeInstanceIdsByTenantId.TryAdd(
                        dispatch.TenantContext.Tenant.TenantId,
                        dispatch.Snapshot.RuntimeInstanceId),
                    $"The production warm-capacity proof recorded more than one first-wave runtime for TenantId='{dispatch.TenantContext.Tenant.TenantId}'.");
            }

            Assert.Equal(
                orderedTenants.Length,
                inventoryRuntimeInstanceIdsByTenantId.Count);

            var crashInventoryWarmRuntimeOverlapCount =
                inventoryRuntimeInstanceIdsByTenantId.Count -
                inventoryRuntimeInstanceIdsByTenantId.Values
                    .Distinct(StringComparer.Ordinal)
                    .Count();

            Assert.Equal(
                0,
                crashInventoryWarmRuntimeOverlapCount);

            Assert.Equal(
                RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                firstWaveHostsByMode.Values
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            foreach (var hostId in firstWaveHostsByMode.Values)
            {
                Assert.Equal(
                    RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                    warmSnapshots.Count(
                        snapshot =>
                            StringComparer.Ordinal.Equals(
                                snapshot.HostId,
                                hostId)));
            }

            var sharedHostId =
                firstWaveHostsByMode[ProductionTenantRuntimeMode.Shared];

            foreach (var sharedSnapshot in warmSnapshots.Where(
                         snapshot =>
                             StringComparer.Ordinal.Equals(
                                 snapshot.HostId,
                                 sharedHostId)))
            {
                sharedRuntimeInstanceIds.TryAdd(
                    sharedSnapshot.RuntimeInstanceId,
                    0);
            }

            Assert.Equal(
                RuntimePoolProfile.CrashRecoveryPlan.InitialRuntimeCountPerPod,
                sharedRuntimeInstanceIds.Count);

            var secondWave =
                new List<ProductionTrafficDispatchProof>(
                    orderedTenants.Length);

            foreach (var tenant in orderedTenants)
            {
                var dispatch =
                    await SubmitProbeAsync(
                            context,
                            scaleOutRequestStore,
                            tenant,
                            waveNumber: 2,
                            expectScaleOutRequest: false)
                        .ConfigureAwait(false);

                Assert.Contains(
                    dispatch.Run.AssignedRuntimeInstanceId!,
                    warmRuntimeInstanceIds);

                var expectedHostIds =
                    tenant.Tenant.RuntimeMode switch
                    {
                        ProductionTenantRuntimeMode.Dedicated =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Dedicated]
                            },
                        ProductionTenantRuntimeMode.Hybrid =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Hybrid],
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Shared]
                            },
                        ProductionTenantRuntimeMode.Shared =>
                            new[]
                            {
                                firstWaveHostsByMode[
                                    ProductionTenantRuntimeMode.Shared]
                            },
                        _ =>
                            throw new InvalidOperationException(
                                $"Unsupported production tenant runtime mode '{tenant.Tenant.RuntimeMode}'.")
                    };

                Assert.Contains(
                    dispatch.Snapshot.HostId!,
                    expectedHostIds);

                secondWave.Add(dispatch);
            }

            await AssertProbeWaveCompletedAsync(
                    context,
                    secondWave)
                .ConfigureAwait(false);

            var hostsAfterReuse =
                await WaitForActiveHostIdsAsync(
                        context.Registry,
                        poolId,
                        RuntimePoolProfile.CrashRecoveryPlan.InitialPodCount,
                        context.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.True(
                warmHostIds.SetEquals(hostsAfterReuse),
                $"The existing-capacity wave changed the physical Pod set. Before='{string.Join(",", warmHostIds.OrderBy(value => value, StringComparer.Ordinal))}', After='{string.Join(",", hostsAfterReuse.OrderBy(value => value, StringComparer.Ordinal))}'.");

            context.Output.WriteLine(string.Empty);
            context.Output.WriteLine(
                "[GRPC KUBERNETES RUNTIME POOL EXISTING CAPACITY PRODUCTION SUMMARY]");
            context.Output.WriteLine("TrafficWaveCount='2'");
            context.Output.WriteLine($"TenantCount='{orderedTenants.Length}'");
            context.Output.WriteLine("DedicatedTenantCount='1'");
            context.Output.WriteLine("HybridTenantCount='1'");
            context.Output.WriteLine("SharedTenantCount='1'");
            context.Output.WriteLine($"WarmPodCount='{warmHostIds.Count}'");
            context.Output.WriteLine($"WarmRuntimeCount='{warmSnapshots.Count}'");
            context.Output.WriteLine($"ExistingRuntimeDispatchCount='{secondWave.Count}'");
            context.Output.WriteLine($"CrashInventoryWarmRuntimeCount='{inventoryRuntimeInstanceIdsByTenantId.Count}'");
            context.Output.WriteLine($"CrashInventoryWarmRuntimeOverlapCount='{crashInventoryWarmRuntimeOverlapCount}'");
            context.Output.WriteLine($"PreludeScaleOutSharedRunCount='{productionPreludeScaleOutSharedRunIds.Count}'");
            context.Output.WriteLine("ScaleOutRequestCountDuringReuse='0'");
            context.Output.WriteLine("NewRuntimeDispatchCount='0'");
            context.Output.WriteLine("NewPodCountDuringReuse='0'");
            context.Output.WriteLine("AdmissionViolationCount='0'");
            context.Output.WriteLine(
                "[GRPC KUBERNETES RUNTIME POOL EXISTING CAPACITY PRODUCTION SUMMARY END]");
        }

        private async Task<ProductionTrafficDispatchProof> SubmitProbeAsync(
            ProcessHostProductionTrafficPreludeContext context,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            ProcessHostProductionTrafficTenantContext tenantContext,
            int waveNumber,
            bool expectScaleOutRequest)
        {
            var probeTenant =
                tenantContext.Tenant with
                {
                    Run = tenantContext.Tenant.Run with
                    {
                        RunCount = 1,
                        StepCount = ProbeStepCount,
                        DelayMs = ProbeDelayMs,
                        FlakyStepInterval = 0,
                        EnableRetention = true
                    }
                };

            var pipelineName =
                $"{tenantContext.PipelinePrefix}-production-wave-{waveNumber:D2}-{Guid.NewGuid():N}";

            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        tenantContext.Mcp,
                        probeTenant,
                        context.ControlPlaneId,
                        pipelineName,
                        context.RequestedBy,
                        context.Source)
                    .ConfigureAwait(false);

            var dispatchedRun =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        tenantContext.Mcp,
                        pipelineName,
                        sharedRunId,
                        context.ScaleOutTimeout +
                        context.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    dispatchedRun.AssignedRuntimeInstanceId),
                $"Production probe was accepted without a runtime binding. TenantId='{probeTenant.TenantId}', RuntimeMode='{probeTenant.RuntimeMode}', SharedRunId='{sharedRunId}'.");

            var snapshot =
                await GetRequiredRuntimeSnapshotAsync(
                        context.Registry,
                        dispatchedRun.AssignedRuntimeInstanceId!)
                    .ConfigureAwait(false);

            AiRuntimeScaleOutRequestRecord? scaleOutRequest = null;

            if (expectScaleOutRequest)
            {
                scaleOutRequest =
                    await WaitForScaleOutRequestAsync(
                            scaleOutRequestStore,
                            context.ControlPlaneId,
                            probeTenant,
                            pipelineName,
                            sharedRunId,
                            context.ScaleOutTimeout)
                        .ConfigureAwait(false);

                AssertTypedAdmissionRequest(
                    scaleOutRequest,
                    probeTenant);

                Assert.True(
                    productionPreludeScaleOutSharedRunIds.TryAdd(
                        sharedRunId,
                        0),
                    $"The production prelude recorded the same scale-out shared run more than once. SharedRunId='{sharedRunId}', ScaleOutRequestId='{scaleOutRequest.RequestId}'.");

                Assert.False(
                    string.IsNullOrWhiteSpace(
                        scaleOutRequest.FulfilledRuntimeInstanceId));

                var fulfilledSnapshot =
                    await GetRequiredRuntimeSnapshotAsync(
                            context.Registry,
                            scaleOutRequest.FulfilledRuntimeInstanceId!)
                        .ConfigureAwait(false);

                Assert.Equal(
                    fulfilledSnapshot.HostId,
                    snapshot.HostId);
            }
            else
            {
                await AssertNoScaleOutRequestAsync(
                        scaleOutRequestStore,
                        context.ControlPlaneId,
                        probeTenant,
                        pipelineName,
                        sharedRunId,
                        TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }

            context.Output.WriteLine(
                $"[PRODUCTION TRAFFIC DISPATCH] Wave='{waveNumber}', TenantId='{probeTenant.TenantId}', RuntimeMode='{probeTenant.RuntimeMode}', SharedRunId='{sharedRunId}', RuntimeInstanceId='{snapshot.RuntimeInstanceId}', PoolId='{snapshot.PoolId}', HostId='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ScaleOutRequestId='{scaleOutRequest?.RequestId ?? string.Empty}'.");

            return new ProductionTrafficDispatchProof(
                tenantContext,
                dispatchedRun,
                snapshot);
        }

        private static async Task<AiRuntimeScaleOutRequestRecord>
            WaitForScaleOutRequestAsync(
                IAiRuntimeScaleOutRequestStore store,
                string controlPlaneId,
                ProductionTenantScenarioDefinition tenant,
                string pipelineName,
                string sharedRunId,
                TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> requests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                requests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                SharedRunId = sharedRunId,
                                MaxResults = 10
                            })
                        .ConfigureAwait(false);

                var fulfilled =
                    requests
                        .Where(
                            request =>
                                request.Status ==
                                    AiRuntimeScaleOutRequestStatus.Fulfilled)
                        .OrderByDescending(request => request.FulfilledAtUtc)
                        .FirstOrDefault();

                if (fulfilled is not null)
                {
                    return fulfilled;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The production warm-up run did not expose a fulfilled typed scale-out request. ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}', PipelineName='{pipelineName}', SharedRunId='{sharedRunId}', ObservedRequestCount='{requests.Count}'.");
        }

        private static async Task AssertNoScaleOutRequestAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string sharedRunId,
            TimeSpan observationWindow)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(observationWindow);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var requests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                SharedRunId = sharedRunId,
                                MaxResults = 10
                            })
                        .ConfigureAwait(false);

                Assert.True(
                    requests.Count == 0,
                    $"Existing capacity reuse created an unexpected scale-out request. ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', RuntimeMode='{tenant.RuntimeMode}', PipelineName='{pipelineName}', SharedRunId='{sharedRunId}', Requests='{string.Join(",", requests.Select(request => $"{request.RequestId}:{request.Status}"))}'.");

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }
        }

        private static void AssertTypedAdmissionRequest(
            AiRuntimeScaleOutRequestRecord request,
            ProductionTenantScenarioDefinition tenant)
        {
            Assert.Equal(
                tenant.TenantId,
                request.TenantId);

            Assert.Equal(
                tenant.TenantGroupId,
                request.TenantGroupId);

            Assert.Equal(
                tenant.TenantId,
                request.ExecutionContextSnapshot.TenantId);

            Assert.Equal(
                tenant.TenantGroupId,
                request.ExecutionContextSnapshot.TenantGroupId);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolveIsolationMode(
                    tenant.RuntimeMode),
                request.IsolationMode);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(
                    tenant.RuntimeMode),
                request.PreferDedicatedCapacity);

            Assert.Equal(
                ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(
                    tenant.RuntimeMode),
                request.AllowSharedFallback);

            Assert.True(
                request.MaxRuntimeInstances.HasValue,
                "The typed scale-out request did not preserve MaxRuntimeInstances.");

            Assert.Equal(
                tenant.MaxRuntimeInstances,
                request.MaxRuntimeInstances.Value);

            Assert.Equal(
                tenant.RuntimeInstanceIdPrefix,
                request.RuntimeInstanceIdPrefix);
        }

        private static async Task AssertProbeWaveCompletedAsync(
            ProcessHostProductionTrafficPreludeContext context,
            IReadOnlyCollection<ProductionTrafficDispatchProof> dispatches)
        {
            foreach (var dispatch in dispatches)
            {
                var statuses =
                    await McpTestWaitHelpers
                        .WaitForTerminalRuntimeRunStatusesAsync(
                            dispatch.TenantContext.Mcp,
                            new[]
                            {
                                dispatch.Run
                            },
                            context.CompletionTimeout)
                        .ConfigureAwait(false);

                var status =
                    Assert.Single(statuses);

                Assert.True(
                    status.Success,
                    status.FailureReason ??
                    status.Message);

                Assert.True(
                    string.Equals(
                        status.RunState?.Status,
                        "completed",
                        StringComparison.OrdinalIgnoreCase),
                    $"Production probe did not complete successfully. SharedRunId='{dispatch.Run.SharedRunId}', RuntimeInstanceId='{dispatch.Run.AssignedRuntimeInstanceId}', LocalRunId='{dispatch.Run.LocalRunId}', Status='{status.RunState?.Status}', FailureReason='{status.RunState?.FailureReason ?? status.FailureReason}'.");
            }
        }

        private static async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
            WaitForReadyPoolSnapshotsAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                int expectedRuntimeCount,
                int expectedHostCount,
                TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeInstanceSnapshot> readySnapshots =
                Array.Empty<AiRuntimeInstanceSnapshot>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await registry
                        .ListAsync(includeStopped: false)
                        .ConfigureAwait(false);

                readySnapshots = snapshots
                    .Where(
                        snapshot =>
                            StringComparer.Ordinal.Equals(
                                snapshot.PoolId,
                                poolId) &&
                            snapshot.Status ==
                                AiRuntimeInstanceStatus.Ready &&
                            !string.IsNullOrWhiteSpace(snapshot.HostId))
                    .OrderBy(snapshot => snapshot.HostId, StringComparer.Ordinal)
                    .ThenBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray();

                var hostCount =
                    readySnapshots
                        .Select(snapshot => snapshot.HostId!)
                        .Distinct(StringComparer.Ordinal)
                        .Count();

                if (readySnapshots.Count == expectedRuntimeCount &&
                    hostCount == expectedHostCount)
                {
                    return readySnapshots;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The mixed-admission warm pool did not expose the exact ready topology. PoolId='{poolId}', ExpectedRuntimeCount='{expectedRuntimeCount}', ActualRuntimeCount='{readySnapshots.Count}', ExpectedHostCount='{expectedHostCount}', ActualHostCount='{readySnapshots.Select(snapshot => snapshot.HostId).Where(hostId => !string.IsNullOrWhiteSpace(hostId)).Distinct(StringComparer.Ordinal).Count()}'.");
        }

        private static int GetRuntimeModeOrder(
            ProductionTenantRuntimeMode runtimeMode)
        {
            return runtimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => 0,
                ProductionTenantRuntimeMode.Hybrid => 1,
                ProductionTenantRuntimeMode.Shared => 2,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(runtimeMode),
                    runtimeMode,
                    "Unsupported production tenant runtime mode.")
            };
        }

        private sealed record ProductionTrafficDispatchProof(
            ProcessHostProductionTrafficTenantContext TenantContext,
            Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store.AiSharedRunRecord Run,
            AiRuntimeInstanceSnapshot Snapshot);
    }

    /// <summary>
    /// Executes five isolated real-Pod failure scenarios concurrently and cleans each scenario's Pods on completion.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolPodFailureP5")]
    public sealed class GrpcKubernetesRuntimePoolPodFailureP5ScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        private const int Parallelism = 5;

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessScaleOutTimeoutOverride =>
            TimeSpan.FromMinutes(4);

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessProgressTimeoutOverride =>
            TimeSpan.FromMinutes(5);

        /// <summary>
        /// Initializes the gRPC Kubernetes Runtime Pool Pod-failure P5 proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolPodFailureP5ScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolPodFailureP5ScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies five independent real Pod deletions, complete durable recovery, safe-tenant continuity,
        /// and immediate per-scenario Pod cleanup.
        /// </summary>
        /// <returns>A task that completes when all five isolated scenarios have converged and cleaned their Pods.</returns>
        [Fact]
        public async Task Grpc_KubernetesPool_P5_Should_Fully_Recover_After_Five_Independent_Pod_Deletions()
        {
            try
            {
                await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                        Parallelism)
                    .ConfigureAwait(false);
            }
            finally
            {
                await CleanupAllTrackedPodsAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        protected override ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryScenario(
            bool includeSafeTenant = false)
        {
            var scenario =
                base.CreateRealRuntimeCrashRecoveryScenario(
                    includeSafeTenant: true);

            var impactedTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-a"));

            var safeTenant =
                scenario.Tenants.Single(
                    tenant =>
                        StringComparer.Ordinal.Equals(
                            tenant.TenantId,
                            "tenant-real-crash-safe"));

            return scenario with
            {
                Name =
                    "grpc-kubernetes-runtime-pool-pod-failure-p5",
                ControlPlaneIdPrefix =
                    "grpc-kubernetes-runtime-pool-pod-failure-p5",
                Tenants = includeSafeTenant
                    ? new[]
                    {
                        impactedTenant,
                        safeTenant
                    }
                    : new[]
                    {
                        impactedTenant
                    }
            };
        }
    }
}
