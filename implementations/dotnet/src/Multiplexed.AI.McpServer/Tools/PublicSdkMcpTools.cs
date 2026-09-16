using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.Rbac.Core.Authorization.Attributes;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>Public wire boundary for SDK publication, submission, observation, result and cancellation.</summary>
    [McpServerToolType]
    public sealed class PublicSdkMcpTools
    {
        private readonly IServiceScopeFactory _scopeFactory;
        public PublicSdkMcpTools(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

        [McpServerTool(Name = AiSdkOperationNames.PublishPipeline)]
        [Description("Publishes one immutable pipeline using the versioned public SDK contract.")]
        public Task<AiSdkPipelinePublicationResponse> PublishPipelineAsync(AiSdkPipelinePublicationRequest request, CancellationToken cancellationToken = default) =>
            InScopeAsync(boundary => boundary.PublishAsync(request, cancellationToken));

        [McpServerTool(Name = AiSdkOperationNames.SubmitExecution)]
        [Description("Creates and submits one published execution through the existing shared submission path.")]
        [RequireCapability("shared-run", "execution", "submit")]
        public Task<AiSdkExecutionSubmissionResponse> SubmitExecutionAsync(AiSdkExecutionSubmissionRequest request, CancellationToken cancellationToken = default) =>
            InScopeAsync(boundary => boundary.SubmitAsync(request, cancellationToken));

        [McpServerTool(Name = AiSdkOperationNames.ObserveExecution)]
        [Description("Returns the logical state of one published execution without exposing runtime placement or leases.")]
        [RequireCapability("execution", "control", "read")]
        public Task<AiSdkExecutionObservation> ObserveExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            InScopeAsync(boundary => boundary.ObserveAsync(executionId, cancellationToken));

        [McpServerTool(Name = AiSdkOperationNames.GetExecutionResult)]
        [Description("Returns the sanitized terminal result of one published execution.")]
        [RequireCapability("execution", "control", "read")]
        public Task<AiSdkExecutionResult> GetExecutionResultAsync(string executionId, CancellationToken cancellationToken = default) =>
            InScopeAsync(boundary => boundary.GetResultAsync(executionId, cancellationToken));

        [McpServerTool(Name = AiSdkOperationNames.CancelExecution)]
        [Description("Requests cooperative cancellation of one published execution.")]
        [RequireCapability("execution", "control", "cancel")]
        public Task<AiSdkExecutionCancellationResponse> CancelExecutionAsync(string executionId, AiSdkExecutionCancellationRequest request, CancellationToken cancellationToken = default) =>
            InScopeAsync(boundary => boundary.CancelAsync(executionId, request, cancellationToken));

        private async Task<T> InScopeAsync<T>(Func<IAiPublicSdkBoundary, Task<T>> action)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IAiPublicSdkBoundary>()).ConfigureAwait(false);
        }
    }
}
