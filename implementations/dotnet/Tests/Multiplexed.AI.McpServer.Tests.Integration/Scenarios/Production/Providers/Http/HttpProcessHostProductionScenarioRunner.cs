using Microsoft.Extensions.DependencyInjection;
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
        public HttpProcessHostProductionScenarioRunner(
            ITestOutputHelper output)
        {
            this.output =
                output
                ?? throw new ArgumentNullException(nameof(output));
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
                CreateHttpProcessProductionScenarioSettings(
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
                                tenantId: tenant.TenantId)
                            .ConfigureAwait(false);

                    tenantContexts.Add(
                        new ProductionTenantMcpContext(
                            tenant,
                            tenantHttpClient,
                            tenantMcp));

                    this.output.WriteLine(
                        $"[HTTP PROCESS PRODUCTION] Tenant MCP client created. TenantId='{tenant.TenantId}'.");
                }

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

                var tenantResults =
                    await Task
                        .WhenAll(tenantTasks)
                        .ConfigureAwait(false);

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
                $"[HTTP PROCESS PRODUCTION] Submitting tenant workload. TenantId='{tenant.TenantId}', PipelineKey='{pipelineName}', RunCount='{tenant.Run.RunCount}', StepCount='{tenant.Run.StepCount}'.");

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
                BuildRunResults(
                    dispatchedRuns,
                    finalStatuses);

            var runtimeInstanceIds =
                runResults
                    .Select(run => run.RuntimeInstanceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

            this.output.WriteLine(
                $"[HTTP PROCESS PRODUCTION] Tenant completed. TenantId='{tenant.TenantId}', PipelineKey='{pipelineName}', RuntimeInstances='{string.Join(", ", runtimeInstanceIds)}', ScaleOutRequests='{scaleOutRequests.Count}'.");

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
        /// Creates HTTP process-host production scenario settings.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path.</param>
        /// <returns>The settings dictionary.</returns>
        private static Dictionary<string, string?> CreateHttpProcessProductionScenarioSettings(
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            var settings =
                GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            settings["AiMcpHost:EnableSharedQueuePump"] = "true";

            settings["AiSharedQueueBackgroundService:Enabled"] = "true";
            settings["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false";
            settings["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100";
            settings["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:00:05";
            settings["AiSharedQueueBackgroundService:IntervalSeconds"] = "1";
            settings["AiSharedQueueBackgroundService:MaxDispatchesPerCycle"] = "10";

            settings["AiSharedQueuePump:Enabled"] = "true";

            // Important:
            // This production scenario starts from zero runtime capacity.
            // Submit must go through admission immediately so admission can create
            // Redis scale-out requests. QueueFirst would enqueue first and may never
            // create the scale-out request when no runtime exists yet.
            settings["AiSharedRuntimeController:SubmitMode"] = "DirectDispatch";

            settings["AiRuntimeScaleOutWatcher:Enabled"] = "true";
            settings["AiRuntimeScaleOutWatcher:IntervalSeconds"] = "1";

            settings["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "false";
            settings["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "100";

            settings["AiEngine:Snapshots:Enabled"] = "true";
            settings["AiEngine:Snapshots:Mongo:Enabled"] = "true";

            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__Enabled"] = "true";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__Provider"] = "mongo-redis";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__RequireReplaySafePayloads"] = "true";

            return settings;
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
        private static string ExtractSharedRunId(
            object submitResult)
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
                    Status = request.Status.ToString(),
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
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="finalStatuses">The terminal runtime statuses.</param>
        /// <returns>The run results.</returns>
        private static IReadOnlyList<ProductionRunScenarioResult> BuildRunResults(
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> finalStatuses)
        {
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
                        status));

                var executionId =
                    status.ExecutionId ??
                    status.RunState?.ExecutionId;

                Assert.False(
                    string.IsNullOrWhiteSpace(executionId),
                    FormatRuntimeStatusFailure(
                        matchingSharedRun,
                        status));

                Assert.True(
                    string.Equals(
                        "completed",
                        status.RunState?.Status,
                        StringComparison.OrdinalIgnoreCase),
                    FormatRuntimeStatusFailure(
                        matchingSharedRun,
                        status));

                results.Add(
                    new ProductionRunScenarioResult
                    {
                        SharedRunId = matchingSharedRun.SharedRunId,
                        RuntimeInstanceId = matchingSharedRun.AssignedRuntimeInstanceId ?? status.RuntimeInstanceId,
                        LocalRunId = matchingSharedRun.LocalRunId ?? status.RunId,
                        ExecutionId = executionId,
                        FinalStatus = status.RunState?.Status,
                        HasLedger = false,
                        HasTrace = false,
                        HasReplayReport = false,
                        HasReplayLedger = false,
                        HasReplayTrace = false
                    });
            }

            return results;
        }

        /// <summary>
        /// Finds the shared run matching a terminal runtime queue status.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="status">The runtime queue status.</param>
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
        /// Formats a runtime status failure for diagnostics.
        /// </summary>
        /// <param name="sharedRun">The matching shared run.</param>
        /// <param name="status">The runtime queue control-plane status.</param>
        /// <returns>The formatted failure message.</returns>
        private static string FormatRuntimeStatusFailure(
            AiSharedRunRecord sharedRun,
            AiRuntimeQueueControlPlaneResult status)
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