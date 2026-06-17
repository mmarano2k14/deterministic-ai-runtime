using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Rbac.Core.Authorization.Attributes;
using System.ComponentModel;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to shared runtime runs.
    /// </summary>
    /// <remarks>
    /// This tool class only consumes the shared runtime control-plane.
    /// It does not execute DAG steps, claim step leases, or bypass runtime queues.
    /// </remarks>
    [McpServerToolType]
    public sealed class SharedRunMcpTools
    {
        private readonly IAiSharedRuntimeController sharedRuntimeController;
        private readonly ILogger<SharedRunMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunMcpTools"/> class.
        /// </summary>
        /// <param name="sharedRuntimeController">The shared runtime controller.</param>
        /// <param name="logger">The logger.</param>
        public SharedRunMcpTools(
            IAiSharedRuntimeController sharedRuntimeController,
            ILogger<SharedRunMcpTools> logger)
        {
            this.sharedRuntimeController = sharedRuntimeController
                ?? throw new ArgumentNullException(nameof(sharedRuntimeController));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Submits one shared run to the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller result.</returns>
        [McpServerTool(Name = "run.submit_run")]
        [Description("Submits one run to the shared runtime controller and shared queue.")]
        [RequireCapability("shared-run", "execution", "submit")]
        public async Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP submit_run called. PipelineKey={PipelineKey}, TenantId={TenantId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.PipelineKey,
                request.TenantId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            var result = await sharedRuntimeController
                .SubmitRunAsync(request, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "MCP submit_run completed. Success={Success}, SharedRunId={SharedRunId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, AssignedRuntimeInstanceId={AssignedRuntimeInstanceId}, FailureReason={FailureReason}",
                result.Success,
                result.SharedRunId,
                result.LocalRunId,
                result.ExecutionId,
                result.AssignedRuntimeInstanceId,
                result.FailureReason);

            return result;
        }

        /// <summary>
        /// Submits multiple shared runs to the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request used as a template.</param>
        /// <param name="count">The number of runs to submit.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller results.</returns>
        [McpServerTool(Name = "run.submit_many_runs")]
        [Description("Submits multiple runs to the shared runtime controller and shared queue.")]
        [RequireCapability("shared-run", "execution", "submit")]
        public async Task<IReadOnlyList<AiSharedRuntimeControllerResult>> SubmitManyRunsAsync(
            AiSharedRuntimeControllerRequest request,
            int count,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    "Count must be greater than zero.");
            }

            logger.LogInformation(
                "MCP submit_many_runs started. Count={Count}, PipelineKey={PipelineKey}, TenantId={TenantId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                count,
                request.PipelineKey,
                request.TenantId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            var results = new List<AiSharedRuntimeControllerResult>(count);

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                request.RequestedSharedRunId = null;
                request.SharedRunId = null;
                request.Operation = AiSharedRuntimeControllerOperation.SubmitRun;

                var result = await sharedRuntimeController
                    .SubmitRunAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(result);
            }

            logger.LogInformation(
                "MCP submit_many_runs completed. Count={Count}, SuccessCount={SuccessCount}, FailedCount={FailedCount}",
                count,
                results.Count(result => result.Success),
                results.Count(result => !result.Success));

            return results;
        }

        /// <summary>
        /// Lists shared runs known by the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller result.</returns>
        [McpServerTool(Name = "run.list_shared")]
        [Description("Lists shared runs known by the shared runtime controller.")]
        [RequireCapability("shared-run", "registry", "list")]
        public async Task<AiSharedRuntimeControllerResult> ListSharedRunsAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP run.list_shared called. IncludeCompleted={IncludeCompleted}, IncludeFailed={IncludeFailed}, IncludeCancelled={IncludeCancelled}",
                request.IncludeCompleted,
                request.IncludeFailed,
                request.IncludeCancelled);

            return await sharedRuntimeController
                .ListRunsAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a shared run known by the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller result.</returns>
        [McpServerTool(Name = "run.get_shared")]
        [Description("Gets a shared run known by the shared runtime controller.")]
        [RequireCapability("shared-run", "registry", "read")]
        public async Task<AiSharedRuntimeControllerResult> GetSharedRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP run.get_shared called. SharedRunId={SharedRunId}",
                request.SharedRunId);

            return await sharedRuntimeController
                .GetRunAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Cancels a shared run through the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller result.</returns>
        [McpServerTool(Name = "run.cancel_shared")]
        [Description("Cancels a shared run through the shared runtime controller.")]
        [RequireCapability("shared-run", "execution", "cancel")]
        public async Task<AiSharedRuntimeControllerResult> CancelSharedRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP run.cancel_shared called. SharedRunId={SharedRunId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.SharedRunId,
                request.Reason,
                request.RequestedBy);

            return await sharedRuntimeController
                .CancelRunAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}