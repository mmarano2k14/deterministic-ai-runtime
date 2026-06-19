using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Rbac.Core.Authorization.Attributes;
using System.ComponentModel;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to execution control-plane operations.
    /// </summary>
    /// <remarks>
    /// This tool class only consumes the execution control-plane.
    /// It does not execute DAG steps, claim work, or modify runtime queues directly.
    /// </remarks>
    [McpServerToolType]
    public sealed class ExecutionControlMcpTools
    {
        private readonly IAiExecutionControlPlane executionControlPlane;
        private readonly ILogger<ExecutionControlMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecutionControlMcpTools"/> class.
        /// </summary>
        /// <param name="executionControlPlane">The execution control-plane.</param>
        /// <param name="logger">The logger.</param>
        public ExecutionControlMcpTools(
            IAiExecutionControlPlane executionControlPlane,
            ILogger<ExecutionControlMcpTools> logger)
        {
            this.executionControlPlane = executionControlPlane
                ?? throw new ArgumentNullException(nameof(executionControlPlane));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Requests cooperative pause for an execution.
        /// </summary>
        [McpServerTool(Name = "control.pause")]
        [Description("Requests cooperative pause for an execution.")]
        [RequireCapability("execution", "control", "pause")]
        public async Task<AiExecutionControlPlaneResult> PauseExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP control.pause called. ExecutionId={ExecutionId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.ExecutionId,
                request.Reason,
                request.RequestedBy);

            return await executionControlPlane
                .PauseAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Requests cooperative resume for an execution.
        /// </summary>
        [McpServerTool(Name = "control.resume")]
        [Description("Requests cooperative resume for an execution.")]
        [RequireCapability("execution", "control", "resume")]
        public async Task<AiExecutionControlPlaneResult> ResumeExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP control.resume called. ExecutionId={ExecutionId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.ExecutionId,
                request.Reason,
                request.RequestedBy);

            return await executionControlPlane
                .ResumeAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Requests cooperative cancellation for an execution.
        /// </summary>
        [McpServerTool(Name = "control.cancel")]
        [Description("Requests cooperative cancellation for an execution.")]
        [RequireCapability("execution", "control", "cancel")]
        public async Task<AiExecutionControlPlaneResult> CancelExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP control.cancel called. ExecutionId={ExecutionId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.ExecutionId,
                request.Reason,
                request.RequestedBy);

            return await executionControlPlane
                .CancelAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the current durable execution control status.
        /// </summary>
        [McpServerTool(Name = "control.status")]
        [Description("Gets the current durable execution control status.")]
        [RequireCapability("execution", "control", "read")]
        public async Task<AiExecutionControlPlaneResult> GetExecutionStatusAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP control.status called. ExecutionId={ExecutionId}, RequestedBy={RequestedBy}",
                request.ExecutionId,
                request.RequestedBy);

            return await executionControlPlane
                .GetStatusAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}