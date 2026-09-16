using MongoDB.Bson;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo
{
    /// <summary>Explicit BSON codec keeps effect evidence independent of global serializers.</summary>
    internal static class AiMcpEffectEvidenceMongoCodec
    {
        internal static BsonDocument Encode(AiMcpEffectEvidenceRecord record)
        {
            AiMcpEffectEvidenceValidation.ValidateRecord(record);
            var document = new BsonDocument
            {
                { "schemaVersion", record.SchemaVersion },
                { "tenantId", record.Scope.TenantId },
                { "tenantGroupId", record.Scope.TenantGroupId },
                { "effectSchemaVersion", record.Intent.Effect.SchemaVersion },
                { "effectId", record.Intent.Effect.EffectId },
                { "requestDigest", record.Intent.Effect.RequestDigest },
                { "executionId", record.Intent.Context.ExecutionId },
                { "pipelineName", record.Intent.Context.PipelineName },
                { "pipelineVersion", record.Intent.Context.PipelineVersion is null
                    ? BsonNull.Value
                    : new BsonString(record.Intent.Context.PipelineVersion) },
                { "stepName", record.Intent.Context.StepName },
                { "stepKey", record.Intent.Context.StepKey },
                { "connectionRef", record.Intent.ConnectionRef },
                { "connectionRevision", record.Intent.ConnectionRevision },
                { "tool", record.Intent.Tool },
                { "argumentsJson", record.Intent.ArgumentsJson },
                { "revision", record.Revision },
                { "status", (int)record.Status },
                { "createdAt", record.CreatedAtUtc.ToUnixTimeMilliseconds() },
                { "updatedAt", record.UpdatedAtUtc.ToUnixTimeMilliseconds() }
            };

            if (record.Attempt is not null)
            {
                document.Add("attemptRequestId", record.Attempt.RequestId);
                document.Add("attemptStartedAt", record.Attempt.StartedAtUtc.ToUnixTimeMilliseconds());
                document.Add("attemptDeadline", record.Attempt.DeadlineUtc.ToUnixTimeMilliseconds());
            }
            if (record.Result is not null)
            {
                document.Add("resultIsError", record.Result.IsError);
                document.Add("resultJson", record.Result.ResponseJson);
                document.Add("resultSha256", record.Result.ResponseSha256);
                document.Add("resultReceivedAt", record.Result.ReceivedAtUtc.ToUnixTimeMilliseconds());
            }
            if (record.Uncertainty is not null)
            {
                document.Add("uncertaintyReasonCode", record.Uncertainty.ReasonCode);
                document.Add("uncertaintyRecordedAt", record.Uncertainty.RecordedAtUtc.ToUnixTimeMilliseconds());
            }
            return document;
        }

        internal static AiMcpEffectEvidenceRecord Decode(BsonDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            var context = new AiMcpToolInvocationContext(
                document["tenantId"].AsString,
                document["tenantGroupId"].AsString,
                document["executionId"].AsString,
                document["pipelineName"].AsString,
                document["pipelineVersion"].IsBsonNull ? null : document["pipelineVersion"].AsString,
                document["stepName"].AsString,
                document["stepKey"].AsString);
            var intent = new AiMcpEffectIntent(
                new AiMcpEffectIdentity(
                    document["effectSchemaVersion"].AsInt32,
                    document["effectId"].AsString,
                    document["requestDigest"].AsString),
                context,
                document["connectionRef"].AsString,
                document["connectionRevision"].AsString,
                document["tool"].AsString,
                document["argumentsJson"].AsString);

            AiMcpEffectDispatchAttempt? attempt = null;
            if (document.TryGetValue("attemptRequestId", out var attemptRequestId) && !attemptRequestId.IsBsonNull)
            {
                attempt = new AiMcpEffectDispatchAttempt(
                    attemptRequestId.AsString,
                    DateTimeOffset.FromUnixTimeMilliseconds(document["attemptStartedAt"].AsInt64),
                    DateTimeOffset.FromUnixTimeMilliseconds(document["attemptDeadline"].AsInt64));
            }

            AiMcpEffectResultEvidence? result = null;
            if (document.TryGetValue("resultJson", out var resultJson) && !resultJson.IsBsonNull)
            {
                result = new AiMcpEffectResultEvidence(
                    document["resultIsError"].AsBoolean,
                    resultJson.AsString,
                    document["resultSha256"].AsString,
                    DateTimeOffset.FromUnixTimeMilliseconds(document["resultReceivedAt"].AsInt64));
            }

            AiMcpEffectUncertaintyEvidence? uncertainty = null;
            if (document.TryGetValue("uncertaintyReasonCode", out var uncertaintyReason) && !uncertaintyReason.IsBsonNull)
            {
                uncertainty = new AiMcpEffectUncertaintyEvidence(
                    uncertaintyReason.AsString,
                    DateTimeOffset.FromUnixTimeMilliseconds(document["uncertaintyRecordedAt"].AsInt64));
            }

            var record = new AiMcpEffectEvidenceRecord
            {
                SchemaVersion = document["schemaVersion"].AsInt32,
                Scope = new AiMcpEffectEvidenceScope(context.TenantId, context.TenantGroupId),
                Intent = intent,
                Revision = document["revision"].AsInt64,
                Status = (AiMcpEffectEvidenceStatus)document["status"].AsInt32,
                Attempt = attempt,
                Result = result,
                Uncertainty = uncertainty,
                CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(document["createdAt"].AsInt64),
                UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(document["updatedAt"].AsInt64)
            };
            AiMcpEffectEvidenceValidation.ValidateRecord(record);
            return record;
        }

        internal static BsonDocument IdentityFilter(AiMcpEffectEvidenceScope scope, string effectId) => new()
        {
            { "tenantId", scope.TenantId },
            { "tenantGroupId", scope.TenantGroupId },
            { "effectId", effectId }
        };

        internal static BsonDocument ReplacementFilter(AiMcpEffectEvidenceRecord expected) => new()
        {
            { "tenantId", expected.Scope.TenantId },
            { "tenantGroupId", expected.Scope.TenantGroupId },
            { "effectId", expected.Intent.Effect.EffectId },
            { "requestDigest", expected.Intent.Effect.RequestDigest },
            { "revision", expected.Revision },
            { "status", (int)expected.Status }
        };
    }
}
