using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>Validates ordered store hints; each candidate still requires a separate authoritative lease CAS.</summary>
    public sealed class AiWorkerDispatchPageReader
    {
        private readonly IAiDurableInvocationStore _store;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly TimeProvider _time;
        public AiWorkerDispatchPageReader(IAiDurableInvocationStore store, IAiControlPlaneIdResolver controlPlane,
            TimeProvider? timeProvider = null) { _store = store; _controlPlane = controlPlane; _time = timeProvider ?? TimeProvider.System; }
        public async Task<IReadOnlyList<AiDurableInvocationRecord>> ReadAsync(AiDurableInvocationScope scope,
            string language, int maxCount, AiWorkerDispatchCursor? after = null, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateScope(scope); AiDurableInvocationValidation.ValidateLanguage(language);
            if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new UnauthorizedAccessException("The control plane cannot poll this worker scope.");
            if (_store is not IAiDurableInvocationDispatchPageStore pages)
                throw new NotSupportedException("Worker polling requires the journal's bounded keyset-page capability.");
            var result = await pages.ListDispatchPageAsync(scope, language, _time.GetUtcNow(), maxCount, after, cancellationToken).ConfigureAwait(false);
            if (result.Count > maxCount) throw new InvalidOperationException("Worker dispatch page exceeded its bound.");
            var cursor = after;
            foreach (var record in result)
            {
                AiDurableInvocationValidation.ValidateRecord(record);
                if (record.Definition.Scope != scope || record.Definition.Target.ExecutionLanguage != language ||
                    AiDurableInvocationValidation.Terminal(record))
                    throw new InvalidOperationException("Worker dispatch store returned a foreign or terminal candidate.");
                if (cursor is not null && (record.UpdatedAtUtc < cursor.UpdatedAtUtc ||
                    record.UpdatedAtUtc == cursor.UpdatedAtUtc && string.CompareOrdinal(record.OperationId, cursor.OperationId) <= 0))
                    throw new InvalidOperationException("Worker dispatch page is not strictly ordered after its cursor.");
                cursor = new(record.UpdatedAtUtc, record.OperationId);
            }
            return result;
        }
    }
}
