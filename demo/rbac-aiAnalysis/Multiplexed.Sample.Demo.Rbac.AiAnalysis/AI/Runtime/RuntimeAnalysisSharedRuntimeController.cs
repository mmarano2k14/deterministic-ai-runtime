using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    /// <summary>
    /// Adapts the existing shared runtime controller to the sample API's
    /// background Child DAG execution context.
    /// </summary>
    /// <remarks>
    /// The native Child DAG dispatcher already carries the exact delegated,
    /// durable execution-context snapshot on the child run request.
    ///
    /// The shared runtime controller also asks IExecutionContextSnapshotProvider
    /// for the current operation context. During a background runtime step there
    /// is no active ASP.NET/RBAC AsyncLocal request context, so the sample pushes
    /// the exact delegated child snapshot only for the duration of that existing
    /// controller call.
    ///
    /// This decorator does not change child-DAG, queue, admission, execution,
    /// recovery, or approval semantics.
    /// </remarks>
    public sealed class RuntimeAnalysisSharedRuntimeController :
        IAiSharedRuntimeController
    {
        private readonly AiSharedRuntimeController _inner;
        private readonly RuntimeAnalysisExecutionContextSnapshotFactory
            _snapshotFactory;

        public RuntimeAnalysisSharedRuntimeController(
            AiSharedRuntimeController inner,
            RuntimeAnalysisExecutionContextSnapshotFactory snapshotFactory)
        {
            _inner =
                inner
                ?? throw new ArgumentNullException(
                    nameof(inner));

            _snapshotFactory =
                snapshotFactory
                ?? throw new ArgumentNullException(
                    nameof(snapshotFactory));
        }

        public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithDurableRequestSnapshotAsync(
                request,
                token => _inner.ExecuteAsync(
                    request,
                    token),
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithDurableRequestSnapshotAsync(
                request,
                token => _inner.SubmitRunAsync(
                    request,
                    token),
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> GetRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithDurableRequestSnapshotAsync(
                request,
                token => _inner.GetRunAsync(
                    request,
                    token),
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithDurableRequestSnapshotAsync(
                request,
                token => _inner.ListRunsAsync(
                    request,
                    token),
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithDurableRequestSnapshotAsync(
                request,
                token => _inner.CancelRunAsync(
                    request,
                    token),
                cancellationToken);
        }

        private async Task<AiSharedRuntimeControllerResult>
            ExecuteWithDurableRequestSnapshotAsync(
                AiSharedRuntimeControllerRequest request,
                Func<CancellationToken, Task<AiSharedRuntimeControllerResult>>
                    operation,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                request);
            ArgumentNullException.ThrowIfNull(
                operation);

            ExecutionContextSnapshot? snapshot =
                request.RunRequest?.ExecutionContextSnapshot;

            if (snapshot is null)
            {
                return await operation(
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            using var snapshotScope =
                _snapshotFactory.PushSnapshot(
                    snapshot);

            return await operation(
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
