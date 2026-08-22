using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Signals;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Ledger.DI;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker.Chaos;
using Xunit;
using Xunit.Abstractions;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.SharedController
{
    /// <summary>
    /// End-to-end integration tests proving that the Shared Runtime Controller can
    /// launch real distributed DAG executions through the real local runtime queue adapter.
    /// </summary>
    /// <remarks>
    /// These tests validate the real path:
    ///
    /// SharedRuntimeController
    /// -> IAiRunAdmissionController
    /// -> LocalAiSharedRunDispatcher
    /// -> IAiRuntimeQueueControlPlane
    /// -> IAiRuntimePipelineBackgroundController
    /// -> distributed DAG workers
    /// -> Redis hot state
    /// -> retention / compaction / eviction
    /// -> snapshot persistence
    /// -> replay validation
    /// -> ledger / timeline / metrics.
    ///
    /// No fake shared run dispatcher is used here.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiSharedControllerExecutionIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public AiSharedControllerExecutionIntegrationTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Validates the complete control-plane execution path with a real 100-step distributed chaos run.
        /// </summary>
        [RedisFact]
        public async Task SharedController_Should_Run_Real_100_Step_Distributed_Chaos_With_Replay_Ledger_Timeline_And_Retention()
        {
            await RunSharedControllerExecutionScenarioAsync(
                SharedControllerExecutionScenario.Steps100());
        }

        /// <summary>
        /// Validates the complete control-plane execution path with a real 500-step distributed chaos run.
        /// </summary>
        [RedisFact]
        public async Task SharedController_Should_Run_Real_500_Step_Distributed_Chaos_With_Replay_Ledger_Timeline_And_Retention()
        {
            await RunSharedControllerExecutionScenarioAsync(
                SharedControllerExecutionScenario.Steps500());
        }

        /// <summary>
        /// Runs the complete Shared Runtime Controller to real runtime execution scenario.
        /// </summary>
        private async Task RunSharedControllerExecutionScenarioAsync(
            SharedControllerExecutionScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            await using var host = await CreateSharedControllerExecutionHostAsync(
                scenario);

            var backgroundController = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var queueControlPlane = host.ServiceProvider.GetRequiredService<IAiRuntimeQueueControlPlane>();
            var sharedController = host.ServiceProvider.GetRequiredService<IAiSharedRuntimeController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();
            var timeline = host.ServiceProvider.GetRequiredService<IAiTraceTimeline>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await backgroundController.StartAsync();

            string? executionId = null;

            try
            {
                var submit = await sharedController.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = $"shared-controller-real-{scenario.StepCount}-{Guid.NewGuid():N}",
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = scenario.PipelineName,
                            PipelineDefinition = scenario.PipelineDefinition,
                            Input = new
                            {
                                candidateId = scenario.CandidateId,
                                source = scenario.Name,
                                stepCount = scenario.StepCount,
                                workerCount = scenario.WorkerCount,
                                sharedController = true,
                                realAdapter = true,
                                retry = true,
                                retention = true,
                                compaction = true,
                                eviction = true,
                                throttling = true,
                                replay = true
                            }
                        },
                        TenantId = "tenant-shared-controller-execution",
                        PipelineKey = scenario.PipelineName,
                        CorrelationId = $"correlation-shared-controller-{Guid.NewGuid():N}",
                        RequestedBy = "integration-test",
                        Source = "shared-controller-execution-integration-test",
                        Reason = "Validate Shared Runtime Controller real execution adapter path.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = scenario.Name,
                            ["step.count"] = scenario.StepCount.ToString(),
                            ["worker.count"] = scenario.WorkerCount.ToString(),
                            ["control.plane"] = "shared-controller",
                            ["real.adapter"] = "true"
                        }
                    });

                Assert.True(
                    submit.Success,
                    submit.FailureReason ?? "Shared controller submit failed.");

                Assert.NotNull(submit.Run);
                Assert.Equal(AiSharedRunStatus.Dispatched, submit.Run.Status);
                Assert.Equal(SharedControllerExecutionAdmissionController.RuntimeInstanceId, submit.AssignedRuntimeInstanceId);
                Assert.False(string.IsNullOrWhiteSpace(submit.SharedRunId));
                Assert.False(string.IsNullOrWhiteSpace(submit.LocalRunId));

                var completedRecord = await WaitForRuntimeExecutionCompletionAsync(
                     queueControlPlane,
                     dagStore,
                     submit.LocalRunId!,
                     scenario.Timeout);

                Assert.NotNull(completedRecord);
                Assert.Equal(AiExecutionStatus.Completed, completedRecord.Status);
                Assert.False(string.IsNullOrWhiteSpace(completedRecord.ExecutionId));

                executionId = completedRecord.ExecutionId;

                var finalized = await finalizedHook.WaitAsync(
                    scenario.SnapshotWaitTimeout);

                Assert.Equal(executionId, finalized.ExecutionId);

                var snapshot = await snapshotStore.GetAsync(
                    executionId);

                Assert.NotNull(snapshot);
                Assert.Equal(executionId, snapshot!.ExecutionId);

                var recordBeforeReplay = await dagStore.GetRecordAsync(
                    executionId);

                var stateBeforeReplay = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(recordBeforeReplay);
                Assert.NotNull(stateBeforeReplay);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    recordBeforeReplay!.Status);

                Assert.Equal(
                    scenario.StepCount,
                    recordBeforeReplay.CompletedSteps.Count);

                await resolver.WarmAsync(
                    executionId,
                    stateBeforeReplay!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    stateBeforeReplay!,
                    resolver);

                AssertNoStaleClaims(
                    stateBeforeReplay!);

                AssertRetentionDidNotBreakState(
                    scenario,
                    stateBeforeReplay!);

                AssertDistributedWorkerMetrics(
                    scenario,
                    metrics);

                var beforeReplayFingerprint = await CreateReplayFingerprintAsync(
                    scenario,
                    executionId,
                    recordBeforeReplay!,
                    stateBeforeReplay!,
                    resolver);

                var ledgerBeforeReplay = await ledger.GetByExecutionAsync(
                    executionId);

                var timelineBeforeReplay = timeline.Get(
                    executionId);

                Assert.NotEmpty(ledgerBeforeReplay);
                Assert.NotEmpty(timelineBeforeReplay);

                PrintDecisionLedger(
                    "SHARED CONTROLLER EXECUTION LEDGER BEFORE REPLAY",
                    executionId,
                    ledgerBeforeReplay);

                AssertLedgerCategoryContainsAny(
                    ledgerBeforeReplay,
                    AiDecisionLedgerCategory.Execution,
                    "execution.");

                AssertLedgerCategoryContainsAny(
                    ledgerBeforeReplay,
                    AiDecisionLedgerCategory.Run,
                    "run.");

                AssertLedgerCategoryContainsAny(
                    ledgerBeforeReplay,
                    AiDecisionLedgerCategory.Claim,
                    "claim.");

                AssertLedgerCategoryContainsAny(
                    ledgerBeforeReplay,
                    AiDecisionLedgerCategory.Step,
                    "step.");

                AssertLedgerCategoryContainsAny(
                    ledgerBeforeReplay,
                    AiDecisionLedgerCategory.Finalization,
                    "finalization.");

                if (scenario.ExpectedRetriedSteps.Count > 0)
                {
                    AssertLedgerCategoryContainsAny(
                        ledgerBeforeReplay,
                        AiDecisionLedgerCategory.Retry,
                        "retry.");
                }

                if (scenario.RetentionPolicies.Count > 0)
                {
                    AssertLedgerCategoryContainsAny(
                        ledgerBeforeReplay,
                        AiDecisionLedgerCategory.Retention,
                        "retention.");
                }

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                Assert.Null(await dagStore.GetRecordAsync(executionId));
                Assert.Null(await dagStore.GetStateAsync(executionId));

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete,
                        IncludeLedgerEvents = true,
                        IncludeTimeline = true,
                        IncludeStepDetails = true,
                        ValidatePayloadReferences = true
                    });

                Assert.NotNull(replayResult);
                Assert.True(replayResult.ReplayValid);
                Assert.True(replayResult.ExecutionFound);
                Assert.True(replayResult.SnapshotFound);
                Assert.True(replayResult.FingerprintFound);
                Assert.True(replayResult.FingerprintMatches);
                Assert.True(replayResult.DependencyGraphValid);
                Assert.True(replayResult.StepStateValid);
                Assert.True(replayResult.PayloadReferencesValid);

                Assert.NotNull(replayResult.ReplayMetadata);
                Assert.Equal(executionId, replayResult.ReplayMetadata!.ExecutionId);
                Assert.False(string.IsNullOrWhiteSpace(replayResult.ReplayMetadata.Fingerprint));
                Assert.Equal("v1", replayResult.ReplayMetadata.FingerprintVersion);

                Assert.NotEmpty(replayResult.LedgerEvents);
                Assert.NotEmpty(replayResult.TimelineEvents);
                Assert.NotEmpty(replayResult.Steps);

                PrintDecisionLedger(
                    "SHARED CONTROLLER REPLAY REPORT LEDGER EVENTS",
                    executionId,
                    replayResult.LedgerEvents);

                var ledgerAfterReplay = await ledger.GetByExecutionAsync(
                    executionId);

                Assert.NotEmpty(ledgerAfterReplay);

                PrintDecisionLedger(
                    "SHARED CONTROLLER LEDGER AFTER REPLAY",
                    executionId,
                    ledgerAfterReplay);

                AssertLedgerContains(
                    ledgerAfterReplay,
                    AiDecisionLedgerCategory.Replay,
                    AiEngineEvents.Replay.Requested);

                AssertLedgerContains(
                    ledgerAfterReplay,
                    AiDecisionLedgerCategory.Replay,
                    AiEngineEvents.Replay.Started);

                AssertLedgerContains(
                    ledgerAfterReplay,
                    AiDecisionLedgerCategory.Replay,
                    AiEngineEvents.Replay.ComparisonCompleted);

                AssertLedgerContains(
                    ledgerAfterReplay,
                    AiDecisionLedgerCategory.Replay,
                    AiEngineEvents.Replay.Completed);

                var restoredRecord = await dagStore.GetRecordAsync(
                    executionId);

                var restoredState = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(restoredRecord);
                Assert.NotNull(restoredState);

                await resolver.WarmAsync(
                    executionId,
                    restoredState!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    restoredState!,
                    resolver);

                AssertNoStaleClaims(
                    restoredState!);

                var afterReplayFingerprint = await CreateReplayFingerprintAsync(
                    scenario,
                    executionId,
                    restoredRecord!,
                    restoredState!,
                    resolver);

                Assert.Equal(beforeReplayFingerprint.Status, afterReplayFingerprint.Status);
                Assert.Equal(beforeReplayFingerprint.IsTerminal, afterReplayFingerprint.IsTerminal);
                Assert.Equal(beforeReplayFingerprint.CompletedSteps, afterReplayFingerprint.CompletedSteps);
                Assert.Equal(beforeReplayFingerprint.StepStatuses, afterReplayFingerprint.StepStatuses);
                Assert.Equal(beforeReplayFingerprint.RetryCounts, afterReplayFingerprint.RetryCounts);
                Assert.Equal(beforeReplayFingerprint.RequiredResolvedSteps, afterReplayFingerprint.RequiredResolvedSteps);

                _output.WriteLine("");
                _output.WriteLine("============================================================");
                _output.WriteLine("SHARED CONTROLLER REAL EXECUTION TEST");
                _output.WriteLine("============================================================");
                _output.WriteLine($"Scenario:             {scenario.Name}");
                _output.WriteLine($"SharedRunId:          {submit.SharedRunId}");
                _output.WriteLine($"LocalRunId:           {submit.LocalRunId}");
                _output.WriteLine($"ExecutionId:          {executionId}");
                _output.WriteLine($"Pipeline:             {scenario.PipelineName}");
                _output.WriteLine($"Steps:                {scenario.StepCount}");
                _output.WriteLine($"Workers:              {scenario.WorkerCount}");
                _output.WriteLine($"ReplayValid:          {replayResult.ReplayValid}");
                _output.WriteLine($"FingerprintMatches:   {replayResult.FingerprintMatches}");
                _output.WriteLine($"LedgerEvents:         {ledgerBeforeReplay.Count}");
                _output.WriteLine($"TimelineEvents:       {timelineBeforeReplay.Count}");
                _output.WriteLine($"ReplayLedgerEvents:   {replayResult.LedgerEvents.Count}");
                _output.WriteLine($"ReplayTimelineEvents: {replayResult.TimelineEvents.Count}");
            }
            finally
            {
                await backgroundController.StopAsync();

                if (!string.IsNullOrWhiteSpace(executionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        executionId);
                }
            }
        }

        /// <summary>
        /// Prints a decision ledger summary and chronological timeline.
        /// </summary>
        private void PrintDecisionLedger(
            string title,
            string executionId,
            IReadOnlyCollection<AiDecisionLedgerEntry> entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(entries);

            _output.WriteLine("");
            _output.WriteLine("============================================================");
            _output.WriteLine(title);
            _output.WriteLine("============================================================");
            _output.WriteLine($"ExecutionId: {executionId}");
            _output.WriteLine($"Events:      {entries.Count}");
            _output.WriteLine("");

            _output.WriteLine("SUMMARY BY CATEGORY / EVENT / OUTCOME");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var group in entries
                .GroupBy(entry => new
                {
                    entry.Category,
                    entry.EventType,
                    entry.Outcome
                })
                .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Outcome.ToString(), StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{group.Key.Category,-16} | " +
                    $"{group.Key.EventType,-45} | " +
                    $"{group.Key.Outcome,-14} | " +
                    $"Count={group.Count()}");
            }

            _output.WriteLine("");
            _output.WriteLine("TIMELINE");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var entry in entries
                .OrderBy(entry => entry.TimestampUtc)
                .ThenBy(entry => entry.Category.ToString(), StringComparer.Ordinal)
                .ThenBy(entry => entry.EventType, StringComparer.Ordinal))
            {
                var metadata = entry.Metadata is null || entry.Metadata.Count == 0
                    ? string.Empty
                    : string.Join(
                        ", ",
                        entry.Metadata
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"{pair.Key}={pair.Value}"));

                _output.WriteLine(
                    $"{entry.TimestampUtc:O} | " +
                    $"{entry.Category,-16} | " +
                    $"{entry.EventType,-45} | " +
                    $"{entry.Outcome,-14} | " +
                    $"ExecutionId={entry.CorrelationContext.ExecutionId ?? string.Empty} | " +
                    $"RunId={entry.CorrelationContext.RunId ?? string.Empty} | " +
                    $"CorrelationId={entry.CorrelationContext.CorrelationId ?? string.Empty} | " +
                    $"StepId={entry.CorrelationContext.StepId ?? string.Empty} | " +
                    $"StepKey={entry.CorrelationContext.StepKey ?? string.Empty} | " +
                    $"Worker={entry.CorrelationContext.WorkerId ?? string.Empty} | " +
                    $"ClaimToken={entry.CorrelationContext.ClaimToken ?? string.Empty} | " +
                    $"Reason={entry.Reason ?? string.Empty} | " +
                    $"Metadata=[{metadata}]");
            }
        }

        private static void AssertLedgerCategoryContainsAny(
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            AiDecisionLedgerCategory category,
            string eventPrefix)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventPrefix);

            var matching = entries
                .Where(entry =>
                    entry.Category == category &&
                    entry.EventType.StartsWith(
                        eventPrefix,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matching.Length > 0)
            {
                return;
            }

            var available = entries
                .GroupBy(entry => new
                {
                    entry.Category,
                    entry.EventType
                })
                .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                .Select(group => $"{group.Key.Category}:{group.Key.EventType}={group.Count()}")
                .ToArray();

            throw new Xunit.Sdk.XunitException(
                $"Expected at least one ledger event with Category='{category}' and EventType prefix='{eventPrefix}', " +
                $"but none was found. Available events: {string.Join(", ", available)}");
        }

        private static async Task<AiDagExecutionEngineTestHost> CreateSharedControllerExecutionHostAsync(
    SharedControllerExecutionScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var controlPlaneId =
                $"shared-controller-execution-{Guid.NewGuid():N}";

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(
                    scenario,
                    controlPlaneId),
                configureServices: services =>
                {
                    var finalizedHook = new DistributedChaosRunFinalizedHook();

                    services.AddSingleton(finalizedHook);
                    services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                        finalizedHook);

                    services.AddInMemoryAiDecisionLedger();

                    services.RemoveAll<IAiControlPlaneIdResolver>();
                    services.AddSingleton<IAiControlPlaneIdResolver>(
                        new StaticAiControlPlaneIdResolver(controlPlaneId));

                    services.AddAiControlPlane();

                    services.RemoveAll<IExecutionContextSnapshotProvider>();
                    services.AddSingleton<IExecutionContextSnapshotProvider>(
                        new StaticExecutionContextSnapshotProvider(
                            CreateExecutionContextSnapshot(
                                tenantId: "tenant-shared-controller-execution",
                                userId: "integration-test")));

                    services.RemoveAll<IAiRunAdmissionController>();
                    services.AddSingleton<IAiRunAdmissionController>(
                        new SharedControllerExecutionAdmissionController(
                            controlPlaneId));

                    services.AddSingleton<
                        IAiRuntimeSignalPublisher,
                        RedisAiRuntimeSignalPublisher>();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiSharedControllerExecutionIntegrationTests).Assembly,
                        typeof(DistributedChaosFlakyProviderStep).Assembly);
                });
        }

        private static AiEngineOptions CreateOptions(
             SharedControllerExecutionScenario scenario,
             string controlPlaneId)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Runtime",
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = scenario.MaxStepsPerCycle,
                    IdleDelay = scenario.WorkerIdleDelay,
                    MaxCycles = scenario.MaxWorkerCycles,
                    IgnoreConcurrencyConflicts = true
                },
                PipelineBackgroundController = new AiRuntimePipelineBackgroundControllerOptions
                {
                    MaxConcurrentRuns = 1,
                    QueueCapacity = 8,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false,
                    MaxLocalWorkersPerExecution = 5,
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = scenario.WorkerCount,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(60)
                    }
                }
            };

            options.ControlPlane.ControlPlaneId = controlPlaneId;

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;
            options.Observability.EnableMetrics = true;

            options.Observability.DecisionLedger.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
            options.Observability.DecisionLedger.StorageMode = AiDecisionLedgerStorageMode.InMemory;

            options.Snapshots.Enabled = true;
            options.Snapshots.Mongo.Enabled = true;
            options.Snapshots.Mongo.ConnectionString = "mongodb://localhost:27017";
            options.Snapshots.Mongo.DatabaseName = "multiplexed_ai_tests";
            options.Snapshots.Mongo.CollectionName =
                $"execution_snapshots_shared_controller_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        private static async Task<AiExecutionRecord> WaitForRuntimeExecutionCompletionAsync(
            IAiRuntimeQueueControlPlane queueControlPlane,
            IAiDagExecutionStore dagStore,
            string runId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(queueControlPlane);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            string? executionId = null;
            string? lastRunStatus = null;
            AiExecutionStatus? lastExecutionStatus = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var result = await queueControlPlane.GetRunStatusAsync(
                    new AiRuntimeQueueControlPlaneRequest
                    {
                        Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                        RunId = runId,
                        RequestedBy = "integration-test",
                        Source = "shared-controller-execution-integration-test"
                    });

                if (result.RunState is not null)
                {
                    lastRunStatus = result.RunState.Status;

                    if (!string.IsNullOrWhiteSpace(result.RunState.ExecutionId))
                    {
                        executionId = result.RunState.ExecutionId;
                    }

                    if (string.Equals(lastRunStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Runtime run '{runId}' reached terminal failure status '{lastRunStatus}'. FailureReason='{result.RunState.FailureReason}'.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(executionId))
                {
                    var record = await dagStore.GetRecordAsync(
                        executionId);

                    if (record is not null)
                    {
                        lastExecutionStatus = record.Status;

                        if (record.Status == AiExecutionStatus.Completed)
                        {
                            return record;
                        }

                        if (record.Status == AiExecutionStatus.Failed ||
                            record.Status == AiExecutionStatus.Cancelled)
                        {
                            throw new InvalidOperationException(
                                $"Execution '{executionId}' reached terminal failure status '{record.Status}'.");
                        }
                    }
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100));
            }

            throw new TimeoutException(
                $"Runtime run '{runId}' did not complete within '{timeout}'. LastRunStatus='{lastRunStatus ?? "none"}', ExecutionId='{executionId ?? "none"}', LastExecutionStatus='{lastExecutionStatus?.ToString() ?? "none"}'.");
        }

        private static async Task CleanupDagExecutionAsync(
            IServiceProvider serviceProvider,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var dagStore = serviceProvider.GetRequiredService<IAiDagExecutionStore>();

            await dagStore.DeleteExecutionBundleAsync(
                executionId);
        }

        private static void AssertLedgerContains(
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            AiDecisionLedgerCategory category,
            string eventType)
        {
            Assert.Contains(entries, entry =>
                entry.Category == category &&
                entry.EventType == eventType);
        }

        private static void AssertNoStaleClaims(
            AiExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.Null(step.ClaimedBy);
                    Assert.Null(step.ClaimToken);
                    Assert.Null(step.ClaimedAtUtc);
                    Assert.Null(step.LeaseExpiresAtUtc);
                });
        }

        private static void AssertRetentionDidNotBreakState(
            SharedControllerExecutionScenario scenario,
            AiExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(state);

            Assert.NotNull(state.Steps);

            Assert.True(
                state.Steps.Count <= scenario.StepCount);

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.True(
                        step.Status == AiStepExecutionStatus.Completed ||
                        step.Status == AiStepExecutionStatus.Failed);
                });
        }

        private void AssertDistributedWorkerMetrics(
            SharedControllerExecutionScenario scenario,
            IAiRuntimeMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(metrics);

            var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

            Assert.NotEmpty(workerCycles);

            Assert.True(
                workerCycles.Count >= scenario.MinimumExpectedParticipatingWorkers,
                $"Expected at least '{scenario.MinimumExpectedParticipatingWorkers}' distributed runtime workers to participate.");

            foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"RuntimeInstanceId='{item.Key}', Cycles='{item.Value}'.");
            }
        }

        private static async Task AssertRequiredStepsResolvedAsync(
            SharedControllerExecutionScenario scenario,
            string executionId,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                Assert.Equal(
                    AiStepExecutionStatus.Completed,
                    step!.Status);
            }

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);

                Assert.True(
                    step.RetryState?.RetryCount >= 1,
                    $"Expected retried step '{stepName}' to have RetryCount >= 1.");
            }
        }

        private static async Task<ReplayFingerprint> CreateReplayFingerprintAsync(
            SharedControllerExecutionScenario scenario,
            string executionId,
            AiExecutionRecord record,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            var selectedSteps = scenario.FullStepFingerprint
                ? scenario.PipelineDefinition.Steps
                : scenario.PipelineDefinition.Steps.Where(step =>
                    !string.IsNullOrWhiteSpace(step.Name) &&
                    scenario.FingerprintStepNames.Contains(
                        step.Name,
                        StringComparer.Ordinal));

            var stepStatuses = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var step in selectedSteps)
            {
                if (string.IsNullOrWhiteSpace(step.Name))
                {
                    continue;
                }

                var status = await resolver.GetStepStatusAsync(
                    executionId,
                    step.Name,
                    state,
                    CancellationToken.None);

                Assert.NotNull(status);

                stepStatuses[step.Name] = status!.Status.ToString();
            }

            var retryCounts = new SortedDictionary<string, int>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                retryCounts[stepName] =
                    step!.RetryState?.RetryCount ?? 0;
            }

            var required = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                required[stepName] = step!.Status.ToString();
            }

            return new ReplayFingerprint
            {
                Status = record.Status.ToString(),
                IsTerminal = record.IsTerminal,
                CompletedSteps = record.CompletedSteps
                    .OrderBy(step => step, StringComparer.Ordinal)
                    .ToArray(),
                StepStatuses = stepStatuses,
                RetryCounts = retryCounts,
                RequiredResolvedSteps = required
            };
        }

        private static AiPipelineDefinition CreatePipelineDefinition(
            SharedControllerExecutionScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "chaos-step-001",
                    StepKey = "hello-world",
                    Order = 1,
                    Config = CreateStepConfig(
                        scenario,
                        index: 1,
                        isFlaky: false)
                }
            };

            for (var index = 2; index < scenario.StepCount; index++)
            {
                var isFlaky =
                    index % scenario.FlakyStepInterval == 0;

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = $"chaos-step-{index:000}",
                        StepKey = isFlaky
                            ? "distributed.chaos.flaky-provider"
                            : "hello-world",
                        Order = index,
                        DependsOn = new[] { "chaos-step-001" },
                        Config = CreateStepConfig(
                            scenario,
                            index,
                            isFlaky)
                    });
            }

            steps.Add(
                new AiPipelineStepDefinition
                {
                    Name = "final-join-step",
                    StepKey = "hello-world",
                    Order = scenario.StepCount,
                    DependsOn = Enumerable.Range(2, scenario.StepCount - 2)
                        .Select(index => $"chaos-step-{index:000}")
                        .ToArray(),
                    Config = new Dictionary<string, object?>
                    {
                        ["provider"] = "openai",
                        ["model"] = "gpt-4.1",
                        ["operation"] = "llm.compose",
                        ["delayMs"] = 5
                    }
                });

            return new AiPipelineDefinition
            {
                Name = scenario.PipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxDegreeOfParallelism"] = scenario.MaxDegreeOfParallelism,
                        ["maxProviderConcurrency"] = scenario.MaxProviderConcurrency,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 10,
                        ["jitter"] = false
                    },
                    ["retention"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["policies"] = scenario.RetentionPolicies.ToArray(),
                        ["archiveReason"] = scenario.RetentionArchiveReason,
                        ["trigger"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["maxStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxCompletedStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxInlinePayloadBytes"] = scenario.MaxInlinePayloadBytes
                        }
                    }
                },
                Steps = steps
            };
        }

        private static Dictionary<string, object?> CreateStepConfig(
            SharedControllerExecutionScenario scenario,
            int index,
            bool isFlaky)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var config = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["delayMs"] = isFlaky ? 10 : 1
            };

            if (isFlaky)
            {
                config["attemptKey"] =
                    $"{scenario.PipelineName}:chaos-step-{index:000}";

                config["retry"] = new Dictionary<string, object?>
                {
                    ["maxRetries"] = 2,
                    ["strategy"] = "Fixed",
                    ["baseDelayMs"] = 15,
                    ["maxDelayMs"] = 15,
                    ["jitter"] = false
                };
            }

            return config;
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
    string tenantId,
    string userId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);

            return new ExecutionContextSnapshot
            {
                ContextKey = Guid.NewGuid().ToString("N"),
                Project = "distributed-deterministic-ai-runtime",
                UserId = userId,
                TenantId = tenantId,
                TenantGroupId = "tenant-group-shared-controller-execution",
                CurrentNamespace = "mcp-ai-runtime",
                Namespaces = new List<NamespaceEntry>
        {
            new()
            {
                Name = "mcp-ai-runtime",
                Trns = new HashSet<string>
                {
                    "trn:distributed-deterministic-ai-runtime:shared-run:execution:submit",
                    "trn:distributed-deterministic-ai-runtime:shared-run:registry:read",
                    "trn:distributed-deterministic-ai-runtime:shared-run:registry:list",
                    "trn:distributed-deterministic-ai-runtime:shared-queue:queue:list",
                    "trn:distributed-deterministic-ai-runtime:shared-queue:status:read",
                    "trn:distributed-deterministic-ai-runtime:shared-queue:pump:drain"
                }
            }
        },
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private sealed class StaticExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            public StaticExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot = snapshot
                    ?? throw new ArgumentNullException(nameof(snapshot));
            }

            public ExecutionContextSnapshot MapToSnapshot()
            {
                return snapshot;
            }
        }

        private sealed class SharedControllerExecutionAdmissionController : IAiRunAdmissionController
        {
            public const string RuntimeInstanceId = "runtime-chaos-1";

            private readonly string _controlPlaneId;

            public SharedControllerExecutionAdmissionController(
                string controlPlaneId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

                _controlPlaneId = controlPlaneId;
            }

            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                var metadata =
                    new Dictionary<string, string>(
                        request.Metadata,
                        StringComparer.Ordinal)
                    {
                        ["controlPlaneId"] = _controlPlaneId
                    };

                return Task.FromResult(new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = RuntimeInstanceId,
                    Reason = "Shared controller execution integration test assigned the run to the local runtime adapter.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 1,
                    CurrentInstanceCount = 1,
                    MaxInstanceCount = 1,
                    Metadata = metadata
                });
            }
        }

        private sealed class SharedControllerExecutionScenario
        {
            public required string Name { get; init; }

            public required string PipelineName { get; init; }

            public required string CandidateId { get; init; }

            public required string RetentionArchiveReason { get; init; }

            public AiPipelineDefinition PipelineDefinition { get; set; } = null!;

            public int StepCount { get; init; }

            public int WorkerCount { get; init; }

            public int MaxStepsPerCycle { get; init; }

            public int MaxWorkerCycles { get; init; }

            public int MaxDegreeOfParallelism { get; init; }

            public int MaxProviderConcurrency { get; init; }

            public int MaxCompletedStepsInState { get; init; }

            public int MaxInlinePayloadBytes { get; init; }

            public int FlakyStepInterval { get; init; }

            public int MinimumExpectedParticipatingWorkers { get; init; }

            public bool FullStepFingerprint { get; init; } = true;

            public TimeSpan WorkerIdleDelay { get; init; }

            public TimeSpan Timeout { get; init; }

            public TimeSpan SnapshotWaitTimeout { get; init; }

            public IReadOnlyCollection<string> RequiredResolvedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> ExpectedRetriedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> FingerprintStepNames { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> RetentionPolicies { get; init; } =
                Array.Empty<string>();

            public static SharedControllerExecutionScenario Steps100()
            {
                var pipelineName = $"shared-controller-chaos-100-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-090",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 98)
                    .Where(index => index % 9 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new SharedControllerExecutionScenario
                {
                    Name = "shared-controller-chaos-100",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-shared-controller-chaos-100",
                    RetentionArchiveReason = "shared-controller-chaos-100-retention",
                    StepCount = 100,
                    WorkerCount = 10,
                    MaxStepsPerCycle = 1,
                    MaxWorkerCycles = 5000,
                    MaxDegreeOfParallelism = 12,
                    MaxProviderConcurrency = 3,
                    MaxCompletedStepsInState = 15,
                    MaxInlinePayloadBytes = 1,
                    FlakyStepInterval = 9,
                    MinimumExpectedParticipatingWorkers = 3,
                    FullStepFingerprint = true,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    SnapshotWaitTimeout = TimeSpan.FromSeconds(60),
                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    },
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            public static SharedControllerExecutionScenario Steps500()
            {
                var pipelineName = $"shared-controller-chaos-500-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-011",
                    "chaos-step-022",
                    "chaos-step-099",
                    "chaos-step-250",
                    "chaos-step-499",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 498)
                    .Where(index => index % 11 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new SharedControllerExecutionScenario
                {
                    Name = "shared-controller-chaos-500",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-shared-controller-chaos-500",
                    RetentionArchiveReason = "shared-controller-chaos-500-retention",
                    StepCount = 500,
                    WorkerCount = 30,
                    MaxStepsPerCycle = 5,
                    MaxWorkerCycles = 10000,
                    MaxDegreeOfParallelism = 64,
                    MaxProviderConcurrency = 12,
                    MaxCompletedStepsInState = 50,
                    MaxInlinePayloadBytes = 1,
                    FlakyStepInterval = 11,
                    MinimumExpectedParticipatingWorkers = 5,
                    FullStepFingerprint = false,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromMinutes(10),
                    SnapshotWaitTimeout = TimeSpan.FromMinutes(3),
                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    },
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }
        }

        private sealed class ReplayFingerprint
        {
            public required string Status { get; init; }

            public required bool IsTerminal { get; init; }

            public required IReadOnlyList<string> CompletedSteps { get; init; }

            public required IReadOnlyDictionary<string, string> StepStatuses { get; init; }

            public required IReadOnlyDictionary<string, int> RetryCounts { get; init; }

            public required IReadOnlyDictionary<string, string> RequiredResolvedSteps { get; init; }
        }
    }
}