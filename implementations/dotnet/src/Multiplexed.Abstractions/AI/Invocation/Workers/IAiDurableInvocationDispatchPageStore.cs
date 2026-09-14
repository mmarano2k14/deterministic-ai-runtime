using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.Abstractions.AI.Invocation.Workers
{
    /// <summary>Keyset cursor, not an acknowledgement or authority to dispatch.</summary>
    public sealed record AiWorkerDispatchCursor(DateTimeOffset UpdatedAtUtc, string OperationId);

    /// <summary>
    /// Optional journal read capability for bounded polling. Ordering is updated-at then
    /// ordinal operation ID; the cursor is exclusive. Leases remain the only write authority.
    /// </summary>
    public interface IAiDurableInvocationDispatchPageStore
    {
        Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchPageAsync(
            AiDurableInvocationScope scope, string executionLanguage, DateTimeOffset nowUtc,
            int maxCount, AiWorkerDispatchCursor? after = null,
            CancellationToken cancellationToken = default);
    }
}
