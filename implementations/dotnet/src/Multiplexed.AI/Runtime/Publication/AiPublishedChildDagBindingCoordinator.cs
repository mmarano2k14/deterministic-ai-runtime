using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Binds an allocated Child DAG execution to the exact immutable publication already selected by its parent.
    /// The Child DAG relation remains authoritative for lifecycle and execution identity.
    /// </summary>
    public sealed class AiPublishedChildDagBindingCoordinator
    {
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _store;
        private readonly AiImmutableJsonPayloadReader _payloadReader;

        public AiPublishedChildDagBindingCoordinator(
            AiPublicationIdentity identity,
            AiPublicationOptions options,
            AiImmutablePublicationStore store,
            AiImmutableJsonPayloadReader payloadReader)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _payloadReader = payloadReader ?? throw new ArgumentNullException(nameof(payloadReader));
        }

        public async Task<bool> BindBeforeDispatchAsync(
            AiDurableInvocationScope scope,
            AiExecutionRecord parentRecord,
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parentRecord);
            ArgumentNullException.ThrowIfNull(relation);
            AiDurableInvocationValidation.ValidateScope(scope);
            if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
                throw new InvalidOperationException("Published Child DAG binding requires an allocated child execution identifier.");
            if (!string.Equals(parentRecord.ExecutionId, relation.ParentExecutionId, StringComparison.Ordinal))
                throw new InvalidOperationException("Published Child DAG binding requires the authoritative parent execution.");

            // Probe only the already-restored durable owner first. Native/unpublished Child DAGs must not
            // acquire a publication RBAC requirement merely because publication services are registered.
            var probeGuard = await _identity.CaptureGuardAsync(scope, cancellationToken).ConfigureAwait(false);
            RequireParentOwner(parentRecord, probeGuard, relation);
            var probeBinding = await _store.ReadExecutionBindingAsync(parentRecord.ExecutionId, probeGuard, cancellationToken)
                .ConfigureAwait(false);
            if (probeBinding is null)
                return false;

            // Published execution paths retain the existing publication execute authorization. Re-read the
            // immutable association under the authorized guard before using any publication material.
            var guard = await _identity.AuthorizeAsync(scope, _options.Execute, cancellationToken).ConfigureAwait(false);
            RequireParentOwner(parentRecord, guard, relation);
            var parentBinding = await _store.ReadExecutionBindingAsync(parentRecord.ExecutionId, guard, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Published parent execution lost its immutable publication binding.");

            var parentDefinition = parentRecord.PipelineDefinitionSnapshot
                ?? throw new InvalidOperationException("A published parent execution must retain its immutable pipeline definition snapshot.");
            if (!string.Equals(parentDefinition.ContentHash, parentBinding.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Published parent execution definition differs from its immutable publication binding.");
            await _payloadReader.LoadAndVerifyAsync(parentDefinition, cancellationToken).ConfigureAwait(false);

            var childPath = AiPublicationDefinitionPath.Append(parentBinding.DefinitionPath, relation.ParentCallSiteId);
            var frozen = await _store.ReadVerifiedAsync(parentBinding.PublicationRef, guard, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(frozen.Publication.PublicationSha256, parentBinding.PublicationSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Published parent execution no longer matches its immutable publication digest.");

            var hasPublishedDescendant = frozen.Publication.Manifest.Functions.Any(function =>
                AiPublicationDefinitionPath.IsSameOrDescendant(function.Site.DefinitionPath, childPath));
            if (!hasPublishedDescendant)
                return false;

            var childDefinition = AiPublicationDefinitionPath.Resolve(frozen.Definition, childPath);
            if (!string.Equals(childDefinition.Name, relation.ChildDagId, StringComparison.Ordinal) ||
                !string.Equals(childDefinition.Version, relation.ChildDagDefinitionVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Published Child DAG identity/version differs from the authoritative child relation.");

            var childDefinitionJson = AiPublicationJson.Serialize(childDefinition);
            var childDefinitionSha256 = AiPublicationJson.Hash(childDefinitionJson);
            if (string.IsNullOrWhiteSpace(relation.FrozenChildDagDefinition.ContentHash) ||
                !string.Equals(relation.FrozenChildDagDefinition.ContentHash, childDefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Frozen Child DAG definition differs from the immutable published child definition.");
            var frozenChildDefinitionJson = await _payloadReader
                .LoadAndVerifyAsync(relation.FrozenChildDagDefinition, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(frozenChildDefinitionJson, childDefinitionJson, StringComparison.Ordinal))
                throw new InvalidOperationException("Frozen Child DAG definition content differs from the immutable published child definition.");

            var binding = new AiPublicationChildRunBinding(
                1,
                guard.Partition,
                relation.ChildExecutionId,
                relation.ParentExecutionId,
                guard.UserId,
                parentBinding.PublicationRef,
                parentBinding.PublicationSha256,
                childPath,
                childDefinitionSha256,
                childDefinition.Name,
                childDefinition.Version!);

            await _store.SaveChildBindingAsync(binding, guard, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static void RequireParentOwner(
            AiExecutionRecord parentRecord,
            AiPublicationIdentity.Guard guard,
            AiChildExecutionRelation relation)
        {
            var owner = parentRecord.ExecutionContextSnapshot
                ?? throw new InvalidOperationException("Published Child DAG binding requires the durable parent execution owner.");
            if (!string.Equals(owner.TenantId, relation.TenantId, StringComparison.Ordinal) ||
                !string.Equals(owner.TenantId, guard.Partition.Scope.TenantId, StringComparison.Ordinal) ||
                !string.Equals(owner.TenantGroupId, guard.Partition.Scope.TenantGroupId, StringComparison.Ordinal) ||
                !string.Equals(owner.Project, guard.Partition.Project, StringComparison.Ordinal) ||
                !string.Equals(owner.CurrentNamespace, guard.Partition.Namespace, StringComparison.Ordinal) ||
                !string.Equals(owner.UserId, guard.UserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Published Child DAG binding does not match the durable parent execution owner.");

            var delegated = relation.DelegatedExecutionContextSnapshot
                ?? throw new InvalidOperationException("Published Child DAG binding requires the durable delegated child execution owner.");
            if (!string.Equals(delegated.TenantId, owner.TenantId, StringComparison.Ordinal) ||
                !string.Equals(delegated.TenantGroupId, owner.TenantGroupId, StringComparison.Ordinal) ||
                !string.Equals(delegated.Project, owner.Project, StringComparison.Ordinal) ||
                !string.Equals(delegated.CurrentNamespace, owner.CurrentNamespace, StringComparison.Ordinal) ||
                !string.Equals(delegated.UserId, owner.UserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Published Child DAG delegated execution owner differs from its parent publication owner.");
        }
    }
}
