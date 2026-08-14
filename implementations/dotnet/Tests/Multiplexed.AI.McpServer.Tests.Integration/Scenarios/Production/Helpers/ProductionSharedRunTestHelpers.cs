using ModelContextProtocol.Protocol;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable helpers for submitting shared runs and waiting for shared-runtime dispatch in production scenarios.
    /// </summary>
    public static class ProductionSharedRunTestHelpers
    {
        /// <summary>
        /// Submits one shared run through the tenant-scoped MCP client.
        /// </summary>
        /// <param name="mcp">The configured MCP test client.</param>
        /// <param name="tenant">The production tenant scenario definition.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="requestedBy">The logical requester identifier.</param>
        /// <param name="source">The logical request source.</param>
        /// <param name="crashCheckpoint">The optional test-only durable crash checkpoint.</param>
        /// <param name="placement">The optional typed placement directive for this admission attempt.</param>
        /// <returns>The submitted shared run identifier.</returns>
        public static async Task<string> SubmitOneRunAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string requestedBy,
            string source,
            McpTestCrashCheckpointDefinition? crashCheckpoint = null,
            AiRunPlacementDirective? placement = null)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

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

            var metadata =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix
                };

            AddLogicalControlPlaneMetadata(
                metadata,
                controlPlaneId);

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = tenant.TenantId,
                    Placement = placement,
                    RequestedBy = requestedBy,
                    Source = source,
                    Metadata = metadata,
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: tenant.Run.StepCount,
                        input: input,
                        enableRetention: tenant.Run.EnableRetention,
                        flakyStepInterval: tenant.Run.FlakyStepInterval,
                        crashCheckpoint: crashCheckpoint)
                };

            var submitResults =
                await mcp
                    .SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            var submitResult =
                Assert.Single(submitResults);

            Assert.True(
                submitResult.Success,
                submitResult.FailureReason ?? submitResult.Message);

            return ExtractSharedRunId(submitResult);
        }

        /// <summary>
        /// Waits for the submitted shared run to become dispatched.
        /// </summary>
        /// <param name="mcp">The configured MCP test client.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The dispatched shared run record.</returns>
        public static async Task<AiSharedRunRecord> WaitForSingleDispatchedRunAsync(
            McpTestClient mcp,
            string pipelineName,
            string sharedRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            sharedRunId
                        },
                        expectedCount: 1,
                        timeout: timeout)
                    .ConfigureAwait(false);

            return Assert.Single(dispatchedRuns);
        }

        /// <summary>
        /// Waits until at least one tenant scale-out request is fulfilled.
        /// </summary>
        /// <param name="store">The runtime scale-out request store.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>
        /// A task that completes when a fulfilled tenant scale-out request is observed.
        /// </returns>
        public static async Task WaitForAnyTenantScaleOutRequestFulfilledAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The scale-out fulfillment timeout must be greater than zero.");
            }

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var deadline =
                startedAtUtc.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> lastRequests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRequests =
                    await ListTenantScaleOutRequestsAsync(
                            store,
                            controlPlaneId,
                            tenant.TenantId,
                            pipelineName)
                        .ConfigureAwait(false);

                if (HasFulfilledScaleOutRequest(lastRequests))
                {
                    return;
                }

                var remaining =
                    deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task
                    .Delay(
                        remaining < TimeSpan.FromMilliseconds(100)
                            ? remaining
                            : TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            /*
             * Perform one final durable read after the nominal deadline.
             *
             * Kubernetes may complete pod readiness and persist fulfillment exactly at
             * the timeout boundary. The durable store remains authoritative, so valid
             * convergence observed by this final read must be accepted.
             */
            lastRequests =
                await ListTenantScaleOutRequestsAsync(
                        store,
                        controlPlaneId,
                        tenant.TenantId,
                        pipelineName)
                    .ConfigureAwait(false);

            if (HasFulfilledScaleOutRequest(lastRequests))
            {
                return;
            }

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            var statusBreakdown =
                lastRequests.Count == 0
                    ? "(none)"
                    : string.Join(
                        ",",
                        lastRequests
                            .GroupBy(request => request.Status)
                            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                            .Select(group => $"{group.Key}:{group.Count()}"));

            var requestDiagnostics =
                lastRequests.Count == 0
                    ? "(no scale-out requests observed)"
                    : string.Join(
                        Environment.NewLine,
                        lastRequests
                            .OrderBy(request => request.CreatedAtUtc)
                            .ThenBy(request => request.RequestId, StringComparer.Ordinal)
                            .Select(
                                (request, index) =>
                                    FormatScaleOutRequestDiagnostic(
                                        request,
                                        index + 1,
                                        completedAtUtc)));

            Assert.Fail(
                $"No fulfilled scale-out request was observed within '{timeout}'. " +
                $"ControlPlaneId='{controlPlaneId}', " +
                $"TenantId='{tenant.TenantId}', " +
                $"TenantGroupId='{tenant.TenantGroupId}', " +
                $"PipelineKey='{pipelineName}', " +
                $"StartedAtUtc='{startedAtUtc:O}', " +
                $"CompletedAtUtc='{completedAtUtc:O}', " +
                $"Elapsed='{completedAtUtc - startedAtUtc}', " +
                $"ObservedRequests='{lastRequests.Count}', " +
                $"StatusBreakdown='{statusBreakdown}'." +
                Environment.NewLine +
                requestDiagnostics);
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        /// <param name="submitResult">The MCP submit result.</param>
        /// <returns>The extracted shared run identifier.</returns>
        public static string ExtractSharedRunId(
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
        /// Submits one shared run and waits until it has been dispatched to a runtime instance.
        /// </summary>
        /// <param name="mcp">The configured MCP test client.</param>
        /// <param name="scaleOutRequestStore">The runtime scale-out request store.</param>
        /// <param name="tenant">The production tenant scenario definition.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="requestedBy">The logical requester identifier.</param>
        /// <param name="source">The logical request source.</param>
        /// <param name="scaleOutTimeout">The scale-out fulfillment timeout.</param>
        /// <param name="dispatchTimeout">The dispatch timeout.</param>
        /// <param name="crashCheckpoint">The optional test-only durable crash checkpoint.</param>
        /// <returns>The dispatched shared run record.</returns>
        public static async Task<AiSharedRunRecord> SubmitAndDispatchOneRunAsync(
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string requestedBy,
            string source,
            TimeSpan scaleOutTimeout,
            TimeSpan dispatchTimeout,
            McpTestCrashCheckpointDefinition? crashCheckpoint = null)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(scaleOutRequestStore);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            var sharedRunId =
                await SubmitOneRunAsync(
                        mcp,
                        tenant,
                        controlPlaneId,
                        pipelineName,
                        requestedBy,
                        source,
                        crashCheckpoint)
                    .ConfigureAwait(false);

            await WaitForAnyTenantScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    controlPlaneId,
                    tenant,
                    pipelineName,
                    scaleOutTimeout)
                .ConfigureAwait(false);

            await DumpDispatchStateBeforeWaitAsync(
                    mcp,
                    tenant,
                    controlPlaneId,
                    pipelineName,
                    sharedRunId)
                .ConfigureAwait(false);

            return await WaitForSingleDispatchedRunAsync(
                    mcp,
                    pipelineName,
                    sharedRunId,
                    dispatchTimeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Lists scale-out requests belonging to one tenant pipeline within one logical
        /// control plane.
        /// </summary>
        /// <param name="store">The runtime scale-out request store.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <returns>The matching durable scale-out request records.</returns>
        private static Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>>
            ListTenantScaleOutRequestsAsync(
                IAiRuntimeScaleOutRequestStore store,
                string controlPlaneId,
                string tenantId,
                string pipelineName)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            return store.ListAsync(
                new AiRuntimeScaleOutRequestQuery
                {
                    ControlPlaneId = controlPlaneId,
                    TenantId = tenantId,
                    PipelineKey = pipelineName,
                    MaxResults = 100
                });
        }

        /// <summary>
        /// Determines whether the observed durable scale-out requests contain a
        /// fulfilled request associated with a concrete runtime instance.
        /// </summary>
        /// <param name="requests">The observed scale-out requests.</param>
        /// <returns>
        /// <see langword="true"/> when a fulfilled runtime instance is present;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        private static bool HasFulfilledScaleOutRequest(
            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);

            return requests.Any(request =>
                request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled &&
                !string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId));
        }

        /// <summary>
        /// Formats one durable scale-out request for timeout diagnostics.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <param name="position">The one-based diagnostic position.</param>
        /// <param name="observedAtUtc">The timestamp of the diagnostic snapshot.</param>
        /// <returns>A detailed single-line diagnostic description.</returns>
        private static string FormatScaleOutRequestDiagnostic(
            AiRuntimeScaleOutRequestRecord request,
            int position,
            DateTimeOffset observedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(request);

            var age =
                observedAtUtc >= request.CreatedAtUtc
                    ? observedAtUtc - request.CreatedAtUtc
                    : TimeSpan.Zero;

            var metadata =
                request.Metadata.Count == 0
                    ? "(none)"
                    : string.Join(
                        ",",
                        request.Metadata
                            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(pair => $"{pair.Key}={pair.Value}"));

            return
                $"{position:00}. " +
                $"RequestId='{request.RequestId}', " +
                $"Status='{request.Status}', " +
                $"ProviderHint='{request.ProviderHint ?? string.Empty}', " +
                $"SharedRunId='{request.SharedRunId}', " +
                $"ControlPlaneId='{request.ControlPlaneId}', " +
                $"TenantId='{request.TenantId ?? string.Empty}', " +
                $"TenantGroupId='{request.TenantGroupId ?? string.Empty}', " +
                $"PipelineKey='{request.PipelineKey ?? string.Empty}', " +
                $"IsolationMode='{request.IsolationMode}', " +
                $"PreferDedicatedCapacity='{request.PreferDedicatedCapacity}', " +
                $"AllowSharedFallback='{request.AllowSharedFallback}', " +
                $"VisibleInstanceCount='{request.VisibleInstanceCount}', " +
                $"AvailableInstanceCount='{request.AvailableInstanceCount}', " +
                $"CurrentInstanceCount='{request.CurrentInstanceCount}', " +
                $"MaxInstanceCount='{request.MaxInstanceCount?.ToString() ?? string.Empty}', " +
                $"RequestedTargetInstanceCount='{request.RequestedTargetInstanceCount}', " +
                $"RuntimeInstanceIdPrefix='{request.RuntimeInstanceIdPrefix ?? string.Empty}', " +
                $"FulfilledRuntimeInstanceId='{request.FulfilledRuntimeInstanceId ?? string.Empty}', " +
                $"Reason='{request.Reason}', " +
                $"RejectionReason='{request.RejectionReason ?? string.Empty}', " +
                $"RequestedBy='{request.RequestedBy ?? string.Empty}', " +
                $"Source='{request.Source ?? string.Empty}', " +
                $"ObservedBy='{request.ObservedBy ?? string.Empty}', " +
                $"FulfilledBy='{request.FulfilledBy ?? string.Empty}', " +
                $"RejectedBy='{request.RejectedBy ?? string.Empty}', " +
                $"CreatedAtUtc='{request.CreatedAtUtc:O}', " +
                $"Age='{age}', " +
                $"ObservedAtUtc='{request.ObservedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"FulfilledAtUtc='{request.FulfilledAtUtc?.ToString("O") ?? string.Empty}', " +
                $"RejectedAtUtc='{request.RejectedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"ExpiredAtUtc='{request.ExpiredAtUtc?.ToString("O") ?? string.Empty}', " +
                $"CancelledAtUtc='{request.CancelledAtUtc?.ToString("O") ?? string.Empty}', " +
                $"ExpiresAtUtc='{request.ExpiresAtUtc?.ToString("O") ?? string.Empty}', " +
                $"CorrelationId='{request.CorrelationId ?? string.Empty}', " +
                $"Metadata='{metadata}'.";
        }

        /// <summary>
        /// Dumps shared run state after scale-out fulfillment and before waiting for dispatch.
        /// </summary>
        /// <param name="mcp">The configured MCP test client.</param>
        /// <param name="tenant">The production tenant scenario definition.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>A task that completes when diagnostics have been written.</returns>
        private static async Task DumpDispatchStateBeforeWaitAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string sharedRunId)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            Console.WriteLine(
                $"[DISPATCH DEBUG BEFORE WAIT] ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', Pipeline='{pipelineName}', SharedRunId='{sharedRunId}'.");

            var result =
                await mcp
                    .ListSharedRunsAsync(
                        new AiSharedRuntimeControllerRequest
                        {
                            Operation = AiSharedRuntimeControllerOperation.ListRuns,
                            TenantId = tenant.TenantId,
                            PipelineKey = pipelineName,
                            IncludeCompleted = true,
                            IncludeFailed = true,
                            IncludeCancelled = true,
                            IncludeDiagnostics = true,
                            CorrelationId = $"dispatch-debug-{sharedRunId}",
                            RequestedBy = "production-shared-run-test-helper",
                            Source = "integration-test",
                            Reason = "Dump shared run state after scale-out fulfillment before dispatch wait.",
                            Metadata = new Dictionary<string, string>
                            {
                                ["controlPlaneId"] = controlPlaneId,
                                ["tenant.id"] = tenant.TenantId,
                                ["tenant.group.id"] = tenant.TenantGroupId,
                                ["pipelineName"] = pipelineName,
                                ["sharedRunId"] = sharedRunId
                            }
                        })
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[DISPATCH DEBUG RESULT] Success='{result.Success}', Message='{result.Message}', SharedRunCount='{result.Runs.Count}'.");

            foreach (var run in result.Runs)
            {
                Console.WriteLine(
                    $"[DISPATCH DEBUG RUN] SharedRunId='{run.SharedRunId}', Status='{run.Status}', AssignedRuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{run.ExecutionId}', FailureReason='{run.FailureReason}'.");
            }
        }

        /// <summary>
        /// Adds logical control-plane identity metadata to a shared-run submission.
        /// </summary>
        /// <param name="metadata">The metadata dictionary to mutate.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        private static void AddLogicalControlPlaneMetadata(
            IDictionary<string, string> metadata,
            string controlPlaneId)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            metadata["logicalControlPlaneId"] = controlPlaneId;
            metadata["controlPlaneId"] = controlPlaneId;
            metadata["control-plane.id"] = controlPlaneId;
            metadata["controlplane.id"] = controlPlaneId;
            metadata["runtime.controlPlaneId"] = controlPlaneId;
            metadata["runtime.control-plane.id"] = controlPlaneId;
            metadata["runtime.controlplane.id"] = controlPlaneId;
            metadata["scenario.controlPlaneId"] = controlPlaneId;
            metadata["scenario.control-plane.id"] = controlPlaneId;
            metadata["scenario.controlplane.id"] = controlPlaneId;
            metadata["scaleout.controlPlaneId"] = controlPlaneId;
            metadata["scaleout.control-plane.id"] = controlPlaneId;
            metadata["scaleout.controlplane.id"] = controlPlaneId;
        }
    }
}