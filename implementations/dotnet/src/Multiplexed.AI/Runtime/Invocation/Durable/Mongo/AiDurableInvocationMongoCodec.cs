using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    /// <summary>
    /// Stores a versioned JSON snapshot with explicit BSON index projections. Decode
    /// verifies those projections; they cannot silently select a different ownership scope.
    /// </summary>
    internal static class AiDurableInvocationMongoCodec
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        internal static BsonDocument Encode(AiDurableInvocationRecord record)
        {
            AiDurableInvocationValidation.ValidateRecord(record);
            var identity = record.Definition.Identity;
            var scope = record.Definition.Scope;
            var snapshot = JsonSerializer.Serialize(record, Json);
            return new BsonDocument
            {
                { "_id", record.OperationId },
                { "tenantId", identity.TenantId },
                { "executionId", identity.ExecutionId },
                { "stepName", identity.StepName },
                { "generation", identity.Generation },
                { "tenantGroupId", scope.TenantGroupId },
                { "controlPlaneId", scope.ControlPlaneId },
                { "language", record.Definition.Target.ExecutionLanguage },
                { "revision", record.Revision },
                { "status", (int)record.Status },
                { "continuationStatus", (int)record.ContinuationStatus },
                { "leaseExpiresAt", record.Lease is null ? BsonNull.Value : new BsonInt64(record.Lease.ExpiresAtUtc.ToUnixTimeMilliseconds()) },
                { "createdAt", record.CreatedAtUtc.ToUnixTimeMilliseconds() },
                { "updatedAt", record.UpdatedAtUtc.ToUnixTimeMilliseconds() },
                { "snapshotSha256", AiDurableInvocationKeys.HashText(snapshot) },
                { "snapshot", snapshot }
            };
        }

        internal static AiDurableInvocationRecord Decode(BsonDocument document)
        {
            var record = JsonSerializer.Deserialize<AiDurableInvocationRecord>(document["snapshot"].AsString, Json)
                ?? throw new InvalidOperationException("Invocation document contains no snapshot.");
            var expected = Encode(record);
            if (document.ElementCount != expected.ElementCount ||
                expected.Any(item => !document.TryGetValue(item.Name, out var value) || !value.Equals(item.Value)))
                throw new InvalidOperationException("Invocation BSON projections or snapshot integrity do not match.");
            return record;
        }

        internal static BsonDocument ScopeFilter(AiDurableInvocationScope scope) => new()
        {
            { "tenantId", scope.TenantId }, { "tenantGroupId", scope.TenantGroupId }, { "controlPlaneId", scope.ControlPlaneId }
        };

        internal static BsonDocument IdentityFilter(AiDurableInvocationIdentity identity) => new()
        {
            { "_id", AiDurableInvocationKeys.OperationId(identity) },
            { "tenantId", identity.TenantId }, { "executionId", identity.ExecutionId },
            { "stepName", identity.StepName }, { "generation", identity.Generation }
        };

        internal static BsonDocument ReplacementFilter(AiDurableInvocationRecord before, AiDurableInvocationRecord after)
        {
            var filter = IdentityFilter(before.Definition.Identity);
            filter.Add("tenantGroupId", before.Definition.Scope.TenantGroupId);
            filter.Add("controlPlaneId", before.Definition.Scope.ControlPlaneId);
            filter.Add("revision", before.Revision);
            filter.Add("snapshotSha256", Encode(before)["snapshotSha256"]);
            var conditions = new BsonArray();
            var serverNow = new BsonDocument("$toLong", "$$NOW");
            if (before.Status == AiDurableInvocationStatus.Leased)
            {
                var replacesLease = after.Status == AiDurableInvocationStatus.Leased && after.Lease!.Epoch != before.Lease!.Epoch;
                conditions.Add(new BsonDocument(replacesLease ? "$lte" : "$gt", new BsonArray { "$leaseExpiresAt", serverNow }));
            }
            if (after.Status == AiDurableInvocationStatus.Leased)
            {
                var expiry = new BsonInt64(after.Lease!.ExpiresAtUtc.ToUnixTimeMilliseconds());
                conditions.Add(new BsonDocument("$gt", new BsonArray { expiry, serverNow }));
                conditions.Add(new BsonDocument("$lte", new BsonArray
                {
                    expiry, new BsonDocument("$add", new BsonArray { serverNow, 300000L })
                }));
            }
            if (conditions.Count != 0) filter.Add("$expr", new BsonDocument("$and", conditions));
            return filter;
        }
    }
}
