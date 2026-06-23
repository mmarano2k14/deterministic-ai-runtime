using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Runners;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http
{
    /// <summary>
    /// Runs provider-agnostic production runtime scenarios against the HTTP provider
    /// using process-based runtime host creation.
    /// </summary>
    public sealed class HttpProcessHostProductionScenarioRunner : IProductionRuntimeScenarioRunner
    {
        private const string RequestedBy = "mcp-production-runtime-scenario-test";
        private const string Source = "mcp-production-runtime-scenario";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostProductionScenarioRunner"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostProductionScenarioRunner(ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <inheritdoc />
        public string ProviderLabel => "http-process-host";

        /// <inheritdoc />
        public async Task<ProductionRuntimeScenarioResult> RunAsync(
            ProductionRuntimeScenarioDefinition scenario,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            await using var host =
                new GenericMcpServerTestHost(
                    settings);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            this.output.WriteLine(
                $"[HTTP PROCESS PRODUCTION] Scenario='{scenario.Name}', ControlPlaneId='{controlPlaneId}', TenantCount='{scenario.Tenants.Count}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var tenantContexts =
                new List<ProductionTenantMcpContext>();

            try
            {
                foreach (var tenant in scenario.Tenants)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var tenantHttpClient =
                        host.CreateClient();

                    var tenantMcp =
                        await McpRbacTestClientHelper
                            .CreateConfiguredClientAsync(
                                host,
                                tenantHttpClient,
                                RequestedBy,
                                tenantId: tenant.TenantId,
                                tenantGroupId: tenant.TenantGroupId)
                            .ConfigureAwait(false);

                    tenantContexts.Add(
                        new ProductionTenantMcpContext(
                            tenant,
                            tenantHttpClient,
                            tenantMcp));

                    this.output.WriteLine(
                        $"[HTTP PROCESS PRODUCTION] Tenant MCP client created. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}'.");
                }

                IReadOnlyList<ProductionTenantScenarioResult> tenantResults;

                if (scenario.RunTenantsSequentially)
                {
                    var sequentialTenantResults =
                        new List<ProductionTenantScenarioResult>();

                    foreach (var context in tenantContexts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var tenantResult =
                            await RunTenantAsync(
                                    scenario,
                                    context.Tenant,
                                    controlPlaneId,
                                    context.Mcp,
                                    scaleOutRequestStore,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        sequentialTenantResults.Add(
                            tenantResult);
                    }

                    tenantResults = sequentialTenantResults;
                }
                else
                {
                    var tenantTasks =
                        tenantContexts
                            .Select(context =>
                                RunTenantAsync(
                                    scenario,
                                    context.Tenant,
                                    controlPlaneId,
                                    context.Mcp,
                                    scaleOutRequestStore,
                                    cancellationToken))
                            .ToArray();

                    tenantResults =
                        await Task
                            .WhenAll(tenantTasks)
                            .ConfigureAwait(false);
                }

                return new ProductionRuntimeScenarioResult
                {
                    ScenarioName = scenario.Name,
                    ControlPlaneId = controlPlaneId,
                    ProviderLabel = ProviderLabel,
                    Tenants = tenantResults
                        .OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal)
                        .ToArray(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["runtimeHostAssemblyPath"] = runtimeHostAssemblyPath,
                        ["hostCreationMode"] = "Process",
                        ["transport"] = "http",
                        ["tenantCount"] = scenario.Tenants.Count.ToString()
                    }
                };
            }
            finally
            {
                foreach (var context in tenantContexts)
                {
                    context.HttpClient.Dispose();
                }
            }
        }

        /// <summary>
        /// Runs the scenario workload for one tenant.
        /// </summary>
        /// <param name="scenario">The scenario definition.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="scaleOutRequestStore">The scale-out request store.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The tenant scenario result.</returns>
        private async Task<ProductionTenantScenarioResult> RunTenantAsync(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            CancellationToken cancellationToken)
        {
            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[HTTP PROCESS PRODUCTION] Submitting tenant workload. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', PipelineKey='{pipelineName}', RunCount='{tenant.Run.RunCount}', StepCount='{tenant.Run.StepCount}'.");

            var sharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        tenant,
                        pipelineName)
                    .ConfigureAwait(false);

            await WaitForAnyTenantScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    controlPlaneId,
                    tenant,
                    pipelineName,
                    scenario.ScaleOutTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        sharedRunIds.ToHashSet(StringComparer.Ordinal),
                        expectedCount: tenant.Run.RunCount,
                        timeout: scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var finalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            var scaleOutRequests =
                await CollectTenantScaleOutRequestsAsync(
                        scaleOutRequestStore,
                        controlPlaneId,
                        tenant,
                        pipelineName,
                        cancellationToken)
                    .ConfigureAwait(false);

            var runResults =
                await BuildRunResultsAsync(
                        mcp,
                        dispatchedRuns,
                        finalStatuses,
                        scenario.Assertions)
                    .ConfigureAwait(false);

            var runtimeInstanceIds =
                runResults
                    .Select(run => run.RuntimeInstanceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

            this.output.WriteLine(
                $"[HTTP PROCESS PRODUCTION] Tenant completed. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', PipelineKey='{pipelineName}', RuntimeInstances='{string.Join(", ", runtimeInstanceIds)}', ScaleOutRequests='{scaleOutRequests.Count}'.");

            return new ProductionTenantScenarioResult
            {
                TenantId = tenant.TenantId,
                TenantGroupId = tenant.TenantGroupId,
                PipelineKey = pipelineName,
                SharedRunIds = sharedRunIds,
                RuntimeInstanceIds = runtimeInstanceIds,
                ScaleOutRequests = scaleOutRequests,
                Runs = runResults,
                CapacityOverflowObserved = false,
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = controlPlaneId,
                    ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix
                }
            };
        }

        /// <summary>
        /// Submits the configured tenant workload.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <returns>The submitted shared run ids.</returns>
        private static async Task<IReadOnlyList<string>> SubmitRunsAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName)
        {
            var input =
                new Dictionary<string, object?>(
                    tenant.Run.Input,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["delayMs"] = tenant.Run.DelayMs,
                    ["stepCount"] = tenant.Run.StepCount
                };

            var submitRequest =
                 new AiSharedRuntimeControllerRequest
                 {
                     Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                     PipelineKey = pipelineName,
                     TenantId = tenant.TenantId,
                     RequestedBy = RequestedBy,
                     Source = Source,
                     Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                     {
                         [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                         [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                         ["pipelineName"] = pipelineName,
                         ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix
                     },
                     RunRequest = McpTestPipelineFactory.CreateRunRequest(
                         pipelineName,
                         stepCount: tenant.Run.StepCount,
                         input: input,
                         enableRetention: tenant.Run.EnableRetention,
                         flakyStepInterval: tenant.Run.FlakyStepInterval)
                 };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        tenant.Run.RunCount)
                    .ConfigureAwait(false);

            Assert.Equal(
                tenant.Run.RunCount,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            return submitResults
                .Select(ExtractSharedRunId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        /// <param name="submitResult">The submit result.</param>
        /// <returns>The shared run id.</returns>
        private static string ExtractSharedRunId(object submitResult)
        {
            ArgumentNullException.ThrowIfNull(submitResult);

            var resultType =
                submitResult.GetType();

            var directSharedRunId =
                resultType.GetProperty("SharedRunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId =
                resultType.GetProperty("RunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            var sharedRun =
                resultType.GetProperty("SharedRun")?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId =
                    sharedRun
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            throw new InvalidOperationException(
                $"Could not extract SharedRunId from submit result type '{resultType.FullName}'.");
        }

        /// <summary>
        /// Waits until at least one scale-out request linked to the tenant workload is fulfilled.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private static async Task WaitForAnyTenantScaleOutRequestFulfilledAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> lastRequests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastRequests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                MaxResults = 100
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                var fulfilled =
                    lastRequests.FirstOrDefault(request =>
                        request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled &&
                        !string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId));

                if (fulfilled is not null)
                {
                    return;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"No fulfilled scale-out request was observed for tenant '{tenant.TenantId}' within '{timeout}'. " +
                $"ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}'." +
                Environment.NewLine +
                FormatScaleOutRequests(lastRequests));

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Collects the scale-out requests that actually exist for the submitted tenant workload.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observed scale-out request results.</returns>
        private static async Task<IReadOnlyList<ProductionScaleOutScenarioResult>> CollectTenantScaleOutRequestsAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var requests =
                await store
                    .ListAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = controlPlaneId,
                            TenantId = tenant.TenantId,
                            PipelineKey = pipelineName,
                            MaxResults = 100
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            Assert.NotEmpty(
                requests);

            return requests
                .OrderBy(request => request.CreatedAtUtc)
                .ThenBy(request => request.RequestId, StringComparer.Ordinal)
                .Select(request => new ProductionScaleOutScenarioResult
                {
                    RequestId = request.RequestId,
                    SharedRunId = request.SharedRunId,
                    TenantId = request.TenantId ?? string.Empty,
                    TenantGroupId = request.TenantGroupId,
                    Status = request.Status.ToString(),
                    IsolationMode = request.IsolationMode.ToString(),
                    PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                    AllowSharedFallback = request.AllowSharedFallback,
                    RuntimeInstanceIdPrefix = request.RuntimeInstanceIdPrefix,
                    WorkerCountPerInstance = request.WorkerCountPerInstance,
                    MaxConcurrentRunsPerInstance = request.MaxConcurrentRunsPerInstance,
                    LocalQueueCapacity = request.LocalQueueCapacity,
                    FulfilledRuntimeInstanceId = request.FulfilledRuntimeInstanceId,
                    RejectionReason = request.RejectionReason,
                    FulfilledAtUtc = request.FulfilledAtUtc,
                    RejectedAtUtc = request.RejectedAtUtc
                })
                .ToArray();
        }

        /// <summary>
        /// Formats scale-out requests for assertion diagnostics.
        /// </summary>
        /// <param name="requests">The requests to format.</param>
        /// <returns>The formatted diagnostic text.</returns>
        private static string FormatScaleOutRequests(
            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> requests)
        {
            if (requests.Count == 0)
            {
                return "No scale-out requests were found for the query.";
            }

            return string.Join(
                Environment.NewLine,
                requests.Select(request =>
                    $"RequestId='{request.RequestId}', SharedRunId='{request.SharedRunId}', " +
                    $"TenantId='{request.TenantId}', PipelineKey='{request.PipelineKey}', Status='{request.Status}', " +
                    $"FulfilledRuntimeInstanceId='{request.FulfilledRuntimeInstanceId}', " +
                    $"RejectionReason='{request.RejectionReason}', Reason='{request.Reason}', " +
                    $"ProviderHint='{request.ProviderHint}', CreatedAtUtc='{request.CreatedAtUtc:O}'."));
        }

        /// <summary>
        /// Builds run results from dispatched shared runs and terminal runtime statuses.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="finalStatuses">The terminal runtime statuses.</param>
        /// <param name="assertions">The scenario assertion options that control which observability and replay queries are executed.</param>
        /// <returns>The run results.</returns>
        private async Task<IReadOnlyList<ProductionRunScenarioResult>> BuildRunResultsAsync(
            McpTestClient mcp,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> finalStatuses,
            ProductionRuntimeScenarioAssertionOptions assertions)
        {
            ArgumentNullException.ThrowIfNull(assertions);

            var results =
                new List<ProductionRunScenarioResult>();

            foreach (var status in finalStatuses)
            {
                var matchingSharedRun =
                    FindMatchingSharedRun(
                        dispatchedRuns,
                        status);

                Assert.True(
                    status.Success,
                    FormatRuntimeStatusFailure(
                        matchingSharedRun,
                        status,
                        ledgerDump: null));

                var executionId =
                    status.ExecutionId ??
                    status.RunState?.ExecutionId;

                Assert.False(
                    string.IsNullOrWhiteSpace(executionId),
                    FormatRuntimeStatusFailure(
                        matchingSharedRun,
                        status,
                        ledgerDump: null));

                if (!string.Equals(
                        "completed",
                        status.RunState?.Status,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var ledgerDump =
                        await BuildLedgerDumpAsync(
                                mcp,
                                executionId!)
                            .ConfigureAwait(false);

                    Assert.Fail(
                        FormatRuntimeStatusFailure(
                            matchingSharedRun,
                            status,
                            ledgerDump));
                }

                var hasLedger = false;
                var hasTrace = false;
                var hasReplayReport = false;
                var hasReplayLedger = false;
                var hasReplayTrace = false;

                var ledgerCount = 0;
                var traceCount = 0;

                var replaySuccess = false;
                string? replayMessage = null;

                if (assertions.AssertLedger)
                {
                    var ledgerEntries =
                        await mcp.GetLedgerByExecutionAsync(
                                executionId!)
                            .ConfigureAwait(false);

                    ledgerCount =
                        ledgerEntries.Count;

                    hasLedger =
                        ledgerCount > 0;
                }

                if (assertions.AssertTrace)
                {
                    var traceEvents =
                        await mcp.GetTraceByExecutionAsync(
                                executionId!)
                            .ConfigureAwait(false);

                    traceCount =
                        traceEvents.Count;

                    hasTrace =
                        traceCount > 0;
                }

                if (assertions.AssertReplayReport ||
                    assertions.AssertReplayLedger ||
                    assertions.AssertReplayTrace)
                {
                    var replayRequest =
                        new AiReplayControlRequest
                        {
                            ExecutionId = executionId!,
                            CorrelationId = $"production-replay-{Guid.NewGuid():N}",
                            RequestedBy = RequestedBy,
                            Source = Source,
                            Operation = AiReplayOperation.Replay
                        };

                    var replayResult =
                        await mcp.ReplayExecutionAsync(
                                replayRequest)
                            .ConfigureAwait(false);

                    replaySuccess =
                        replayResult.Success;

                    replayMessage =
                        replayResult.FailureReason ??
                        replayResult.Message;

                    if (assertions.AssertReplayReport)
                    {
                        replayRequest.Operation =
                            AiReplayOperation.GetReport;

                        var replayReport =
                            await mcp.GetReplayReportAsync(
                                    replayRequest)
                                .ConfigureAwait(false);

                        hasReplayReport =
                            replayResult.Success &&
                            replayReport.Success;
                    }

                    if (assertions.AssertReplayLedger)
                    {
                        replayRequest.Operation =
                            AiReplayOperation.GetLedger;

                        var replayLedger =
                            await mcp.GetReplayLedgerAsync(
                                    replayRequest)
                                .ConfigureAwait(false);

                        hasReplayLedger =
                            replayLedger.Success;
                    }

                    if (assertions.AssertReplayTrace)
                    {
                        replayRequest.Operation =
                            AiReplayOperation.GetTimeline;

                        var replayTrace =
                            await mcp.GetReplayTraceAsync(
                                    replayRequest)
                                .ConfigureAwait(false);

                        hasReplayTrace =
                            replayTrace.Success;
                    }
                }

                this.output.WriteLine(
                    $"[HTTP PROCESS PRODUCTION][OBSERVABILITY DEBUG] ExecutionId='{executionId}', " +
                    $"LedgerCount='{ledgerCount}', TraceCount='{traceCount}', " +
                    $"HasLedger='{hasLedger}', HasTrace='{hasTrace}', " +
                    $"ReplaySuccess='{replaySuccess}', ReplayMessage='{replayMessage}', " +
                    $"HasReplayReport='{hasReplayReport}', HasReplayLedger='{hasReplayLedger}', HasReplayTrace='{hasReplayTrace}'.");

                results.Add(
                    new ProductionRunScenarioResult
                    {
                        SharedRunId = matchingSharedRun.SharedRunId,
                        RuntimeInstanceId = matchingSharedRun.AssignedRuntimeInstanceId ?? status.RuntimeInstanceId,
                        LocalRunId = matchingSharedRun.LocalRunId ?? status.RunId,
                        ExecutionId = executionId,
                        FinalStatus = status.RunState?.Status,
                        HasLedger = hasLedger,
                        HasTrace = hasTrace,
                        HasReplayReport = hasReplayReport,
                        HasReplayLedger = hasReplayLedger,
                        HasReplayTrace = hasReplayTrace
                    });
            }

            return results;
        }

        /// <summary>
        /// Finds the shared run matching a terminal runtime queue status.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="status">The runtime queue control-plane status.</param>
        /// <returns>The matching shared run.</returns>
        private static AiSharedRunRecord FindMatchingSharedRun(
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            AiRuntimeQueueControlPlaneResult status)
        {
            var matchingSharedRun =
                dispatchedRuns.SingleOrDefault(run =>
                    string.Equals(
                        run.AssignedRuntimeInstanceId,
                        status.RuntimeInstanceId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        run.LocalRunId,
                        status.RunId,
                        StringComparison.Ordinal));

            if (matchingSharedRun is not null)
            {
                return matchingSharedRun;
            }

            if (!string.IsNullOrWhiteSpace(status.RunId))
            {
                matchingSharedRun =
                    dispatchedRuns.SingleOrDefault(run =>
                        string.Equals(
                            run.LocalRunId,
                            status.RunId,
                            StringComparison.Ordinal));
            }

            if (matchingSharedRun is not null)
            {
                return matchingSharedRun;
            }

            var dump =
                string.Join(
                    Environment.NewLine,
                    dispatchedRuns.Select(run =>
                        $"SharedRunId='{run.SharedRunId}', RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}'."));

            Assert.Fail(
                $"Could not match runtime queue status to a dispatched shared run. " +
                $"StatusRuntimeInstanceId='{status.RuntimeInstanceId}', StatusRunId='{status.RunId}'." +
                Environment.NewLine +
                dump);

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Builds a compact ledger dump for diagnostics.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="executionId">The execution id.</param>
        /// <returns>The formatted ledger dump.</returns>
        private static async Task<string> BuildLedgerDumpAsync(
            McpTestClient mcp,
            string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                return "<missing execution id>";
            }

            var entries =
                await mcp.GetLedgerByExecutionAsync(
                        executionId)
                    .ConfigureAwait(false);

            if (entries.Count == 0)
            {
                return "<empty>";
            }

            return string.Join(
                " || ",
                entries
                    .TakeLast(10)
                    .Select(entry => JsonSerializer.Serialize(entry)));
        }

        /// <summary>
        /// Formats a runtime status failure for diagnostics.
        /// </summary>
        /// <param name="sharedRun">The matching shared run.</param>
        /// <param name="status">The runtime queue control-plane status.</param>
        /// <param name="ledgerDump">The optional ledger dump.</param>
        /// <returns>The formatted failure message.</returns>
        private static string FormatRuntimeStatusFailure(
            AiSharedRunRecord sharedRun,
            AiRuntimeQueueControlPlaneResult status,
            string? ledgerDump)
        {
            var statusJson =
                JsonSerializer.Serialize(
                    status,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            return
                "Runtime run did not complete successfully." +
                Environment.NewLine +
                $"SharedRunId='{sharedRun.SharedRunId}'." +
                Environment.NewLine +
                $"SharedRunStatus='{sharedRun.Status}'." +
                Environment.NewLine +
                $"AssignedRuntimeInstanceId='{sharedRun.AssignedRuntimeInstanceId}'." +
                Environment.NewLine +
                $"SharedRunLocalRunId='{sharedRun.LocalRunId}'." +
                Environment.NewLine +
                $"SharedRunExecutionId='{sharedRun.ExecutionId}'." +
                Environment.NewLine +
                $"SharedRunFailureReason='{sharedRun.FailureReason}'." +
                Environment.NewLine +
                $"StatusSuccess='{status.Success}'." +
                Environment.NewLine +
                $"StatusMessage='{status.Message}'." +
                Environment.NewLine +
                $"StatusFailureReason='{status.FailureReason}'." +
                Environment.NewLine +
                $"StatusRuntimeInstanceId='{status.RuntimeInstanceId}'." +
                Environment.NewLine +
                $"StatusRunId='{status.RunId}'." +
                Environment.NewLine +
                $"StatusExecutionId='{status.ExecutionId}'." +
                Environment.NewLine +
                $"RunStateStatus='{status.RunState?.Status}'." +
                Environment.NewLine +
                $"RunStateExecutionId='{status.RunState?.ExecutionId}'." +
                Environment.NewLine +
                $"Diagnostics='{string.Join(" | ", status.Diagnostics)}'." +
                Environment.NewLine +
                $"Ledger='{ledgerDump ?? "<not loaded>"}'." +
                Environment.NewLine +
                "RawStatusJson=" +
                Environment.NewLine +
                statusJson;
        }

        /// <summary>
        /// Holds the tenant-scoped MCP client context used by the production scenario runner.
        /// </summary>
        /// <param name="Tenant">The tenant scenario definition.</param>
        /// <param name="HttpClient">The tenant-scoped HTTP client.</param>
        /// <param name="Mcp">The tenant-scoped MCP test client.</param>
        private sealed record ProductionTenantMcpContext(
            ProductionTenantScenarioDefinition Tenant,
            HttpClient HttpClient,
            McpTestClient Mcp);
    }
}