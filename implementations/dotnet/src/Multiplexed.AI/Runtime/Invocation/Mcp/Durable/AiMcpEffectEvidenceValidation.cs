using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>Central validation for durable MCP intent and evidence transitions.</summary>
    internal static class AiMcpEffectEvidenceValidation
    {
        internal const int RecordSchemaVersion = 1;

        internal static void ValidateAddress(AiMcpEffectEvidenceScope scope, string effectId)
        {
            ValidateScope(scope);
            RequireReference(effectId, nameof(effectId), 192);
        }

        internal static void ValidateScope(AiMcpEffectEvidenceScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            RequireIdentifier(scope.TenantId, nameof(scope.TenantId));
            RequireIdentifier(scope.TenantGroupId, nameof(scope.TenantGroupId));
        }

        internal static void ValidateRecord(AiMcpEffectEvidenceRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            Require(record.SchemaVersion == RecordSchemaVersion, "Unsupported MCP effect evidence schema version.");
            ValidateScope(record.Scope);
            ValidateIntent(record.Intent);
            Require(record.Scope.TenantId == record.Intent.Context.TenantId &&
                record.Scope.TenantGroupId == record.Intent.Context.TenantGroupId,
                "MCP effect evidence scope does not match the frozen intent.");
            Require(record.Revision >= 0, "MCP effect evidence revision cannot be negative.");
            Require(record.CreatedAtUtc != default && record.UpdatedAtUtc >= record.CreatedAtUtc,
                "MCP effect evidence timestamps are invalid.");

            switch (record.Status)
            {
                case AiMcpEffectEvidenceStatus.Prepared:
                    Require(record.Revision == 0, "Prepared MCP effect evidence must start at revision zero.");
                    Require(record.Attempt is null && record.Result is null && record.Uncertainty is null,
                        "Prepared MCP effect evidence cannot contain dispatch or outcome evidence.");
                    break;

                case AiMcpEffectEvidenceStatus.Dispatching:
                    ValidateAttempt(record.Attempt, record.CreatedAtUtc);
                    Require(record.Result is null && record.Uncertainty is null,
                        "Dispatching MCP effect evidence cannot already contain an outcome.");
                    break;

                case AiMcpEffectEvidenceStatus.Completed:
                    ValidateAttempt(record.Attempt, record.CreatedAtUtc);
                    ValidateResult(record.Result, record.Attempt!);
                    Require(record.Uncertainty is null,
                        "Completed MCP effect evidence cannot retain uncertainty evidence.");
                    break;

                case AiMcpEffectEvidenceStatus.Uncertain:
                    ValidateAttempt(record.Attempt, record.CreatedAtUtc);
                    ValidateUncertainty(record.Uncertainty, record.Attempt!.StartedAtUtc);
                    Require(record.Result is null,
                        "Uncertain MCP effect evidence cannot contain a confirmed result.");
                    break;

                default:
                    throw new InvalidOperationException("Unknown MCP effect evidence status.");
            }
        }

        internal static void ValidateTransition(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectEvidenceRecord replacement)
        {
            ValidateRecord(expected);
            ValidateRecord(replacement);
            Require(expected.SchemaVersion == replacement.SchemaVersion &&
                expected.Scope == replacement.Scope &&
                expected.Intent == replacement.Intent &&
                expected.CreatedAtUtc == replacement.CreatedAtUtc,
                "MCP effect immutable evidence changed during a transition.");
            Require(replacement.Revision == checked(expected.Revision + 1),
                "MCP effect evidence revision must increment exactly once.");
            Require(replacement.UpdatedAtUtc >= expected.UpdatedAtUtc,
                "MCP effect evidence time cannot move backwards.");

            var legal = (expected.Status, replacement.Status) switch
            {
                (AiMcpEffectEvidenceStatus.Prepared, AiMcpEffectEvidenceStatus.Dispatching) => true,
                (AiMcpEffectEvidenceStatus.Dispatching, AiMcpEffectEvidenceStatus.Completed) => true,
                (AiMcpEffectEvidenceStatus.Dispatching, AiMcpEffectEvidenceStatus.Uncertain) => true,
                (AiMcpEffectEvidenceStatus.Uncertain, AiMcpEffectEvidenceStatus.Completed) => true,
                _ => false
            };
            Require(legal, $"Illegal MCP effect evidence transition {expected.Status} -> {replacement.Status}.");

            if (expected.Attempt is not null)
            {
                Require(expected.Attempt == replacement.Attempt,
                    "The physical MCP effect attempt is immutable after dispatch begins.");
            }
        }

        internal static string ResponseSha256(string responseJson)
        {
            using var _ = ValidateResponseJson(responseJson);
            return HashResponseJson(responseJson);
        }

        private static string HashResponseJson(string responseJson) =>
            "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(responseJson))).ToLowerInvariant();

        private static void ValidateIntent(AiMcpEffectIntent intent)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(intent.Effect);
            ArgumentNullException.ThrowIfNull(intent.Context);
            RequireReference(intent.Effect.EffectId, nameof(intent.Effect.EffectId), 192);
            RequireDigest(intent.Effect.RequestDigest, nameof(intent.Effect.RequestDigest));
            RequireIdentifier(intent.Context.TenantId, nameof(intent.Context.TenantId));
            RequireIdentifier(intent.Context.TenantGroupId, nameof(intent.Context.TenantGroupId));
            RequireIdentifier(intent.Context.ExecutionId, nameof(intent.Context.ExecutionId));
            RequireIdentifier(intent.Context.PipelineName, nameof(intent.Context.PipelineName));
            if (intent.Context.PipelineVersion is not null)
                RequireIdentifier(intent.Context.PipelineVersion, nameof(intent.Context.PipelineVersion));
            RequireIdentifier(intent.Context.StepName, nameof(intent.Context.StepName));
            RequireIdentifier(intent.Context.StepKey, nameof(intent.Context.StepKey));
            RequireIdentifier(intent.ConnectionRef, nameof(intent.ConnectionRef));
            RequireIdentifier(intent.ConnectionRevision, nameof(intent.ConnectionRevision));
            RequireIdentifier(intent.Tool, nameof(intent.Tool));

            using var document = ParseBoundedJson(intent.ArgumentsJson, "MCP effect arguments");
            Require(document.RootElement.ValueKind == JsonValueKind.Object,
                "MCP effect arguments must be a JSON object.");
            var arguments = AiMcpToolJson.CopyResponse(document.RootElement);
            var probe = new AiMcpToolRequest(
                AiMcpEffectIdentities.RequestSchemaVersion,
                "evidence-validation",
                DateTimeOffset.UnixEpoch,
                intent.Context,
                intent.ConnectionRef,
                intent.ConnectionRevision,
                intent.Tool,
                arguments)
            { Effect = intent.Effect };
            Require(AiMcpEffectIdentities.Create(probe) == intent.Effect,
                "MCP effect durable intent does not match its effect identity or request digest.");
        }

        private static void ValidateAttempt(AiMcpEffectDispatchAttempt? attempt, DateTimeOffset createdAtUtc)
        {
            Require(attempt is not null, "Dispatched MCP effect evidence requires an attempt.");
            RequireReference(attempt!.RequestId, nameof(attempt.RequestId), 128);
            Require(attempt.StartedAtUtc >= createdAtUtc,
                "MCP effect dispatch cannot predate durable intent preparation.");
            Require(attempt.DeadlineUtc > attempt.StartedAtUtc,
                "MCP effect dispatch deadline must be later than its start time.");
        }

        private static void ValidateResult(AiMcpEffectResultEvidence? result, AiMcpEffectDispatchAttempt attempt)
        {
            Require(result is not null, "Completed MCP effect evidence requires a confirmed result.");
            Require(result!.ReceivedAtUtc >= attempt.StartedAtUtc,
                "MCP effect result cannot predate dispatch.");
            RequireDigest(result.ResponseSha256, nameof(result.ResponseSha256));
            using var response = ValidateResponseJson(result.ResponseJson);
            var root = response.RootElement;
            Require(root.TryGetProperty("schemaVersion", out var schemaVersion) &&
                schemaVersion.ValueKind == JsonValueKind.Number && schemaVersion.GetInt32() == 1,
                "MCP effect response evidence has an invalid schema version.");
            Require(root.TryGetProperty("requestId", out var requestId) &&
                requestId.ValueKind == JsonValueKind.String &&
                string.Equals(requestId.GetString(), attempt.RequestId, StringComparison.Ordinal),
                "MCP effect response evidence does not match the dispatched request id.");
            Require(root.TryGetProperty("isError", out var isError) &&
                (isError.ValueKind == JsonValueKind.True || isError.ValueKind == JsonValueKind.False) &&
                isError.GetBoolean() == result.IsError,
                "MCP effect response error flag does not match the stored result evidence.");
            Require(result.ResponseSha256 == HashResponseJson(result.ResponseJson),
                "MCP effect result digest does not match the stored response.");
        }

        private static void ValidateUncertainty(
            AiMcpEffectUncertaintyEvidence? uncertainty,
            DateTimeOffset startedAtUtc)
        {
            Require(uncertainty is not null, "Uncertain MCP effect evidence requires a reason.");
            RequireSegment(uncertainty!.ReasonCode, nameof(uncertainty.ReasonCode), 128);
            Require(uncertainty.RecordedAtUtc >= startedAtUtc,
                "MCP effect uncertainty cannot predate dispatch.");
        }

        private static JsonDocument ValidateResponseJson(string responseJson)
        {
            var document = ParseBoundedJson(responseJson, "MCP effect response");
            try
            {
                Require(document.RootElement.ValueKind == JsonValueKind.Object,
                    "MCP effect response evidence must be a JSON object.");
                _ = AiMcpToolJson.CopyResponse(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        private static JsonDocument ParseBoundedJson(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 65536)
                throw new InvalidOperationException($"{name} is missing or exceeds 65536 UTF-8 bytes.");
            try
            {
                return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 33 });
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"{name} is not valid bounded JSON.", exception);
            }
        }

        internal static bool EquivalentIntent(AiMcpEffectIntent left, AiMcpEffectIntent right) =>
            left.Effect == right.Effect &&
            left.Context == right.Context &&
            string.Equals(left.ConnectionRef, right.ConnectionRef, StringComparison.Ordinal) &&
            string.Equals(left.ConnectionRevision, right.ConnectionRevision, StringComparison.Ordinal) &&
            string.Equals(left.Tool, right.Tool, StringComparison.Ordinal);

        private static void RequireDigest(string value, string name)
        {
            if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
                value.Skip(7).Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            {
                throw new InvalidOperationException($"Invalid {name}.");
            }
        }

        private static void RequireIdentifier(string? value, string name)
        {
            if (value is null || value.Length == 0 || value.Length > 1024 || value.Any(char.IsControl))
                throw new InvalidOperationException($"Invalid {name}.");
        }

        private static void RequireSegment(string? value, string name, int maxLength)
        {
            if (value is null || value.Length == 0 || value.Length > maxLength ||
                value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            {
                throw new InvalidOperationException($"Invalid {name}.");
            }
        }

        private static void RequireReference(string? value, string name, int maxLength)
        {
            if (value is null || value.Length == 0 || value.Length > maxLength || value.Any(char.IsControl))
                throw new InvalidOperationException($"Invalid {name}.");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
