using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.AI.McpServer.Tools;
using Multiplexed.Rbac.Core.Authorization.Attributes;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class ExecutionControlMcpHumanInputToolTests
    {
        [Fact]
        public async Task Submit_Human_Input_Tool_Delegates_To_Existing_Control_Plane_And_Keeps_Its_Capability()
        {
            var controlPlane = new RecordingControlPlane();
            var tools = new ExecutionControlMcpTools(
                controlPlane,
                NullLogger<ExecutionControlMcpTools>.Instance);
            var request = new AiExecutionControlPlaneRequest
            {
                ExecutionId = "exec-input",
                Operation = AiExecutionControlPlaneOperation.SubmitHumanInput,
                WaitingKey = "approval:pricing",
                Input = new Dictionary<string, object?> { ["approved"] = true },
                RequestedBy = "user-a"
            };

            var response = await tools.SubmitHumanInputAsync(request);

            Assert.Same(request, controlPlane.SubmittedInput);
            Assert.True(response.Success);
            Assert.Equal("exec-input", response.ExecutionId);

            var method = typeof(ExecutionControlMcpTools).GetMethod(nameof(ExecutionControlMcpTools.SubmitHumanInputAsync))
                ?? throw new InvalidOperationException("Human input MCP tool was not found.");
            var tool = Assert.Single(method.GetCustomAttributes<McpServerToolAttribute>(true));
            Assert.Equal("control.input.submit", tool.Name);
            var capability = Assert.Single(method.GetCustomAttributes<RequireCapabilityAttribute>(true));
            Assert.Equal("execution", capability.Resource);
            Assert.Equal("control", capability.Feature);
            Assert.Equal("input", capability.Action);
        }

        private sealed class RecordingControlPlane : IAiExecutionControlPlane
        {
            internal AiExecutionControlPlaneRequest? SubmittedInput { get; private set; }

            public Task<AiExecutionControlPlaneResult> SubmitHumanInputAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                SubmittedInput = request;
                return Task.FromResult(Success(request, AiExecutionControlPlaneOperation.SubmitHumanInput));
            }

            public Task<AiExecutionControlPlaneResult> ExecuteAsync(AiExecutionControlPlaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> PauseAsync(AiExecutionControlPlaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> ResumeAsync(AiExecutionControlPlaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> CancelAsync(AiExecutionControlPlaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> GetStatusAsync(AiExecutionControlPlaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            private static AiExecutionControlPlaneResult Success(
                AiExecutionControlPlaneRequest request,
                AiExecutionControlPlaneOperation operation) => new()
                {
                    ExecutionId = request.ExecutionId,
                    Operation = operation,
                    Success = true,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = DateTimeOffset.UnixEpoch,
                    CompletedAtUtc = DateTimeOffset.UnixEpoch
                };
        }
    }
}
