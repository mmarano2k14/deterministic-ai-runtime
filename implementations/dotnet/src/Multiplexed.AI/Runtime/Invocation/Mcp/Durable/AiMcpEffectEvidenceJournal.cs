using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>
    /// Trusted server facade for durable outbound MCP evidence. This initial integration
    /// prepares and reads immutable intent only; network dispatch remains unchanged until
    /// a later coordinator explicitly consumes the CAS lifecycle.
    /// </summary>
    public sealed class AiMcpEffectEvidenceJournal
    {
        private readonly IAiMcpEffectEvidenceStore _store;
        private readonly TimeProvider _timeProvider;

        public AiMcpEffectEvidenceJournal(
            IAiMcpEffectEvidenceStore store,
            TimeProvider? timeProvider = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Persists the exact logical intent represented by schema-2 effect metadata.
        /// RequestId and deadline are physical-attempt fields and are not frozen here.
        /// </summary>
        public Task<AiMcpEffectEvidenceRecord> PrepareAsync(
            AiMcpToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            AiMcpEffectIdentities.ValidateRequest(request);
            if (request.SchemaVersion != AiMcpEffectIdentities.RequestSchemaVersion || request.Effect is null)
                throw new InvalidOperationException("Durable MCP effect evidence requires the versioned effect envelope.");

            var arguments = AiMcpToolJson.CopyResponse(request.Arguments);
            var intent = new AiMcpEffectIntent(
                request.Effect,
                request.Context,
                request.ConnectionRef,
                request.ConnectionRevision,
                request.Tool,
                arguments.GetRawText());
            var now = _timeProvider.GetUtcNow();
            var prepared = new AiMcpEffectEvidenceRecord
            {
                Scope = new AiMcpEffectEvidenceScope(request.Context.TenantId, request.Context.TenantGroupId),
                Intent = intent,
                Revision = 0,
                Status = AiMcpEffectEvidenceStatus.Prepared,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            AiMcpEffectEvidenceValidation.ValidateRecord(prepared);
            return _store.GetOrCreateAsync(prepared, cancellationToken);
        }

        public Task<AiMcpEffectEvidenceRecord?> GetAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateAddress(scope, effectId);
            return _store.GetAsync(scope, effectId, cancellationToken);
        }
    }
}
