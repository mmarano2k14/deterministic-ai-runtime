using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>
    /// Trusted server facade for durable outbound MCP evidence. It owns only the effect
    /// evidence lifecycle and never becomes a DAG, retry or recovery authority.
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

        /// <summary>
        /// Attempts the only transition that grants one physical outbound attempt the
        /// right to cross the network boundary. A lost CAS grants no dispatch authority.
        /// </summary>
        public async Task<AiMcpEffectEvidenceRecord?> TryBeginDispatchAsync(
            AiMcpEffectEvidenceRecord prepared,
            AiMcpToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(prepared);
            ArgumentNullException.ThrowIfNull(request);
            AiMcpEffectIdentities.ValidateRequest(request);
            AiMcpEffectEvidenceValidation.ValidateRecord(prepared);
            if (prepared.Status != AiMcpEffectEvidenceStatus.Prepared)
                throw new InvalidOperationException("Only Prepared MCP effect evidence can begin dispatch.");
            EnsureRequestMatches(prepared, request);

            var now = _timeProvider.GetUtcNow();
            if (request.DeadlineUtc <= now)
                throw new TimeoutException("MCP request deadline expired before durable dispatch authority was acquired.");

            var dispatching = prepared with
            {
                Revision = checked(prepared.Revision + 1),
                Status = AiMcpEffectEvidenceStatus.Dispatching,
                Attempt = new AiMcpEffectDispatchAttempt(request.RequestId, now, request.DeadlineUtc),
                UpdatedAtUtc = now
            };
            AiMcpEffectEvidenceValidation.ValidateTransition(prepared, dispatching);
            return await _store.TryReplaceAsync(prepared, dispatching, cancellationToken).ConfigureAwait(false)
                ? dispatching
                : null;
        }

        /// <summary>
        /// Persists one confirmed normalized response before it can be returned to the DAG.
        /// Reconciliation may complete either Dispatching or Uncertain evidence for the
        /// same immutable physical attempt.
        /// </summary>
        public async Task<AiMcpEffectEvidenceRecord?> TryCompleteAsync(
            AiMcpEffectEvidenceRecord current,
            JsonElement response,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(current);
            AiMcpEffectEvidenceValidation.ValidateRecord(current);
            if (current.Status is not (AiMcpEffectEvidenceStatus.Dispatching or AiMcpEffectEvidenceStatus.Uncertain) ||
                current.Attempt is null)
            {
                throw new InvalidOperationException(
                    "Only Dispatching or Uncertain MCP effect evidence can accept a confirmed transport result.");
            }

            var detached = AiMcpToolJson.CopyResponse(response);
            if (detached.ValueKind != JsonValueKind.Object ||
                !detached.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1 ||
                !detached.TryGetProperty("requestId", out var requestId) ||
                requestId.ValueKind != JsonValueKind.String ||
                !string.Equals(requestId.GetString(), current.Attempt.RequestId, StringComparison.Ordinal) ||
                !detached.TryGetProperty("isError", out var isError) ||
                isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !detached.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array ||
                detached.TryGetProperty("structuredContent", out var structuredContent) &&
                structuredContent.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("MCP transport returned an invalid normalized response for durable completion.");
            }

            var responseJson = detached.GetRawText();
            var now = _timeProvider.GetUtcNow();
            var completed = current with
            {
                Revision = checked(current.Revision + 1),
                Status = AiMcpEffectEvidenceStatus.Completed,
                Result = new AiMcpEffectResultEvidence(
                    isError.GetBoolean(),
                    responseJson,
                    AiMcpEffectEvidenceValidation.ResponseSha256(responseJson),
                    now),
                Uncertainty = null,
                NonEmission = null,
                UpdatedAtUtc = now
            };
            AiMcpEffectEvidenceValidation.ValidateTransition(current, completed);
            return await _store.TryReplaceAsync(current, completed, cancellationToken).ConfigureAwait(false)
                ? completed
                : null;
        }

        /// <summary>
        /// Records that a durably fenced attempt has no confirmed result. This transition
        /// never grants a retry or another outbound emission.
        /// </summary>
        public async Task<AiMcpEffectEvidenceRecord?> TryMarkUncertainAsync(
            AiMcpEffectEvidenceRecord dispatching,
            string reasonCode,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dispatching);
            AiMcpEffectEvidenceValidation.ValidateRecord(dispatching);
            if (dispatching.Status != AiMcpEffectEvidenceStatus.Dispatching)
                throw new InvalidOperationException("Only Dispatching MCP effect evidence can become uncertain.");
            ValidateReasonCode(reasonCode);

            var now = _timeProvider.GetUtcNow();
            var uncertain = dispatching with
            {
                Revision = checked(dispatching.Revision + 1),
                Status = AiMcpEffectEvidenceStatus.Uncertain,
                Uncertainty = new AiMcpEffectUncertaintyEvidence(reasonCode, now),
                UpdatedAtUtc = now
            };
            AiMcpEffectEvidenceValidation.ValidateTransition(dispatching, uncertain);
            return await _store.TryReplaceAsync(dispatching, uncertain, cancellationToken).ConfigureAwait(false)
                ? uncertain
                : null;
        }

        /// <summary>
        /// Records authoritative evidence that the selected tools/call attempt did not
        /// cross the business-effect boundary. This state does not schedule a redelivery.
        /// </summary>
        public async Task<AiMcpEffectEvidenceRecord?> TryMarkNotSentAsync(
            AiMcpEffectEvidenceRecord current,
            string reasonCode,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(current);
            AiMcpEffectEvidenceValidation.ValidateRecord(current);
            if (current.Status is not (AiMcpEffectEvidenceStatus.Dispatching or AiMcpEffectEvidenceStatus.Uncertain))
                throw new InvalidOperationException("Only Dispatching or Uncertain MCP effect evidence can become NotSent.");
            ValidateReasonCode(reasonCode);

            var now = _timeProvider.GetUtcNow();
            var notSent = current with
            {
                Revision = checked(current.Revision + 1),
                Status = AiMcpEffectEvidenceStatus.NotSent,
                Uncertainty = null,
                NonEmission = new AiMcpEffectNonEmissionEvidence(reasonCode, now),
                UpdatedAtUtc = now
            };
            AiMcpEffectEvidenceValidation.ValidateTransition(current, notSent);
            return await _store.TryReplaceAsync(current, notSent, cancellationToken).ConfigureAwait(false)
                ? notSent
                : null;
        }

        public Task<AiMcpEffectEvidenceRecord?> GetAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateAddress(scope, effectId);
            return _store.GetAsync(scope, effectId, cancellationToken);
        }

        private static void EnsureRequestMatches(
            AiMcpEffectEvidenceRecord record,
            AiMcpToolRequest request)
        {
            if (request.Effect is null ||
                record.Scope.TenantId != request.Context.TenantId ||
                record.Scope.TenantGroupId != request.Context.TenantGroupId ||
                record.Intent.Effect != request.Effect ||
                record.Intent.Context != request.Context ||
                !string.Equals(record.Intent.ConnectionRef, request.ConnectionRef, StringComparison.Ordinal) ||
                !string.Equals(record.Intent.ConnectionRevision, request.ConnectionRevision, StringComparison.Ordinal) ||
                !string.Equals(record.Intent.Tool, request.Tool, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MCP request does not match the prepared durable effect intent.");
            }
        }

        private static void ValidateReasonCode(string reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128 ||
                reasonCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            {
                throw new ArgumentException("A bounded MCP effect reason code is required.", nameof(reasonCode));
            }
        }
    }
}
