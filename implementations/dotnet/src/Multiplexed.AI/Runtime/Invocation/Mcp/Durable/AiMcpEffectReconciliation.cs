using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>Outcome returned by an explicit provider/tool reconciliation query.</summary>
    public enum AiMcpEffectReconciliationDisposition
    {
        Unknown,
        Completed,
        NotSent
    }

    /// <summary>
    /// Result of a read/query reconciliation contract. It never grants tools/call or retry
    /// authority. Completed carries the original attempt-correlated normalized response.
    /// </summary>
    public sealed record AiMcpEffectReconciliationResult
    {
        public required AiMcpEffectReconciliationDisposition Disposition { get; init; }
        public JsonElement? Response { get; init; }
        public string? ReasonCode { get; init; }

        public static AiMcpEffectReconciliationResult Completed(JsonElement response) => new()
        {
            Disposition = AiMcpEffectReconciliationDisposition.Completed,
            Response = response.Clone()
        };

        public static AiMcpEffectReconciliationResult NotSent(string reasonCode) => new()
        {
            Disposition = AiMcpEffectReconciliationDisposition.NotSent,
            ReasonCode = reasonCode
        };

        public static AiMcpEffectReconciliationResult Unknown(string reasonCode = "reconciliation-unknown") => new()
        {
            Disposition = AiMcpEffectReconciliationDisposition.Unknown,
            ReasonCode = reasonCode
        };
    }

    /// <summary>
    /// Explicit server-owned reconciliation contract for one bounded provider/tool family.
    /// Implementations must query existing external state and must not invoke the original
    /// business tools/call as part of reconciliation.
    /// </summary>
    public interface IAiMcpEffectReconciliationProvider
    {
        bool CanReconcile(AiMcpEffectIntent intent);

        Task<AiMcpEffectReconciliationResult> ReconcileAsync(
            AiMcpEffectEvidenceRecord evidence,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Applies explicit reconciliation evidence to the durable record. It does not scan,
    /// schedule, retry, re-emit or mutate the owning DAG.
    /// </summary>
    public sealed class AiMcpEffectReconciliationService
    {
        private readonly AiMcpEffectEvidenceJournal _journal;
        private readonly IReadOnlyList<IAiMcpEffectReconciliationProvider> _providers;

        public AiMcpEffectReconciliationService(
            AiMcpEffectEvidenceJournal journal,
            IEnumerable<IAiMcpEffectReconciliationProvider> providers)
        {
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _providers = (providers ?? throw new ArgumentNullException(nameof(providers))).ToArray();
        }

        public async Task<AiMcpEffectEvidenceRecord> ReconcileAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken = default)
        {
            var current = await _journal.GetAsync(scope, effectId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"MCP effect evidence '{effectId}' was not found.");

            if (current.Status is AiMcpEffectEvidenceStatus.Completed or AiMcpEffectEvidenceStatus.NotSent)
                return current;
            if (current.Status == AiMcpEffectEvidenceStatus.Prepared)
                throw new InvalidOperationException("Prepared MCP effect evidence has no physical attempt to reconcile.");

            var provider = ResolveProvider(current.Intent);
            var result = await provider.ReconcileAsync(current, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MCP effect reconciliation provider returned no result.");

            return result.Disposition switch
            {
                AiMcpEffectReconciliationDisposition.Completed =>
                    await ApplyCompletedAsync(current, result, cancellationToken).ConfigureAwait(false),
                AiMcpEffectReconciliationDisposition.NotSent =>
                    await ApplyNotSentAsync(current, result, cancellationToken).ConfigureAwait(false),
                AiMcpEffectReconciliationDisposition.Unknown =>
                    await ApplyUnknownAsync(current, result, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unknown MCP effect reconciliation disposition.")
            };
        }

        private IAiMcpEffectReconciliationProvider ResolveProvider(AiMcpEffectIntent intent)
        {
            var matches = _providers.Where(provider => provider.CanReconcile(intent)).Take(2).ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new NotSupportedException(
                    "No explicit MCP effect reconciliation provider supports this frozen connection/tool intent."),
                _ => throw new InvalidOperationException(
                    "Multiple MCP effect reconciliation providers claim the same frozen connection/tool intent.")
            };
        }

        private async Task<AiMcpEffectEvidenceRecord> ApplyCompletedAsync(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectReconciliationResult result,
            CancellationToken cancellationToken)
        {
            if (result.Response is not { } response)
                throw new InvalidOperationException("Completed MCP reconciliation requires a normalized response.");

            var completed = await _journal.TryCompleteAsync(expected, response, cancellationToken).ConfigureAwait(false);
            if (completed is not null) return completed;
            return await ResolveCasLossAsync(expected, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AiMcpEffectEvidenceRecord> ApplyNotSentAsync(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectReconciliationResult result,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(result.ReasonCode))
                throw new InvalidOperationException("NotSent MCP reconciliation requires a bounded reason code.");

            var notSent = await _journal.TryMarkNotSentAsync(
                expected, result.ReasonCode, cancellationToken).ConfigureAwait(false);
            if (notSent is not null) return notSent;
            return await ResolveCasLossAsync(expected, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AiMcpEffectEvidenceRecord> ApplyUnknownAsync(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectReconciliationResult result,
            CancellationToken cancellationToken)
        {
            if (expected.Status == AiMcpEffectEvidenceStatus.Uncertain) return expected;
            var reason = string.IsNullOrWhiteSpace(result.ReasonCode)
                ? "reconciliation-unknown"
                : result.ReasonCode;
            var uncertain = await _journal.TryMarkUncertainAsync(
                expected, reason, cancellationToken).ConfigureAwait(false);
            if (uncertain is not null) return uncertain;
            return await ResolveCasLossAsync(expected, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AiMcpEffectEvidenceRecord> ResolveCasLossAsync(
            AiMcpEffectEvidenceRecord expected,
            CancellationToken cancellationToken)
        {
            var current = await _journal.GetAsync(
                expected.Scope,
                expected.Intent.Effect.EffectId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("MCP effect evidence disappeared during reconciliation.");

            if (current.Status is AiMcpEffectEvidenceStatus.Completed or AiMcpEffectEvidenceStatus.NotSent)
                return current;

            throw new IOException(
                "MCP effect reconciliation lost its evidence CAS and no terminal outcome is authoritative.");
        }
    }
}
