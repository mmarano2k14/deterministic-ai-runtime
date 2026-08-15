using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support
{
    /// <summary>
    /// Captures shared runtime submissions while preserving the requested run identity for composition unit tests.
    /// </summary>
    internal sealed class CapturingSharedRuntimeController : IAiSharedRuntimeController
    {
        private readonly object gate = new();
        private readonly List<AiSharedRuntimeControllerRequest> requests = [];

        public IReadOnlyList<AiSharedRuntimeControllerRequest> Requests
        {
            get
            {
                lock (this.gate)
                {
                    return this.requests.ToArray();
                }
            }
        }

        public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default) => SubmitRunAsync(request, cancellationToken);

        public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                this.requests.Add(request);
            }

            var now = DateTimeOffset.UtcNow;
            var run = new AiSharedRunRecord
            {
                SharedRunId = request.RequestedSharedRunId!,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = request.RunRequest!,
                ExecutionContextSnapshot = request.RunRequest!.ExecutionContextSnapshot!,
                PipelineKey = request.PipelineKey,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = request.Metadata
            };

            return Task.FromResult(
                new AiSharedRuntimeControllerResult
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    Success = true,
                    SharedRunId = run.SharedRunId,
                    Run = run,
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                });
        }

        public Task<AiSharedRuntimeControllerResult> GetRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
