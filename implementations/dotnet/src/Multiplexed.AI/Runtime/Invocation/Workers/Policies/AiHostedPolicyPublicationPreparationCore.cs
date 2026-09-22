using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Centralizes the common durable-owner, RBAC-context, publication authorization,
    /// immutable artifact materialization, and post-I/O ownership revalidation sequence
    /// shared by hosted custom-policy families. Policy-family request validation and
    /// business semantics remain outside this component.
    /// </summary>
    internal sealed class AiHostedPolicyPublicationPreparationCore
    {
        private readonly IAiExecutionStore _executions;
        private readonly IAiDagExecutionStore? _dag;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;

        internal AiHostedPolicyPublicationPreparationCore(
            IAiExecutionStore executions,
            IExecutionContextAccessor accessor,
            IAiControlPlaneIdResolver controlPlane,
            AiPublicationIdentity identity,
            AiPublicationOptions options,
            IAiDagExecutionStore? dag = null)
        {
            _executions = executions ?? throw new ArgumentNullException(nameof(executions));
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _dag = dag;
        }

        internal async Task<AiWorkerCodeBundle> PrepareAsync<TRequest>(
            TRequest request,
            string executionId,
            string tenantId,
            string tenantGroupId,
            string unavailableMessage,
            string ownershipMessage,
            string terminalMessage,
            string changedMessage,
            Func<TRequest, AiPublicationIdentity.Guard, CancellationToken, Task<AiWorkerCodeBundle>> readWorkerCodeAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(readWorkerCodeAsync);
            cancellationToken.ThrowIfCancellationRequested();

            var parent = await GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(unavailableMessage);

            var snapshot = AiHostedPolicyOwnershipRevalidator.ValidateInitial(
                parent,
                executionId,
                tenantId,
                tenantGroupId,
                ownershipMessage,
                terminalMessage);

            var controlPlaneId = await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new AiDurableInvocationScope(snapshot.TenantId!, snapshot.TenantGroupId!, controlPlaneId);
            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));

            try
            {
                var guard = await _identity.AuthorizeAsync(scope, _options.Execute, cancellationToken).ConfigureAwait(false);
                var result = await readWorkerCodeAsync(request, guard, cancellationToken).ConfigureAwait(false);
                guard.RequireCurrent();

                var current = await GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false);
                AiHostedPolicyOwnershipRevalidator.RequireCurrent(
                    current,
                    executionId,
                    snapshot,
                    changedMessage);

                guard.RequireCurrent();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                if (previous is null)
                {
                    _accessor.Clear();
                }
                else
                {
                    _accessor.Set(previous);
                }
            }
        }

        private Task<AiExecutionRecord?> GetRecordAsync(string executionId, CancellationToken cancellationToken)
            => _dag is null
                ? _executions.GetRecordAsync(executionId, cancellationToken)
                : _dag.GetRecordAsync(executionId, cancellationToken);
    }
}
