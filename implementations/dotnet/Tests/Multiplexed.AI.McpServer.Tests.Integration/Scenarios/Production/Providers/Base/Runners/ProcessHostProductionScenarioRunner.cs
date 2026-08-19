using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.AI.Stores;
using StackExchange.Redis;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners
{
    /// <summary>
    /// Runs provider-agnostic production runtime scenarios against remote runtime-host providers.
    /// </summary>
    internal sealed class ProcessHostProductionScenarioRunner
    {
        private const string RequestedBy = "mcp-production-runtime-scenario-test";
        private const string Source = "mcp-production-runtime-scenario";
        private static readonly TimeSpan ReplaySnapshotReadinessTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReplaySnapshotReadinessPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly string providerLabel;
        private readonly string logPrefix;
        private readonly string transportName;
        private readonly AiRuntimeHostCreationMode hostCreationMode;
        private readonly Func<ProductionRuntimeScenarioDefinition, string, string, Dictionary<string, string?>> settingsBuilder;
        private readonly Func<string, Task>? scenarioCleanup;
        private readonly Func<IServiceProvider, string, ProductionRuntimeScenarioDefinition, IAiRuntimeHostProcessControl>? childRuntimeProcessControlFactory;
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessHostProductionScenarioRunner"/> class.
        /// </summary>
        /// <param name="providerLabel">The provider label used in scenario results.</param>
        /// <param name="logPrefix">The log prefix used in test output.</param>
        /// <param name="transportName">The transport name used in result metadata.</param>
        /// <param name="hostCreationMode">The physical runtime-host creation mode.</param>
        /// <param name="settingsBuilder">The runtime-host settings builder.</param>
        /// <param name="output">The test output helper.</param>
        /// <param name="scenarioCleanup">Optional provider cleanup executed after the MCP host has stopped.</param>
        /// <param name="childRuntimeProcessControlFactory">Optional provider-specific factory used to inject the configured physical nested-child failure boundary.</param>
        public ProcessHostProductionScenarioRunner(
            string providerLabel,
            string logPrefix,
            string transportName,
            AiRuntimeHostCreationMode hostCreationMode,
            Func<ProductionRuntimeScenarioDefinition, string, string, Dictionary<string, string?>> settingsBuilder,
            ITestOutputHelper output,
            Func<string, Task>? scenarioCleanup = null,
            Func<IServiceProvider, string, ProductionRuntimeScenarioDefinition, IAiRuntimeHostProcessControl>? childRuntimeProcessControlFactory = null)
        {
            this.providerLabel = !string.IsNullOrWhiteSpace(providerLabel)
                ? providerLabel
                : throw new ArgumentException("Provider label is required.", nameof(providerLabel));

            this.logPrefix = !string.IsNullOrWhiteSpace(logPrefix)
                ? logPrefix
                : throw new ArgumentException("Log prefix is required.", nameof(logPrefix));

            this.transportName = !string.IsNullOrWhiteSpace(transportName)
                ? transportName
                : throw new ArgumentException("Transport name is required.", nameof(transportName));

            this.hostCreationMode = hostCreationMode;
            this.settingsBuilder = settingsBuilder ?? throw new ArgumentNullException(nameof(settingsBuilder));
            this.scenarioCleanup = scenarioCleanup;
            this.childRuntimeProcessControlFactory = childRuntimeProcessControlFactory;
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Runs a production runtime scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The production runtime scenario result.</returns>
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
                settingsBuilder(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            try
            {
                await using var host =
                    new GenericMcpServerTestHost(settings);

                var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var executionSnapshotStore =
                RequiresReplaySnapshot(scenario.Assertions)
                    ? host.Services.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>()
                    : null;

            var childCompositionEnabled =
                scenario.Tenants.Any(tenant => tenant.Run.ChildDepth > 0);
            var childRelationStore =
                childCompositionEnabled
                    ? ProductionChildDagScenarioHelpers.CreateRelationStore(host.Services)
                    : null;
            using var childObservationScope =
                childCompositionEnabled
                    ? host.Services.CreateScope()
                    : null;
            var childDagExecutionStore =
                childObservationScope?.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            var childRuntimeFailureEnabled =
                scenario.Tenants.Any(tenant => tenant.Run.ChildRuntimeFailure is not null);

            var childRuntimeFailureServices =
                childRuntimeFailureEnabled
                    ? new ChildDagRuntimeFailureServices(
                        host.Services.GetRequiredService<IConnectionMultiplexer>(),
                        this.childRuntimeProcessControlFactory is not null
                            ? this.childRuntimeProcessControlFactory(
                                host.Services,
                                controlPlaneId,
                                scenario)
                            : host.Services
                                .GetRequiredService<AiRuntimeHostProcessControlSelector>()
                                .GetRequired(this.hostCreationMode),
                        host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>(),
                        host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>())
                    : null;

            output.WriteLine(
                $"[{logPrefix}] Scenario='{scenario.Name}', ControlPlaneId='{controlPlaneId}', TenantCount='{scenario.Tenants.Count}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

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

                    output.WriteLine(
                        $"[{logPrefix}] Tenant MCP client created. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}'.");
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
                                executionSnapshotStore,
                                childRelationStore,
                                childDagExecutionStore,
                                childRuntimeFailureServices,
                                cancellationToken)
                            .ConfigureAwait(false);

                        sequentialTenantResults.Add(tenantResult);
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
                                    executionSnapshotStore,
                                    childRelationStore,
                                    childDagExecutionStore,
                                    childRuntimeFailureServices,
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
                    ProviderLabel = providerLabel,
                    Tenants = tenantResults
                        .OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal)
                        .ToArray(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["runtimeHostAssemblyPath"] = runtimeHostAssemblyPath,
                        ["hostCreationMode"] = this.hostCreationMode.ToString(),
                        ["transport"] = transportName,
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
            finally
            {
                if (this.scenarioCleanup is not null)
                {
                    await this.scenarioCleanup(controlPlaneId)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Runs the scenario workload for one tenant.
        /// </summary>
        private async Task<ProductionTenantScenarioResult> RunTenantAsync(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            IAiExecutionSnapshotStore<ExecutionContextSnapshot>? executionSnapshotStore,
            IAiChildExecutionRelationStore? childRelationStore,
            IAiDagExecutionStore? childDagExecutionStore,
            ChildDagRuntimeFailureServices? childRuntimeFailureServices,
            CancellationToken cancellationToken)
        {
            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{Guid.NewGuid():N}";

            ValidateChildRuntimeFailure(tenant.Run);

            var childRuntimeFailure = tenant.Run.ChildRuntimeFailure;
            ProductionCrashCheckpointGate? childCrashCheckpointGate = null;
            ProductionCrashCheckpointGate? parentPreChildCheckpointGate = null;

            if (childRuntimeFailure is not null)
            {
                if (childRuntimeFailureServices is null ||
                    childRelationStore is null ||
                    childDagExecutionStore is null)
                {
                    throw new InvalidOperationException(
                        "Child runtime failure scenarios require child composition persistence and physical process-control services.");
                }

                childCrashCheckpointGate = await ProductionCrashCheckpointGate
                    .ArmAsync(
                        childRuntimeFailureServices.ConnectionMultiplexer,
                        output,
                        controlPlaneId,
                        tenant.TenantId,
                        pipelineName,
                        childRuntimeFailure.CrashCheckpointStepIndex,
                        stateTtl: scenario.CompletionTimeout + TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

                if (childRuntimeFailure.Target == ProductionChildDagFailureTarget.ParentRuntimeAfterPark)
                {
                    /*
                     * The Child DAG call-site is appended after every historical root step. Blocking the final
                     * root step therefore gives the harness one deterministic moment to make the parent runtime
                     * ineligible for new admission before ExecuteChildDag dispatches C1. The currently running
                     * parent is not interrupted by pausing its local queue.
                     */
                    parentPreChildCheckpointGate = await ProductionCrashCheckpointGate
                        .ArmAsync(
                            childRuntimeFailureServices.ConnectionMultiplexer,
                            output,
                            controlPlaneId,
                            tenant.TenantId,
                            pipelineName,
                            tenant.Run.StepCount,
                            stateTtl: scenario.CompletionTimeout + TimeSpan.FromMinutes(2))
                        .ConfigureAwait(false);
                }
            }

            output.WriteLine(
                $"[{logPrefix}] Submitting tenant workload. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', PipelineKey='{pipelineName}', RunCount='{tenant.Run.RunCount}', StepCount='{tenant.Run.StepCount}', ChildDepth='{tenant.Run.ChildDepth}'.");

            var sharedRunIds =
                await SubmitRunsAsync(
                    mcp,
                    tenant,
                    pipelineName,
                    parentPreChildCheckpointGate?.Definition,
                    childCrashCheckpointGate?.Definition,
                    childRuntimeFailure?.TargetDepth ?? 0)
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

            ProductionChildDagRuntimeFailureResult? childRuntimeFailureResult = null;

            if (childRuntimeFailure is not null)
            {
                var parentRun = Assert.Single(dispatchedRuns);
                var parentExecutionId = parentRun.ExecutionId;

                if (string.IsNullOrWhiteSpace(parentExecutionId))
                {
                    var parentExecutionObservation = await McpTestWaitHelpers
                        .WaitForRuntimeRunExecutionIdAsync(
                            mcp,
                            parentRun,
                            scenario.DispatchTimeout)
                        .ConfigureAwait(false);

                    parentExecutionId =
                        parentExecutionObservation.ExecutionId ??
                        parentExecutionObservation.RunState?.ExecutionId;
                }

                if (string.IsNullOrWhiteSpace(parentExecutionId))
                {
                    throw new InvalidOperationException(
                        $"Submitted parent run '{parentRun.SharedRunId}' did not expose a durable ExecutionId before child runtime failure injection.");
                }

                childRuntimeFailureResult = await ProductionChildDagRuntimeFailureTestHelpers
                    .InjectRuntimeFailureAndObserveRecoveryAsync(
                        output,
                        childRuntimeFailure,
                        childCrashCheckpointGate!,
                        parentPreChildCheckpointGate,
                        mcp,
                        childRelationStore!,
                        childDagExecutionStore!,
                        childRuntimeFailureServices!.RunExecutionIndex,
                        childRuntimeFailureServices.ProcessControl,
                        childRuntimeFailureServices.Registry,
                        tenant.TenantId,
                        parentExecutionId,
                        pipelineName,
                        parentRun.AssignedRuntimeInstanceId,
                        parentRun.LocalRunId,
                        tenant.Run.ChildDepth,
                        scenario.CompletionTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var finalStatuses =
                tenant.Run.ChildDepth == 0
                    ? await McpTestWaitHelpers
                        .WaitForTerminalRuntimeRunStatusesAsync(
                            mcp,
                            dispatchedRuns,
                            timeout: scenario.CompletionTimeout)
                        .ConfigureAwait(false)
                    : childDagExecutionStore is not null
                        ? await ProductionChildDagScenarioHelpers
                            .WaitForDurableParentCompletionAsync(
                                mcp,
                                childDagExecutionStore,
                                dispatchedRuns,
                                scenario.CompletionTimeout,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : throw new InvalidOperationException(
                            "Child DAG scenarios require the shared authoritative DAG execution store.");

            if (executionSnapshotStore is not null)
            {
                await WaitForReplaySnapshotsAsync(
                        executionSnapshotStore,
                        finalStatuses,
                        ReplaySnapshotReadinessTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

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
                    tenant,
                    pipelineName,
                    childRelationStore,
                    scenario.CompletionTimeout,
                    childRuntimeFailureResult,
                    scenario.Assertions,
                    cancellationToken)
                .ConfigureAwait(false);

            var runtimeInstanceIds =
                runResults
                    .Select(run => run.RuntimeInstanceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

            output.WriteLine(
                $"[{logPrefix}] Tenant completed. TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', PipelineKey='{pipelineName}', RuntimeInstances='{string.Join(", ", runtimeInstanceIds)}', ScaleOutRequests='{scaleOutRequests.Count}'.");

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

        private static void ValidateChildRuntimeFailure(
            ProductionRunScenarioDefinition run)
        {
            ArgumentNullException.ThrowIfNull(run);

            var failure = run.ChildRuntimeFailure;
            if (failure is null)
            {
                return;
            }

            if (run.ChildDepth <= 0)
            {
                throw new InvalidOperationException(
                    "A Child DAG runtime-boundary failure injection requires ChildDepth to be greater than zero.");
            }

            if (run.RunCount != 1)
            {
                throw new InvalidOperationException(
                    "The focused physical Child DAG runtime-boundary failure proof currently requires exactly one submitted parent run.");
            }

            if (failure.TargetDepth <= 0 || failure.TargetDepth > run.ChildDepth)
            {
                throw new InvalidOperationException(
                    $"Child DAG runtime-boundary failure TargetDepth must be between 1 and '{run.ChildDepth}'.");
            }

            if (failure.CrashCheckpointStepIndex <= 1 ||
                failure.CrashCheckpointStepIndex > run.StepCount)
            {
                throw new InvalidOperationException(
                    $"Child DAG runtime-boundary failure CrashCheckpointStepIndex must be between 2 and '{run.StepCount}'.");
            }

            if (failure.Target == ProductionChildDagFailureTarget.ParentRuntimeAfterPark &&
                failure.TargetDepth != 1)
            {
                throw new InvalidOperationException(
                    "The parked root-parent runtime failure proof requires TargetDepth=1.");
            }
        }

        private static async Task<IReadOnlyList<string>> SubmitRunsAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            McpTestCrashCheckpointDefinition? parentCrashCheckpoint,
            McpTestCrashCheckpointDefinition? childCrashCheckpoint,
            int childCrashCheckpointDepth)
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
                    Metadata = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
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
                        flakyStepInterval: tenant.Run.FlakyStepInterval,
                        crashCheckpoint: parentCrashCheckpoint,
                        childDepth: tenant.Run.ChildDepth,
                        childCrashCheckpoint: childCrashCheckpoint,
                        childCrashCheckpointDepth: childCrashCheckpointDepth)
                };

            var submitResults =
                await mcp
                    .SubmitManyRunsAsync(
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

                await Task
                    .Delay(
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

        private static async Task<IReadOnlyList<ProductionScaleOutScenarioResult>> CollectTenantScaleOutRequestsAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            CancellationToken cancellationToken)
        {
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

            Assert.NotEmpty(requests);

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
        /// Waits until terminal execution snapshots required by replay are visible in the shared durable store.
        /// </summary>
        /// <param name="snapshotStore">The authoritative shared execution snapshot store.</param>
        /// <param name="finalStatuses">The terminal parent run observations.</param>
        /// <param name="timeout">The maximum snapshot-readiness wait for the complete set.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when every replay snapshot is visible.</returns>
        /// <remarks>
        /// The DAG engine persists its terminal execution record before running terminal lifecycle side effects.
        /// A production poll can therefore observe <c>IsTerminal</c> slightly before terminal snapshot persistence
        /// finishes. Waiting on the shared snapshot store closes that observation race without retrying Replay itself
        /// or changing the runtime lifecycle ordering.
        /// </remarks>
        private static async Task WaitForReplaySnapshotsAsync(
            IAiExecutionSnapshotStore<ExecutionContextSnapshot> snapshotStore,
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> finalStatuses,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshotStore);
            ArgumentNullException.ThrowIfNull(finalStatuses);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "Replay snapshot readiness timeout must be greater than zero.");
            }

            var executionIds = finalStatuses
                .Select(status => status.ExecutionId ?? status.RunState?.ExecutionId)
                .Where(executionId => !string.IsNullOrWhiteSpace(executionId))
                .Select(executionId => executionId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (executionIds.Length != finalStatuses.Count)
            {
                throw new InvalidOperationException(
                    "Replay snapshot readiness requires every terminal runtime status to expose an ExecutionId.");
            }

            var pending = executionIds.ToHashSet(StringComparer.Ordinal);
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (pending.Count > 0 && DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var executionId in pending.ToArray())
                {
                    var snapshot = await snapshotStore
                        .GetAsync(executionId, cancellationToken)
                        .ConfigureAwait(false);

                    if (snapshot is not null)
                    {
                        pending.Remove(executionId);
                    }
                }

                if (pending.Count == 0)
                {
                    return;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                        remaining < ReplaySnapshotReadinessPollInterval
                            ? remaining
                            : ReplaySnapshotReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Terminal replay snapshots did not become visible within '{timeout}'. ExecutionIds='{string.Join(",", pending.OrderBy(id => id, StringComparer.Ordinal))}'.");
        }

        /// <summary>
        /// Determines whether the scenario requires terminal execution snapshots for replay evidence.
        /// </summary>
        /// <param name="assertions">The configured production assertions.</param>
        /// <returns><see langword="true"/> when at least one replay artifact is required.</returns>
        private static bool RequiresReplaySnapshot(
            ProductionRuntimeScenarioAssertionOptions assertions)
        {
            ArgumentNullException.ThrowIfNull(assertions);

            return assertions.AssertReplayReport ||
                   assertions.AssertReplayLedger ||
                   assertions.AssertReplayTrace;
        }

        private async Task<IReadOnlyList<ProductionRunScenarioResult>> BuildRunResultsAsync(
            McpTestClient mcp,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> finalStatuses,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            IAiChildExecutionRelationStore? childRelationStore,
            TimeSpan childRelationTimeout,
            ProductionChildDagRuntimeFailureResult? childRuntimeFailureResult,
            ProductionRuntimeScenarioAssertionOptions assertions,
            CancellationToken cancellationToken)
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
                        status,
                        ledgerDump: null));

                var executionId =
                    status.ExecutionId ?? status.RunState?.ExecutionId;

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

                var ledgerCount = 0;
                var traceCount = 0;
                var hasLedger = false;
                var hasTrace = false;
                var hasReplayReport = false;
                var hasReplayLedger = false;
                var hasReplayTrace = false;
                var replaySuccess = false;
                string? replayMessage = null;

                if (assertions.AssertLedger)
                {
                    var ledgerEntries =
                        await mcp
                            .GetLedgerByExecutionAsync(executionId!)
                            .ConfigureAwait(false);

                    ledgerCount = ledgerEntries.Count;
                    hasLedger = ledgerCount > 0;
                }

                if (assertions.AssertTrace)
                {
                    var traceEvents =
                        await mcp
                            .GetTraceByExecutionAsync(executionId!)
                            .ConfigureAwait(false);

                    traceCount = traceEvents.Count;
                    hasTrace = traceCount > 0;
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
                        await mcp
                            .ReplayExecutionAsync(replayRequest)
                            .ConfigureAwait(false);

                    replaySuccess = replayResult.Success;
                    replayMessage = replayResult.FailureReason ?? replayResult.Message;

                    if (assertions.AssertReplayReport)
                    {
                        replayRequest.Operation = AiReplayOperation.GetReport;

                        var replayReport =
                            await mcp
                                .GetReplayReportAsync(replayRequest)
                                .ConfigureAwait(false);

                        hasReplayReport = replayResult.Success && replayReport.Success;
                    }

                    if (assertions.AssertReplayLedger)
                    {
                        replayRequest.Operation = AiReplayOperation.GetLedger;

                        var replayLedger =
                            await mcp
                                .GetReplayLedgerAsync(replayRequest)
                                .ConfigureAwait(false);

                        hasReplayLedger = replayLedger.Success;
                    }

                    if (assertions.AssertReplayTrace)
                    {
                        replayRequest.Operation = AiReplayOperation.GetTimeline;

                        var replayTrace =
                            await mcp
                                .GetReplayTraceAsync(replayRequest)
                                .ConfigureAwait(false);

                        hasReplayTrace = replayTrace.Success;
                    }
                }

                output.WriteLine(
                    $"[{logPrefix}][OBSERVABILITY DEBUG] ExecutionId='{executionId}', LedgerCount='{ledgerCount}', TraceCount='{traceCount}', HasLedger='{hasLedger}', HasTrace='{hasTrace}', ReplaySuccess='{replaySuccess}', ReplayMessage='{replayMessage}', HasReplayReport='{hasReplayReport}', HasReplayLedger='{hasReplayLedger}', HasReplayTrace='{hasReplayTrace}'.");

                var childDagExecutions =
                    tenant.Run.ChildDepth == 0
                        ? Array.Empty<ProductionChildDagScenarioResult>()
                        : childRelationStore is not null
                            ? await ProductionChildDagScenarioHelpers
                                .WaitForNestedRelationsAsync(
                                    childRelationStore,
                                    tenant.TenantId,
                                    executionId!,
                                    pipelineName,
                                    tenant.Run.ChildDepth,
                                    childRelationTimeout,
                                    cancellationToken)
                                .ConfigureAwait(false)
                            : throw new InvalidOperationException(
                                "Child DAG scenario results require an authoritative child execution relation store.");

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
                        HasReplayTrace = hasReplayTrace,
                        ChildDagExecutions = childDagExecutions,
                        ChildDagRuntimeFailure = childRuntimeFailureResult
                    });
            }

            return results;
        }

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
                $"Could not match runtime queue status to a dispatched shared run. StatusRuntimeInstanceId='{status.RuntimeInstanceId}', StatusRunId='{status.RunId}'." +
                Environment.NewLine +
                dump);

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        private static async Task<string> BuildLedgerDumpAsync(
            McpTestClient mcp,
            string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                return string.Empty;
            }

            var entries =
                await mcp
                    .GetLedgerByExecutionAsync(executionId)
                    .ConfigureAwait(false);

            if (entries.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                " || ",
                entries
                    .TakeLast(10)
                    .Select(entry => JsonSerializer.Serialize(entry)));
        }

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

            return "Runtime run did not complete successfully." +
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
                $"Ledger='{ledgerDump ?? string.Empty}'." +
                Environment.NewLine +
                "RawStatusJson=" +
                Environment.NewLine +
                statusJson;
        }

        private sealed record ProductionTenantMcpContext(
            ProductionTenantScenarioDefinition Tenant,
            HttpClient HttpClient,
            McpTestClient Mcp);

        private sealed record ChildDagRuntimeFailureServices(
            IConnectionMultiplexer ConnectionMultiplexer,
            IAiRuntimeHostProcessControl ProcessControl,
            IAiRuntimeInstanceRegistry Registry,
            IAiRuntimeRunExecutionIndex RunExecutionIndex);
    }
}
