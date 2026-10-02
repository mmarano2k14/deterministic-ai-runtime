using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>
    /// Durable fence around one existing MCP transport. It grants at most one outbound
    /// attempt for a logical effect, replays confirmed evidence locally, and never turns
    /// unresolved evidence into blind re-emission authority.
    /// </summary>
    public sealed class AiDurableMcpToolTransport : IAiMcpToolTransport
    {
        private const int MaxCasReloads = 8;
        private readonly AiMcpEffectEvidenceJournal _journal;
        private readonly IAiMcpToolTransport _inner;

        public AiDurableMcpToolTransport(
            AiMcpEffectEvidenceJournal journal,
            IAiMcpToolTransport inner)
        {
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (ReferenceEquals(inner, this))
                throw new InvalidOperationException("A durable MCP transport cannot wrap itself.");
        }

        public async Task<JsonElement> InvokeAsync(
            AiMcpToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var current = await _journal.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            var scope = current.Scope;
            var effectId = current.Intent.Effect.EffectId;

            for (var attempt = 0; attempt < MaxCasReloads; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (current.Status)
                {
                    case AiMcpEffectEvidenceStatus.Completed:
                        return ReplayCompleted(current, request.RequestId);

                    case AiMcpEffectEvidenceStatus.Dispatching:
                    case AiMcpEffectEvidenceStatus.Uncertain:
                        throw new InvalidOperationException(
                            $"MCP effect '{effectId}' is {current.Status}; automatic outbound re-emission is forbidden.");

                    case AiMcpEffectEvidenceStatus.NotSent:
                        throw new InvalidOperationException(
                            $"MCP effect '{effectId}' is confirmed NotSent; this evidence does not grant automatic redelivery authority.");

                    case AiMcpEffectEvidenceStatus.Prepared:
                        var dispatching = await _journal.TryBeginDispatchAsync(
                            current, request, cancellationToken).ConfigureAwait(false);
                        if (dispatching is null)
                        {
                            current = await _journal.GetAsync(scope, effectId, cancellationToken).ConfigureAwait(false)
                                ?? throw new IOException("MCP effect evidence disappeared after a dispatch CAS loss.");
                            continue;
                        }

                        return await InvokeFencedAsync(dispatching, request, cancellationToken).ConfigureAwait(false);

                    default:
                        throw new InvalidOperationException("Unknown MCP effect evidence status.");
                }
            }

            throw new IOException("MCP effect dispatch authority did not converge after repeated evidence CAS races.");
        }

        private async Task<JsonElement> InvokeFencedAsync(
            AiMcpEffectEvidenceRecord dispatching,
            AiMcpToolRequest request,
            CancellationToken cancellationToken)
        {
            var boundary = new DispatchBoundary();
            JsonElement response;
            try
            {
                if (_inner is IAiMcpDispatchBoundaryAwareTransport classified)
                {
                    response = await classified.InvokeAsync(request, boundary, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    boundary.MarkPossiblySent();
                    response = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (boundary.PossiblySent)
                {
                    await TryRecordUncertaintyAsync(dispatching, Reason(exception)).ConfigureAwait(false);
                }
                else
                {
                    await TryRecordNotSentAsync(dispatching, Reason(exception)).ConfigureAwait(false);
                }
                throw;
            }

            AiMcpEffectEvidenceRecord? completed;
            try
            {
                completed = await _journal.TryCompleteAsync(dispatching, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    "MCP remote result was received but durable completion evidence could not be confirmed.",
                    exception);
            }

            if (completed is not null) return response;

            var current = await _journal.GetAsync(
                dispatching.Scope,
                dispatching.Intent.Effect.EffectId,
                cancellationToken).ConfigureAwait(false);
            if (current?.Status == AiMcpEffectEvidenceStatus.Completed &&
                current.Attempt == dispatching.Attempt &&
                current.Result is not null &&
                SameResult(current.Result, response))
            {
                return response;
            }

            throw new IOException(
                "MCP remote result was received but the durable completion CAS was not authoritative.");
        }

        private async Task TryRecordUncertaintyAsync(
            AiMcpEffectEvidenceRecord dispatching,
            string reasonCode)
        {
            try
            {
                // Once Dispatching is durable, caller/deadline cancellation cannot be allowed
                // to cancel the safety transition that fences future physical re-emission.
                _ = await _journal.TryMarkUncertainAsync(
                    dispatching, reasonCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Never replace the original transport/protocol exception. A failed or
                // ambiguous evidence update leaves Dispatching durable and fail-closed.
            }
        }

        private async Task TryRecordNotSentAsync(
            AiMcpEffectEvidenceRecord dispatching,
            string reasonCode)
        {
            try
            {
                // Dispatch authority was already persisted. Finalizing the evidence must not
                // be skipped merely because the request token is now cancelled.
                _ = await _journal.TryMarkNotSentAsync(
                    dispatching, reasonCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The original failure remains authoritative to the caller. If the
                // no-emission evidence cannot be persisted, Dispatching stays fail-closed.
            }
        }

        private static JsonElement ReplayCompleted(
            AiMcpEffectEvidenceRecord completed,
            string requestId)
        {
            if (completed.Status != AiMcpEffectEvidenceStatus.Completed || completed.Result is null)
                throw new InvalidOperationException("Only completed MCP effect evidence can be replayed.");
            if (string.IsNullOrWhiteSpace(requestId))
                throw new InvalidOperationException("MCP replay requires the current physical request id.");

            using var source = JsonDocument.Parse(completed.Result.ResponseJson);
            var root = source.RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", root.GetProperty("schemaVersion").GetInt32());
                writer.WriteString("requestId", requestId);
                writer.WriteBoolean("isError", root.GetProperty("isError").GetBoolean());
                writer.WritePropertyName("content");
                root.GetProperty("content").WriteTo(writer);
                if (root.TryGetProperty("structuredContent", out var structuredContent))
                {
                    writer.WritePropertyName("structuredContent");
                    structuredContent.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            using var replay = JsonDocument.Parse(stream.ToArray());
            return replay.RootElement.Clone();
        }

        private static bool SameResult(AiMcpEffectResultEvidence evidence, JsonElement response)
        {
            try
            {
                var detached = AiMcpToolJson.CopyResponse(response);
                return string.Equals(
                    evidence.ResponseSha256,
                    AiMcpEffectEvidenceValidation.ResponseSha256(detached.GetRawText()),
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string Reason(Exception exception) => exception switch
        {
            OperationCanceledException => "transport-cancelled",
            TimeoutException => "transport-timeout",
            _ => "transport-failure"
        };

        private sealed class DispatchBoundary : IAiMcpDispatchBoundary
        {
            private int _possiblySent;
            internal bool PossiblySent => Volatile.Read(ref _possiblySent) != 0;
            public void MarkPossiblySent() => Interlocked.Exchange(ref _possiblySent, 1);
        }
    }
}
