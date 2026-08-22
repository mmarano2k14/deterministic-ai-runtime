using Microsoft.Extensions.DependencyInjection;
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
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Ledger.DI;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker.Chaos;
using Xunit;
using Xunit.Abstractions;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Persistence.Replay
{
    /// <summary>
    /// Reference replay integration tests validating the complete replay stack
    /// against a real distributed 100-step runtime execution.
    /// </summary>
    /// <remarks>
    /// This test class is intentionally end-to-end. It validates that replay is not a mock:
    /// it runs a distributed DAG execution, persists a terminal snapshot, deletes the live
    /// execution bundle, restores from replay, validates deterministic fingerprints, and
    /// verifies replay metadata, decision ledger events, and trace timeline events.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiExecutionReplayReferenceIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public AiExecutionReplayReferenceIntegrationTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        [RedisFact]
        public async Task Replay_Should_Restore_100_Step_Distributed_Chaos_With_Metadata_Ledger_Timeline_And_Fingerprint()
        {
            var scenario = ReplayReferenceScenario.Steps100();

            await using var host = await CreateReplayReferenceHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();
            var timeline = host.ServiceProvider.GetRequiredService<IAiTraceTimeline>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                 AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                     pipelineName: scenario.PipelineName,
                     pipelineDefinition: scenario.PipelineDefinition,
                     input: new
                     {
                         candidateId = scenario.CandidateId,
                         source = scenario.Name,
                         stepCount = scenario.StepCount,
                         workerCount = scenario.WorkerCount,
                         replayReference = true
                     },
                     source: scenario.Name));

                Assert.NotNull(handle);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);

                var executionId = handle.ExecutionId!;

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

                Assert.Contains(
                    replayResult.LedgerEvents,
                    entry =>
                        entry.Category == AiDecisionLedgerCategory.Replay &&
                        entry.EventType == AiEngineEvents.Replay.Requested);

                Assert.Contains(
                    replayResult.LedgerEvents,
                    entry =>
                        entry.Category == AiDecisionLedgerCategory.Replay &&
                        entry.EventType == AiEngineEvents.Replay.Started);

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

                var ledgerAfterReplay = await ledger.GetByExecutionAsync(
                    executionId);

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

                var replayEvents = ledgerAfterReplay
                    .Where(entry => entry.Category == AiDecisionLedgerCategory.Replay)
                    .OrderBy(entry => entry.TimestampUtc)
                    .ToArray();

                _output.WriteLine("");
                _output.WriteLine("============================================================");
                _output.WriteLine("REFERENCE REPLAY TEST");
                _output.WriteLine("============================================================");
                _output.WriteLine($"ExecutionId:              {executionId}");
                _output.WriteLine($"RunId:                    {handle.RunId}");
                _output.WriteLine($"Pipeline:                 {scenario.PipelineName}");
                _output.WriteLine($"Steps:                    {scenario.StepCount}");
                _output.WriteLine($"Workers:                  {scenario.WorkerCount}");
                _output.WriteLine($"ReplayValid:              {replayResult.ReplayValid}");
                _output.WriteLine($"FingerprintMatches:       {replayResult.FingerprintMatches}");
                _output.WriteLine($"LedgerEventsInReport:     {replayResult.LedgerEvents.Count}");
                _output.WriteLine($"TimelineEventsInReport:   {replayResult.TimelineEvents.Count}");
                _output.WriteLine($"ReplayLedgerEventsTotal:  {replayEvents.Length}");
                _output.WriteLine("");

                _output.WriteLine("REPLAY LEDGER EVENTS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in replayEvents)
                {
                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.EventType,-40} | " +
                        $"{entry.Outcome,-12} | " +
                        $"Worker={entry.CorrelationContext.WorkerId} | " +
                        $"Reason={entry.Reason}");
                }

                _output.WriteLine("");
                _output.WriteLine("REPLAY REPORT SUMMARY");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"TotalSteps:               {replayResult.TotalSteps}");
                _output.WriteLine($"CompletedSteps:           {replayResult.CompletedSteps}");
                _output.WriteLine($"FailedSteps:              {replayResult.FailedSteps}");
                _output.WriteLine($"WaitingForRetrySteps:     {replayResult.WaitingForRetrySteps}");
                _output.WriteLine($"RunningSteps:             {replayResult.RunningSteps}");
                _output.WriteLine($"RetryCount:               {replayResult.RetryCount}");
                _output.WriteLine($"RecoveryCount:            {replayResult.RecoveryCount}");
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        [RedisFact]
        public async Task Replay_Should_Audit_Without_Restoring_State()
        {
            var scenario = ReplayReferenceScenario.Steps100();

            await using var host = await CreateReplayReferenceHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            auditOnly = true
                        },
                        source: scenario.Name));

                var final = await handle.Completion.WaitAsync(scenario.Timeout);

                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var finalized = await finalizedHook.WaitAsync(scenario.SnapshotWaitTimeout);

                Assert.Equal(executionId, finalized.ExecutionId);

                await CleanupDagExecutionAsync(host.ServiceProvider, executionId);

                Assert.Null(await dagStore.GetRecordAsync(executionId));
                Assert.Null(await dagStore.GetStateAsync(executionId));

                var report = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId,
                        Mode = AiExecutionReplayMode.AuditOnly,
                        IncludeLedgerEvents = true,
                        IncludeTimeline = true,
                        IncludeStepDetails = true,
                        ValidatePayloadReferences = true
                    });

                Assert.True(report.ReplayValid);
                Assert.True(report.SnapshotFound);
                Assert.True(report.FingerprintMatches);
                Assert.NotNull(report.ReplayMetadata);

                Assert.Null(await dagStore.GetRecordAsync(executionId));
                Assert.Null(await dagStore.GetStateAsync(executionId));
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(host.ServiceProvider, handle.ExecutionId);
                }
            }
        }

        [RedisFact]
        public async Task Replay_Should_Fail_When_Snapshot_Does_Not_Exist()
        {
            var scenario = ReplayReferenceScenario.Steps100();

            await using var host = await CreateReplayReferenceHostAsync(scenario);

            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();

            var executionId = Guid.NewGuid().ToString("N");

            var report = await replayService.ReplayAsync(
                new AiExecutionReplayRequest
                {
                    ExecutionId = executionId,
                    Mode = AiExecutionReplayMode.AuditOnly,
                    IncludeLedgerEvents = true,
                    IncludeTimeline = true,
                    IncludeStepDetails = true,
                    ValidatePayloadReferences = true
                });

            Assert.False(report.ReplayValid);
            Assert.False(report.ExecutionFound);
            Assert.False(report.SnapshotFound);
            Assert.Equal("Snapshot not found.", report.FailureReason);
            Assert.NotEmpty(report.Issues);
        }

        [RedisFact]
        public async Task Replay_Should_Not_Load_Ledger_Or_Timeline_When_Not_Requested()
        {
            var scenario = ReplayReferenceScenario.Steps100();

            await using var host = await CreateReplayReferenceHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            noObservabilityPayload = true
                        },
                        source: scenario.Name));

                var final = await handle.Completion.WaitAsync(scenario.Timeout);

                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var finalized = await finalizedHook.WaitAsync(scenario.SnapshotWaitTimeout);

                Assert.Equal(executionId, finalized.ExecutionId);

                await CleanupDagExecutionAsync(host.ServiceProvider, executionId);

                var report = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete,
                        IncludeLedgerEvents = false,
                        IncludeTimeline = false,
                        IncludeStepDetails = true,
                        ValidatePayloadReferences = true
                    });

                Assert.True(report.ReplayValid);
                Assert.Empty(report.LedgerEvents);
                Assert.Empty(report.TimelineEvents);
                Assert.NotEmpty(report.Steps);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(host.ServiceProvider, handle.ExecutionId);
                }
            }
        }

        [RedisFact]
        public async Task Replay_Should_Print_Ledger_And_Timeline_Report()
        {
            var scenario = ReplayReferenceScenario.Steps100();

            await using var host = await CreateReplayReferenceHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                     AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                         pipelineName: scenario.PipelineName,
                         pipelineDefinition: scenario.PipelineDefinition,
                         input: new
                         {
                             candidateId = scenario.CandidateId,
                             source = scenario.Name,
                             replayDiagnostic = true
                         },
                         source: scenario.Name));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var finalized = await finalizedHook.WaitAsync(
                    scenario.SnapshotWaitTimeout);

                Assert.Equal(executionId, finalized.ExecutionId);

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                var report = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete,
                        IncludeLedgerEvents = true,
                        IncludeTimeline = true,
                        IncludeStepDetails = true,
                        ValidatePayloadReferences = true
                    });

                Assert.True(report.ReplayValid);
                Assert.NotEmpty(report.LedgerEvents);
                Assert.NotEmpty(report.TimelineEvents);

                _output.WriteLine("");
                _output.WriteLine("============================================================");
                _output.WriteLine("REPLAY DIAGNOSTIC REPORT");
                _output.WriteLine("============================================================");
                _output.WriteLine($"ExecutionId:            {report.ExecutionId}");
                _output.WriteLine($"Mode:                   {report.Mode}");
                _output.WriteLine($"PipelineName:           {report.PipelineName}");
                _output.WriteLine($"Status:                 {report.Status}");
                _output.WriteLine($"ReplayValid:            {report.ReplayValid}");
                _output.WriteLine($"FingerprintFound:       {report.FingerprintFound}");
                _output.WriteLine($"FingerprintMatches:     {report.FingerprintMatches}");
                _output.WriteLine($"DependencyGraphValid:   {report.DependencyGraphValid}");
                _output.WriteLine($"StepStateValid:         {report.StepStateValid}");
                _output.WriteLine($"PayloadReferencesValid: {report.PayloadReferencesValid}");
                _output.WriteLine($"TotalSteps:             {report.TotalSteps}");
                _output.WriteLine($"CompletedSteps:         {report.CompletedSteps}");
                _output.WriteLine($"FailedSteps:            {report.FailedSteps}");
                _output.WriteLine($"WaitingForRetrySteps:   {report.WaitingForRetrySteps}");
                _output.WriteLine($"RunningSteps:           {report.RunningSteps}");
                _output.WriteLine($"RetryCount:             {report.RetryCount}");
                _output.WriteLine($"RecoveryCount:          {report.RecoveryCount}");
                _output.WriteLine($"Issues:                 {report.Issues.Count}");
                _output.WriteLine($"StepReports:            {report.Steps.Count}");
                _output.WriteLine($"LedgerEvents:           {report.LedgerEvents.Count}");
                _output.WriteLine($"TimelineEvents:         {report.TimelineEvents.Count}");

                if (report.ReplayMetadata is not null)
                {
                    _output.WriteLine("");
                    _output.WriteLine("REPLAY METADATA");
                    _output.WriteLine("------------------------------------------------------------");
                    _output.WriteLine($"Metadata.ExecutionId:        {report.ReplayMetadata.ExecutionId}");
                    _output.WriteLine($"Metadata.FingerprintVersion: {report.ReplayMetadata.FingerprintVersion}");
                    _output.WriteLine($"Metadata.GeneratedAtUtc:     {report.ReplayMetadata.GeneratedAtUtc:O}");
                    _output.WriteLine($"Metadata.Fingerprint:        {report.ReplayMetadata.Fingerprint}");
                }

                _output.WriteLine("");
                _output.WriteLine("LEDGER SUMMARY BY CATEGORY / EVENT / OUTCOME");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in report.LedgerEvents
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
                        $"{group.Key.Category,-16} | {group.Key.EventType,-40} | {group.Key.Outcome,-12} | Count={group.Count()}");
                }

                _output.WriteLine("");
                _output.WriteLine("REPLAY LEDGER EVENTS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in report.LedgerEvents
                    .Where(entry => entry.Category == AiDecisionLedgerCategory.Replay)
                    .OrderBy(entry => entry.TimestampUtc))
                {
                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.EventType,-40} | " +
                        $"{entry.Outcome,-12} | " +
                        $"Worker={entry.CorrelationContext.WorkerId} | " +
                        $"StepId={entry.CorrelationContext.StepId} | " +
                        $"StepKey={entry.CorrelationContext.StepKey} | " +
                        $"Reason={entry.Reason}");
                }

                _output.WriteLine("");
                _output.WriteLine("TRACE SUMMARY BY CATEGORY / NAME");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in report.TimelineEvents
                    .GroupBy(traceEvent => new
                    {
                        traceEvent.Category,
                        traceEvent.Name
                    })
                    .OrderBy(group => group.Key.Category, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Name, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.Name,-40} | Count={group.Count()}");
                }

                _output.WriteLine("");
                _output.WriteLine("TRACE TIMELINE SAMPLE");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var traceEvent in report.TimelineEvents
                    .OrderBy(traceEvent => traceEvent.TimestampUtc)
                    .Take(80))
                {
                    var runtime = traceEvent.Correlation?.Runtime;

                    var tags = traceEvent.Tags is null || traceEvent.Tags.Count == 0
                        ? string.Empty
                        : string.Join(
                            ", ",
                            traceEvent.Tags
                                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .Select(pair => $"{pair.Key}={pair.Value}"));

                    _output.WriteLine(
                        $"{traceEvent.TimestampUtc:O} | " +
                        $"{traceEvent.Category,-16} | " +
                        $"{traceEvent.Name,-40} | " +
                        $"ExecutionId={runtime?.ExecutionId ?? traceEvent.ExecutionId} | " +
                        $"RunId={runtime?.RunId ?? string.Empty} | " +
                        $"Worker={runtime?.WorkerId ?? string.Empty} | " +
                        $"StepId={traceEvent.StepId ?? traceEvent.Correlation?.StepId ?? string.Empty} | " +
                        $"StepKey={traceEvent.Correlation?.StepKey ?? string.Empty} | " +
                        $"Tags=[{tags}]");
                }
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        private static async Task<AiDagExecutionEngineTestHost> CreateReplayReferenceHostAsync(
            ReplayReferenceScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(scenario),
                configureServices: services =>
                {
                    var finalizedHook = new DistributedChaosRunFinalizedHook();

                    services.AddSingleton(finalizedHook);
                    services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                        finalizedHook);

                    services.AddInMemoryAiDecisionLedger();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiExecutionReplayReferenceIntegrationTests).Assembly);
                });
        }

        private static AiEngineOptions CreateOptions(
            ReplayReferenceScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

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
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = scenario.WorkerCount,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(60)
                    }
                }
            };

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
                $"execution_snapshots_replay_reference_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        private static async Task CleanupDagExecutionAsync(
            IServiceProvider serviceProvider,
            string executionId)
        {
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
            ReplayReferenceScenario scenario,
            AiExecutionState state)
        {
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
            ReplayReferenceScenario scenario,
            IAiRuntimeMetrics metrics)
        {
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
            ReplayReferenceScenario scenario,
            string executionId,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
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
            ReplayReferenceScenario scenario,
            string executionId,
            AiExecutionRecord record,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            var selectedSteps = scenario.PipelineDefinition.Steps;

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
            ReplayReferenceScenario scenario)
        {
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
            ReplayReferenceScenario scenario,
            int index,
            bool isFlaky)
        {
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

        private sealed class ReplayReferenceScenario
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

            public TimeSpan WorkerIdleDelay { get; init; }

            public TimeSpan Timeout { get; init; }

            public TimeSpan SnapshotWaitTimeout { get; init; }

            public IReadOnlyCollection<string> RequiredResolvedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> ExpectedRetriedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> RetentionPolicies { get; init; } =
                Array.Empty<string>();

            public static ReplayReferenceScenario Steps100()
            {
                var pipelineName = $"replay-reference-chaos-100-{Guid.NewGuid():N}";

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

                var scenario = new ReplayReferenceScenario
                {
                    Name = "replay-reference-chaos-100",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-replay-reference-chaos-100",
                    RetentionArchiveReason = "replay-reference-chaos-100-retention",
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
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    SnapshotWaitTimeout = TimeSpan.FromSeconds(60),
                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    },
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps
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