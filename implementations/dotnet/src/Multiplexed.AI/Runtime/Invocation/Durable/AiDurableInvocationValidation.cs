using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    /// <summary>One set of invariants for preparation, snapshots and persisted transitions.</summary>
    internal static class AiDurableInvocationValidation
    {
        internal static void Text(string value, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
            if (value.Length > 512) throw new ArgumentException("Invocation identifiers cannot exceed 512 characters.", name);
            _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value);
        }

        internal static void ValidateIdentity(AiDurableInvocationIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            Text(identity.TenantId, nameof(identity.TenantId));
            Text(identity.ExecutionId, nameof(identity.ExecutionId));
            Text(identity.StepName, nameof(identity.StepName));
            ArgumentOutOfRangeException.ThrowIfNegative(identity.Generation);
        }

        internal static void ValidateScope(AiDurableInvocationScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            Text(scope.TenantId, nameof(scope.TenantId));
            Text(scope.TenantGroupId, nameof(scope.TenantGroupId));
            Text(scope.ControlPlaneId, nameof(scope.ControlPlaneId));
        }

        internal static void ValidateAddress(AiDurableInvocationScope scope, AiDurableInvocationIdentity identity)
        {
            ValidateScope(scope);
            ValidateIdentity(identity);
            Require(scope.TenantId == identity.TenantId, "Invocation tenant and trusted scope do not match.");
        }

        internal static void ValidateLanguage(string language)
        {
            Require(language is "python" or "typescript" or "dotnet", "An effective supported custom language is required.");
        }

        internal static AiDurableInvocationDefinition Freeze(AiDurableInvocationDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ValidateAddress(definition.Scope, definition.Identity);
            var target = definition.Target;
            ArgumentNullException.ThrowIfNull(target);
            Text(target.PipelineName, nameof(target.PipelineName));
            Text(target.PipelineVersion, nameof(target.PipelineVersion));
            Text(target.PublicationRef, nameof(target.PublicationRef));
            Text(target.ImplementationRef, nameof(target.ImplementationRef));
            Text(target.EnvironmentRef, nameof(target.EnvironmentRef));
            ValidateLanguage(target.ExecutionLanguage);
            Hash(target.DefinitionSha256);
            Hash(target.PublicationSha256);
            Hash(target.ImplementationSha256);
            Hash(target.EnvironmentSha256);
            return definition with { InputsJson = AiDurableInvocationJson.Normalize(definition.InputsJson, true) };
        }

        internal static AiDurableInvocationResult Freeze(AiDurableInvocationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            return result with { PayloadJson = AiDurableInvocationJson.Normalize(result.PayloadJson, false) };
        }

        internal static string ResultHash(AiDurableInvocationResult result) =>
            AiDurableInvocationKeys.HashText((result.Success ? "success\n" : "failure\n") + result.PayloadJson);

        internal static void ValidateLease(AiDurableInvocationLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            Text(lease.WorkerId, nameof(lease.WorkerId));
            Text(lease.Token, nameof(lease.Token));
            Require(lease.Epoch > 0, "Lease epoch must be positive.");
            Timestamp(lease.ExpiresAtUtc);
        }

        internal static bool SameAssignment(AiDurableInvocationLease? left, AiDurableInvocationLease? right) =>
            left is not null && right is not null && left.WorkerId == right.WorkerId &&
            left.Epoch == right.Epoch && left.Token == right.Token;

        internal static bool Terminal(AiDurableInvocationRecord record) =>
            record.Status is AiDurableInvocationStatus.Succeeded or AiDurableInvocationStatus.Failed;

        internal static void ValidateRecord(AiDurableInvocationRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            Require(record.SchemaVersion == 1, "Unsupported invocation journal schema.");
            Require(record.Definition == Freeze(record.Definition), "Invocation preparation must already be normalized.");
            Require(record.OperationId == AiDurableInvocationKeys.OperationId(record.Definition.Identity), "Invalid operation identity.");
            Require(record.EffectIdempotencyKey == AiDurableInvocationKeys.EffectIdempotencyKey(record.Definition.Identity), "Invalid effect identity.");
            Require(record.InputsSha256 == AiDurableInvocationKeys.HashText(record.Definition.InputsJson), "Input integrity mismatch.");
            Require(record.Revision >= 0, "Invalid journal revision.");
            Require(Enum.IsDefined(record.Status) && Enum.IsDefined(record.ContinuationStatus), "Unknown journal state.");
            Timestamp(record.CreatedAtUtc);
            Timestamp(record.UpdatedAtUtc);
            Require(record.UpdatedAtUtc >= record.CreatedAtUtc, "Journal timestamps moved backwards.");
            if (record.Lease is not null) ValidateLease(record.Lease);
            if (record.Status == AiDurableInvocationStatus.Prepared)
                Require(record.Lease is null && record.Revision == 0 && record.CreatedAtUtc == record.UpdatedAtUtc, "A prepared record cannot contain an assignment or replacement revision.");
            else
                Require(record.Lease is not null && record.Revision > 0, "An assigned/terminal record must retain its lease fence.");

            if (!Terminal(record))
            {
                Require(record.Result is null && record.ResultSha256 is null && record.CompletedAtUtc is null &&
                    record.ContinuationStatus == AiDurableInvocationContinuationStatus.None && record.ContinuationReason is null,
                    "A nonterminal invocation cannot contain a result or continuation.");
                return;
            }
            Require(record.Result is not null && record.CompletedAtUtc is not null, "Terminal result is incomplete.");
            Require(record.Result == Freeze(record.Result!), "Result must already be normalized.");
            Require(record.ResultSha256 == ResultHash(record.Result!), "Result integrity mismatch.");
            Require((record.Status == AiDurableInvocationStatus.Succeeded) == record.Result!.Success, "Status and result disagree.");
            Timestamp(record.CompletedAtUtc!.Value);
            Require(record.CompletedAtUtc!.Value >= record.CreatedAtUtc && record.CompletedAtUtc.Value <= record.UpdatedAtUtc,
                "Invalid terminal timestamp.");
            Require(record.CompletedAtUtc!.Value < record.Lease!.ExpiresAtUtc, "A result must be accepted before lease expiry.");
            Require(record.ContinuationStatus != AiDurableInvocationContinuationStatus.None, "Terminal result must retain continuation intent.");
            if (record.ContinuationStatus is AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed)
                Text(record.ContinuationReason!, nameof(record.ContinuationReason));
            else
                Require(record.ContinuationReason is null, "Unacknowledged continuation cannot contain an acknowledgement reason.");
        }

        internal static void ValidateTransition(AiDurableInvocationRecord before, AiDurableInvocationRecord after)
        {
            ValidateRecord(before);
            ValidateRecord(after);
            Require(after.Definition == before.Definition && after.CreatedAtUtc == before.CreatedAtUtc &&
                after.OperationId == before.OperationId && after.EffectIdempotencyKey == before.EffectIdempotencyKey &&
                after.InputsSha256 == before.InputsSha256 && after.Revision == checked(before.Revision + 1) &&
                after.UpdatedAtUtc >= before.UpdatedAtUtc, "Invocation replacement changed frozen data or revision order.");

            if (Terminal(before))
            {
                Require(after.Status == before.Status && after.Lease == before.Lease && after.Result == before.Result &&
                    after.ResultSha256 == before.ResultSha256 && after.CompletedAtUtc == before.CompletedAtUtc,
                    "An authoritative terminal result cannot be overwritten.");
                Require(before.ContinuationStatus is AiDurableInvocationContinuationStatus.Pending or AiDurableInvocationContinuationStatus.Scheduled,
                    "Acknowledged continuation is immutable.");
                Require(after.ContinuationStatus is AiDurableInvocationContinuationStatus.Scheduled or
                    AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed ||
                    before.ContinuationStatus == AiDurableInvocationContinuationStatus.Pending &&
                    after.ContinuationStatus == AiDurableInvocationContinuationStatus.Pending,
                    "Invalid continuation transition.");
                return;
            }

            if (Terminal(after))
            {
                Require(before.Status == AiDurableInvocationStatus.Leased && after.Lease == before.Lease &&
                    after.ContinuationStatus == AiDurableInvocationContinuationStatus.Pending &&
                    after.CompletedAtUtc == after.UpdatedAtUtc && before.Lease!.ExpiresAtUtc > after.UpdatedAtUtc,
                    "Only a live assignment can atomically persist its result and pending continuation.");
                return;
            }

            Require(after.Status == AiDurableInvocationStatus.Leased, "An invocation cannot return to Prepared.");
            Require(after.Lease!.ExpiresAtUtc > after.UpdatedAtUtc, "New lease expiry must be in the future.");
            if (before.Status == AiDurableInvocationStatus.Prepared)
                Require(after.Lease.Epoch == 1, "The first lease must use epoch one.");
            else if (SameAssignment(before.Lease, after.Lease))
                Require(before.Lease!.ExpiresAtUtc > after.UpdatedAtUtc && after.Lease.ExpiresAtUtc > before.Lease.ExpiresAtUtc,
                    "Renewal requires a live lease and must extend it.");
            else
                Require(before.Lease!.ExpiresAtUtc <= after.UpdatedAtUtc &&
                    after.Lease.Epoch == checked(before.Lease.Epoch + 1) && after.Lease.Token != before.Lease.Token,
                    "Replacement requires expiry, a new token and the next epoch.");
        }

        internal static DateTimeOffset Milliseconds(DateTimeOffset value) =>
            DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

        private static void Timestamp(DateTimeOffset value) =>
            Require(value.Offset == TimeSpan.Zero && value == Milliseconds(value), "Journal timestamps must be UTC with millisecond precision.");

        private static void Hash(string value)
        {
            Require(value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "Pinned content hashes must be lower-case SHA-256 values.");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
