using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    internal sealed class PolicyOwnershipDriftExecutionStore : IAiExecutionStore
    {
        private readonly IAiExecutionStore _inner;
        private readonly Func<ExecutionContextSnapshot, ExecutionContextSnapshot> _mutate;
        private int _recordReads;

        internal PolicyOwnershipDriftExecutionStore(
            IAiExecutionStore inner,
            Func<ExecutionContextSnapshot, ExecutionContextSnapshot> mutate)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _mutate = mutate ?? throw new ArgumentNullException(nameof(mutate));
        }

        public Task CreateAsync(AiExecutionRecord record, AiExecutionState state, CancellationToken cancellationToken = default) =>
            _inner.CreateAsync(record, state, cancellationToken);

        public async Task<AiExecutionRecord?> GetRecordAsync(string executionId, CancellationToken cancellationToken = default)
        {
            var record = await _inner.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (record is null || Interlocked.Increment(ref _recordReads) == 1)
            {
                return record;
            }

            return new AiExecutionRecord
            {
                ExecutionId = record.ExecutionId,
                Status = record.Status,
                ExecutionContextSnapshot = record.ExecutionContextSnapshot is null
                    ? null
                    : _mutate(Clone(record.ExecutionContextSnapshot))
            };
        }

        public Task<AiExecutionState?> GetStateAsync(string executionId, CancellationToken cancellationToken = default) =>
            _inner.GetStateAsync(executionId, cancellationToken);

        public Task<bool> TryUpdateAsync(string executionId, string expectedStepKey, AiExecutionRecord record, AiExecutionState state, CancellationToken cancellationToken = default) =>
            _inner.TryUpdateAsync(executionId, expectedStepKey, record, state, cancellationToken);

        public Task SaveRecordAsync(AiExecutionRecord record, CancellationToken cancellationToken = default) =>
            _inner.SaveRecordAsync(record, cancellationToken);

        public Task SaveStateAsync(string executionId, AiExecutionState state, CancellationToken cancellationToken = default) =>
            _inner.SaveStateAsync(executionId, state, cancellationToken);

        public Task DeleteRecordAsync(string executionId, CancellationToken cancellationToken = default) =>
            _inner.DeleteRecordAsync(executionId, cancellationToken);

        public Task DeleteStateAsync(string executionId, CancellationToken cancellationToken = default) =>
            _inner.DeleteStateAsync(executionId, cancellationToken);

        public Task RestoreAsync(AiExecutionRecord record, AiExecutionState state, CancellationToken cancellationToken = default) =>
            _inner.RestoreAsync(record, state, cancellationToken);

        private static ExecutionContextSnapshot Clone(ExecutionContextSnapshot source) => new()
        {
            ContextKey = source.ContextKey,
            Project = source.Project,
            UserId = source.UserId,
            TenantId = source.TenantId,
            TenantGroupId = source.TenantGroupId,
            CurrentNamespace = source.CurrentNamespace,
            Namespaces = source.Namespaces.ToList(),
            InFlightCount = source.InFlightCount,
            TtlSeconds = source.TtlSeconds,
            CreatedAtUtc = source.CreatedAtUtc
        };
    }
}
