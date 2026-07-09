using ModelContextProtocol.Protocol;
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
        /// <returns>The submitted shared run identifier.</returns>
        public static async Task<string> SubmitOneRunAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string requestedBy,
            string source)
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
                    RequestedBy = requestedBy,
                    Source = source,
                    Metadata = metadata,
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: tenant.Run.StepCount,
                        input: input,
                        enableRetention: tenant.Run.EnableRetention,
                        flakyStepInterval: tenant.Run.FlakyStepInterval)
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
        /// <returns>A task that completes when a fulfilled tenant scale-out request is observed.</returns>
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

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> lastRequests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRequests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                MaxResults = 100
                            })
                        .ConfigureAwait(false);

                if (lastRequests.Any(request =>
                        request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled &&
                        !string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId)))
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"No fulfilled scale-out request was observed within '{timeout}'. " +
                $"ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', PipelineKey='{pipelineName}', " +
                $"ObservedRequests='{lastRequests.Count}'.");
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
            TimeSpan dispatchTimeout)
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
                        source)
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