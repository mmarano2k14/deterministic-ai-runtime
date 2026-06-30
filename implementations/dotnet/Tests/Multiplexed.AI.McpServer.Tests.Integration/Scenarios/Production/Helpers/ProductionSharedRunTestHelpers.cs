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
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="requestedBy">The logical requester identifier.</param>
        /// <param name="source">The logical request source.</param>
        /// <returns>The submitted shared run identifier.</returns>
        public static async Task<string> SubmitOneRunAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string requestedBy,
            string source)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(tenant);
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

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = tenant.TenantId,
                    RequestedBy = requestedBy,
                    Source = source,
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
            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
                        pipelineName,
                        requestedBy,
                        source)
                    .ConfigureAwait(false);

            await ProductionSharedRunTestHelpers
                .WaitForAnyTenantScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    controlPlaneId,
                    tenant,
                    pipelineName,
                    scaleOutTimeout)
                .ConfigureAwait(false);

            return await ProductionSharedRunTestHelpers
                .WaitForSingleDispatchedRunAsync(
                    mcp,
                    pipelineName,
                    sharedRunId,
                    dispatchTimeout)
                .ConfigureAwait(false);
        }
    }
}