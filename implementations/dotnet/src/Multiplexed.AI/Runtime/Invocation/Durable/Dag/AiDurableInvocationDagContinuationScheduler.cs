using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Submits normal external-wait continuations through the existing shared controller and queue.</summary>
    public sealed class AiDurableInvocationDagContinuationScheduler
    {
        private readonly IAiSharedRuntimeController _controller;
        private readonly IAiSharedQueue _queue;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        public AiDurableInvocationDagContinuationScheduler(IAiSharedRuntimeController controller, IAiSharedQueue queue,
            IExecutionContextAccessor accessor, IAiControlPlaneIdResolver controlPlane)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        }
        public static string SharedRunId(AiDurableInvocationRecord record) => "invocation-continuation-" + record.OperationId;
        public static string ContinuationId(AiDurableInvocationRecord record) => "invocation-continuation:" + record.OperationId;

        public async Task EnqueueAsync(AiDurableInvocationRecord invocation, AiExecutionRecord parent,
            CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateRecord(invocation);
            ArgumentNullException.ThrowIfNull(parent);
            var scope = invocation.Definition.Scope;
            var snapshot = parent.ExecutionContextSnapshot;
            if (invocation.ContinuationStatus != AiDurableInvocationContinuationStatus.Scheduled ||
                !AiDurableInvocationValidation.Terminal(invocation) || parent.IsTerminal ||
                parent.ExecutionMode != AiExecutionMode.Dag || parent.ExecutionId != invocation.Definition.Identity.ExecutionId ||
                parent.PipelineName != invocation.Definition.Target.PipelineName || snapshot?.TenantId != scope.TenantId ||
                snapshot.TenantGroupId != scope.TenantGroupId || string.IsNullOrWhiteSpace(snapshot.ContextKey) ||
                await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new InvalidOperationException("Continuation dispatch does not match the authoritative scheduled operation and parent.");
            var id = ContinuationId(invocation);
            var continuation = new AiRuntimeExternalWaitContinuation
            {
                ExecutionId = parent.ExecutionId, StepName = invocation.Definition.Identity.StepName, ContinuationId = id
            };
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiControlPlaneMetadataKeys.ControlPlaneId] = scope.ControlPlaneId,
                [AiRuntimeExternalWaitMetadataKeys.Continuation] = "true",
                [AiRuntimeExternalWaitMetadataKeys.ContinuationId] = id,
                [AiRuntimeExternalWaitMetadataKeys.ExecutionId] = parent.ExecutionId,
                [AiRuntimeExternalWaitMetadataKeys.Step] = continuation.StepName
            };
            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var accepted = await _controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun, RequestedSharedRunId = SharedRunId(invocation),
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst, TenantId = scope.TenantId,
                    PipelineKey = parent.PipelineName, CorrelationId = id, Source = "durable-custom-invocation",
                    Reason = "resume-durable-custom-result", Metadata = metadata,
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = parent.PipelineName!, ExternalWaitContinuation = continuation,
                        ExecutionContextSnapshot = snapshot, Metadata = metadata
                    }
                }, cancellationToken).ConfigureAwait(false);
                var actual = accepted.Run?.RunRequest.ExternalWaitContinuation;
                if (!accepted.Success || accepted.SharedRunId != SharedRunId(invocation) || actual is null ||
                    actual.ExecutionId != continuation.ExecutionId || actual.StepName != continuation.StepName ||
                    actual.ContinuationId != id || accepted.Run!.ExecutionContextSnapshot.TenantId != scope.TenantId ||
                    accepted.Run.ExecutionContextSnapshot.TenantGroupId != scope.TenantGroupId ||
                    !string.IsNullOrWhiteSpace(accepted.Run.RunRequest.RequestedExecutionId))
                    throw new InvalidOperationException("Shared continuation acceptance changed or rejected the exact operation identity.");
            }
            finally
            {
                if (previous is null) _accessor.Clear(); else _accessor.Set(previous);
            }
        }

        public async Task CancelAsync(AiDurableInvocationRecord invocation, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateRecord(invocation);
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != invocation.Definition.Scope.ControlPlaneId)
                throw new InvalidOperationException("A different control plane cannot cancel this continuation.");
            await _queue.CancelAsync(SharedRunId(invocation), "durable-invocation-continuation-terminal", cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
