using System.Text;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots
{
    /// <summary>
    /// Freezes child DAG definitions and invocation inputs into immutable replay-safe payload snapshots.
    /// </summary>
    /// <remarks>
    /// Small snapshots remain inline. Larger snapshots are written through the existing payload-store
    /// infrastructure using a content-addressed immutable key before any parent-child relation may reference them.
    /// </remarks>
    public sealed class AiChildDagSnapshotService
    {
        private const string JsonContentType = "application/json";
        private const string DefinitionPayloadKind = "child-dag-definition";
        private const string InvocationInputPayloadKind = "child-dag-invocation-input";
        private const string DelegationPolicyBindingPayloadKind = "child-dag-delegation-policy-binding";
        private const string DelegationPolicyDecisionPayloadKind = "child-dag-delegation-policy-decision";
        private const string ChildResultPayloadKind = "child-dag-result";
        private const string InvocationPreparationPayloadKind = "child-dag-invocation-preparation";
        private const string ArtifactKeyPrefix = "immutable-sha256-";
        private const string InvocationPreparationKeyPrefix = "child-invocation-preparation-";

        private readonly IAiPayloadStoreResolver payloadStoreResolver;
        private readonly AiImmutableJsonPayloadReader immutableJsonPayloadReader;
        private readonly AiPayloadStoreOptions payloadOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildDagSnapshotService"/> class.
        /// </summary>
        /// <param name="payloadStoreResolver">The configured execution payload store resolver.</param>
        /// <param name="payloadOptions">The execution payload storage options.</param>
        public AiChildDagSnapshotService(
            IAiPayloadStoreResolver payloadStoreResolver,
            IOptions<AiPayloadStoreOptions> payloadOptions)
        {
            this.payloadStoreResolver = payloadStoreResolver ?? throw new ArgumentNullException(nameof(payloadStoreResolver));
            this.immutableJsonPayloadReader = new AiImmutableJsonPayloadReader(this.payloadStoreResolver);
            ArgumentNullException.ThrowIfNull(payloadOptions);
            this.payloadOptions = payloadOptions.Value;
        }

        /// <summary>
        /// Freezes the exact declarative child DAG definition used by one invocation.
        /// </summary>
        /// <param name="definition">The declarative child pipeline definition.</param>
        /// <param name="parentExecutionId">The parent execution identifier used only as payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An inline or artifact-backed immutable payload snapshot.</returns>
        public Task<AiStoredPayload> FreezeDefinitionAsync(
            AiPipelineDefinition definition,
            string parentExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);

            return FreezeAsync(
                definition,
                DefinitionPayloadKind,
                parentExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Loads and verifies a frozen declarative child DAG definition.
        /// </summary>
        /// <param name="snapshot">The immutable child DAG definition snapshot.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact declarative <see cref="AiPipelineDefinition"/> represented by the snapshot.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the snapshot is invalid or the definition does not contain an explicit name and version.
        /// </exception>
        public async Task<AiPipelineDefinition> LoadDefinitionAsync(
            AiStoredPayload snapshot,
            CancellationToken cancellationToken = default)
        {
            var canonicalJson = await LoadAndVerifyAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);

            var definition = AiCanonicalJson.Deserialize<AiPipelineDefinition>(canonicalJson);
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new InvalidOperationException(
                    "Frozen child DAG definition does not contain a pipeline name.");
            }

            if (string.IsNullOrWhiteSpace(definition.Version))
            {
                throw new InvalidOperationException(
                    $"Frozen child DAG definition '{definition.Name}' does not contain an explicit version.");
            }

            return definition;
        }

        /// <summary>
        /// Loads the exact canonical JSON representation of a frozen declarative child DAG definition.
        /// </summary>
        /// <remarks>
        /// The returned JSON is directly compatible with the existing runtime pipeline JSON definition resolver.
        /// No resolved step implementation or runtime service instance is serialized into the snapshot.
        /// </remarks>
        /// <param name="snapshot">The immutable child DAG definition snapshot.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The verified canonical declarative pipeline JSON.</returns>
        public Task<string> LoadDefinitionJsonAsync(
            AiStoredPayload snapshot,
            CancellationToken cancellationToken = default)
        {
            return LoadAndVerifyAsync(snapshot, cancellationToken);
        }

        /// <summary>
        /// Freezes the exact invocation input supplied to a child DAG.
        /// </summary>
        /// <param name="input">The child invocation input.</param>
        /// <param name="parentExecutionId">The parent execution identifier used only as payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An inline or artifact-backed immutable payload snapshot.</returns>
        public Task<AiStoredPayload> FreezeInvocationInputAsync(
            object? input,
            string parentExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);

            return FreezeAsync(
                input,
                InvocationInputPayloadKind,
                parentExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Freezes the exact delegation policy binding resolved for one parent call site.
        /// </summary>
        /// <param name="definition">The resolved child delegation policy definition.</param>
        /// <param name="parentExecutionId">The parent execution identifier used only as payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An inline or artifact-backed immutable policy binding snapshot.</returns>
        public Task<AiStoredPayload> FreezeDelegationPolicyBindingAsync(
            AiChildDelegationPolicyDefinition definition,
            string parentExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);

            return FreezeAsync(
                definition,
                DelegationPolicyBindingPayloadKind,
                parentExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Loads and verifies a frozen delegation policy binding without consulting live step or pipeline configuration.
        /// </summary>
        /// <param name="snapshot">The immutable policy binding snapshot stored on the relation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact delegation policy definition represented by the snapshot.</returns>
        public async Task<AiChildDelegationPolicyDefinition> LoadDelegationPolicyBindingAsync(
            AiStoredPayload snapshot,
            CancellationToken cancellationToken = default)
        {
            var canonicalJson = await LoadAndVerifyAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);

            return AiCanonicalJson.Deserialize<AiChildDelegationPolicyDefinition>(canonicalJson);
        }

        /// <summary>
        /// Freezes the historical outcome of one completed child delegation policy evaluation.
        /// </summary>
        /// <param name="approved">Indicates whether delegation was approved.</param>
        /// <param name="reason">The committed delegation decision reason.</param>
        /// <param name="results">The ordered results returned by the existing policy engine.</param>
        /// <param name="parentExecutionId">The parent execution identifier used only as payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An immutable decision snapshot suitable for committing with the relation CAS.</returns>
        public Task<AiStoredPayload> FreezeDelegationPolicyDecisionAsync(
            bool approved,
            string? reason,
            IReadOnlyCollection<AiPolicyResult> results,
            string parentExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(results);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);

            var snapshot = new DelegationPolicyDecisionSnapshot
            {
                Approved = approved,
                Reason = reason,
                Results = results
                    .Select(result => new DelegationPolicyResultSnapshot
                    {
                        Kind = result.Kind,
                        Message = result.Message
                    })
                    .ToArray()
            };

            return FreezeAsync(
                snapshot,
                DelegationPolicyDecisionPayloadKind,
                parentExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Freezes the authoritative execution-level output of one terminal child DAG execution.
        /// </summary>
        /// <remarks>
        /// The snapshot reuses the execution state's existing inline data and payload-backed data descriptors.
        /// Step runtime state, claims, leases, and other orchestration internals are intentionally excluded from
        /// the business result snapshot.
        /// </remarks>
        /// <param name="state">The terminal child execution state.</param>
        /// <param name="childExecutionId">The child execution identifier used only as immutable payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An inline or artifact-backed immutable child result snapshot.</returns>
        public Task<AiStoredPayload> FreezeChildResultAsync(
            AiExecutionState state,
            string childExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentException.ThrowIfNullOrWhiteSpace(childExecutionId);

            var snapshot = new ChildExecutionResultSnapshot
            {
                Data = new Dictionary<string, object?>(state.Data, StringComparer.Ordinal),
                DataPayloads = state.DataPayloads is null
                    ? null
                    : new Dictionary<string, AiStoredPayload>(state.DataPayloads, StringComparer.Ordinal)
            };

            return FreezeAsync(
                snapshot,
                ChildResultPayloadKind,
                childExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Persists the complete immutable pre-relation preparation for one child invocation generation.
        /// </summary>
        /// <param name="identity">The authoritative typed child invocation identity.</param>
        /// <param name="frozenDefinition">The already frozen declarative child DAG definition.</param>
        /// <param name="frozenInvocationInput">The already frozen invocation input.</param>
        /// <param name="executionContextSnapshot">The durable delegated execution context.</param>
        /// <param name="delegatedMetadata">Adapter-neutral metadata delegated to the child run.</param>
        /// <param name="delegationPolicyBindingSnapshot">The already frozen delegation policy binding.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact immutable preparation manifest persisted for the typed invocation.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the configured payload store cannot provide immutable exact-key writes or when an existing
        /// preparation under the same deterministic identity key contains conflicting content.
        /// </exception>
        public async Task<AiChildInvocationPreparationSnapshot> FreezeInvocationPreparationAsync(
            AiChildInvocationIdentity identity,
            AiStoredPayload frozenDefinition,
            AiStoredPayload frozenInvocationInput,
            ExecutionContextSnapshot executionContextSnapshot,
            IReadOnlyDictionary<string, string> delegatedMetadata,
            AiStoredPayload delegationPolicyBindingSnapshot,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(frozenDefinition);
            ArgumentNullException.ThrowIfNull(frozenInvocationInput);
            ArgumentNullException.ThrowIfNull(executionContextSnapshot);
            ArgumentNullException.ThrowIfNull(delegatedMetadata);
            ArgumentNullException.ThrowIfNull(delegationPolicyBindingSnapshot);

            var childInvocationKey = AiChildInvocationKeyFactory.Create(identity);
            var preparation = new AiChildInvocationPreparationSnapshot
            {
                Identity = identity,
                ChildInvocationKey = childInvocationKey,
                FrozenChildDagDefinition = frozenDefinition,
                FrozenInvocationInput = frozenInvocationInput,
                DelegatedExecutionContextSnapshot = executionContextSnapshot,
                DelegatedMetadata = new Dictionary<string, string>(delegatedMetadata, StringComparer.Ordinal),
                DelegationPolicyBindingSnapshot = delegationPolicyBindingSnapshot
            };
            var canonicalJson = AiCanonicalJson.Serialize(preparation);
            var key = CreateInvocationPreparationKey(childInvocationKey);
            var payloadStore = this.payloadStoreResolver.Resolve();

            if (payloadStore is not IAiImmutablePayloadStore immutablePayloadStore)
            {
                throw new InvalidOperationException(
                    $"Configured payload store '{payloadStore.GetType().Name}' does not support immutable exact-key writes required by child DAG preparation recovery.");
            }

            await immutablePayloadStore
                .SaveImmutableAsync(
                    key,
                    canonicalJson,
                    new AiPayloadMetadata
                    {
                        Kind = InvocationPreparationPayloadKind,
                        ExecutionId = identity.ParentExecutionId,
                        StepName = identity.ParentCallSiteId,
                        ContentType = JsonContentType,
                        Reason = "deterministic-child-dag-pre-relation-preparation"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var persisted = await payloadStore
                .LoadAsync(key, cancellationToken)
                .ConfigureAwait(false);

            if (persisted is null ||
                !string.Equals(
                    AiCanonicalJson.Canonicalize(persisted),
                    canonicalJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Child DAG invocation preparation '{key}' was not durably verified after persistence.");
            }

            return preparation;
        }

        /// <summary>
        /// Loads a previously persisted pre-relation preparation for one exact typed child invocation identity.
        /// </summary>
        /// <param name="identity">The typed invocation identity used to derive the deterministic preparation key.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The verified preparation when present; otherwise <see langword="null"/>.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the persisted preparation does not match the requested typed identity or contains invalid
        /// immutable snapshot references.
        /// </exception>
        public async Task<AiChildInvocationPreparationSnapshot?> TryLoadInvocationPreparationAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var childInvocationKey = AiChildInvocationKeyFactory.Create(identity);
            var key = CreateInvocationPreparationKey(childInvocationKey);
            var payloadStore = this.payloadStoreResolver.Resolve();
            var persisted = await payloadStore
                .LoadAsync(key, cancellationToken)
                .ConfigureAwait(false);

            if (persisted is null)
            {
                return null;
            }

            var canonicalJson = AiCanonicalJson.Canonicalize(persisted);
            var preparation = AiCanonicalJson.Deserialize<AiChildInvocationPreparationSnapshot>(canonicalJson);
            EnsurePreparationMatches(identity, childInvocationKey, preparation);

            await LoadDefinitionAsync(preparation.FrozenChildDagDefinition, cancellationToken).ConfigureAwait(false);
            await LoadAndVerifyAsync(preparation.FrozenInvocationInput, cancellationToken).ConfigureAwait(false);
            await LoadDelegationPolicyBindingAsync(preparation.DelegationPolicyBindingSnapshot, cancellationToken).ConfigureAwait(false);

            return preparation;
        }

        /// <summary>
        /// Creates the deterministic pre-relation preparation payload key for one logical child invocation generation.
        /// </summary>
        /// <param name="childInvocationKey">The deterministic child invocation key.</param>
        /// <returns>The exact immutable payload-store key.</returns>
        private static string CreateInvocationPreparationKey(string childInvocationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childInvocationKey);
            return string.Concat(InvocationPreparationKeyPrefix, childInvocationKey);
        }

        /// <summary>
        /// Validates that a persisted preparation is an exact match for the requested typed invocation identity.
        /// </summary>
        /// <param name="identity">The requested typed identity.</param>
        /// <param name="childInvocationKey">The deterministic key derived from the requested identity.</param>
        /// <param name="preparation">The persisted preparation to validate.</param>
        private static void EnsurePreparationMatches(
            AiChildInvocationIdentity identity,
            string childInvocationKey,
            AiChildInvocationPreparationSnapshot preparation)
        {
            ArgumentNullException.ThrowIfNull(preparation);

            if (!Equals(preparation.Identity, identity) ||
                !string.Equals(preparation.ChildInvocationKey, childInvocationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Persisted child DAG invocation preparation '{childInvocationKey}' does not match the requested typed identity.");
            }

            if (preparation.DelegatedExecutionContextSnapshot is null ||
                !string.Equals(
                    preparation.DelegatedExecutionContextSnapshot.TenantId,
                    identity.TenantId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Persisted child DAG invocation preparation '{childInvocationKey}' does not preserve the authoritative tenant context.");
            }
        }

        /// <summary>
        /// Loads a previously frozen snapshot and verifies its stable content hash before returning it.
        /// </summary>
        /// <param name="snapshot">The frozen payload snapshot.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The canonical JSON content.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the snapshot is incomplete, missing, or its persisted content no longer matches its hash.
        /// </exception>
        public Task<string> LoadAndVerifyAsync(
            AiStoredPayload snapshot,
            CancellationToken cancellationToken = default)
        {
            return this.immutableJsonPayloadReader.LoadAndVerifyAsync(snapshot, cancellationToken);
        }

        /// <summary>
        /// Freezes one serialized value using the shared inline-or-content-addressed snapshot protocol.
        /// </summary>
        /// <param name="value">The value to freeze.</param>
        /// <param name="kind">The semantic payload kind.</param>
        /// <param name="parentExecutionId">The parent execution identifier used as payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The durable immutable stored-payload descriptor.</returns>
        private async Task<AiStoredPayload> FreezeAsync(
            object? value,
            string kind,
            string parentExecutionId,
            CancellationToken cancellationToken)
        {
            var canonicalJson = AiCanonicalJson.Serialize(value);
            var sizeBytes = Encoding.UTF8.GetByteCount(canonicalJson);
            var digest = AiCanonicalJson.ComputeSha256(canonicalJson);

            if (sizeBytes <= Math.Max(0, this.payloadOptions.MaxInlineSizeBytes))
            {
                return AiStoredPayload.Inline(
                    canonicalJson,
                    sizeBytes,
                    JsonContentType,
                    digest);
            }

            var payloadStore = this.payloadStoreResolver.Resolve();
            if (payloadStore is not IAiImmutablePayloadStore immutablePayloadStore)
            {
                throw new InvalidOperationException(
                    $"Configured payload store '{payloadStore.GetType().Name}' does not support immutable exact-key writes required by child DAG snapshots.");
            }

            var artifactId = string.Concat(ArtifactKeyPrefix, digest);
            await immutablePayloadStore
                .SaveImmutableAsync(
                    artifactId,
                    canonicalJson,
                    new AiPayloadMetadata
                    {
                        Kind = kind,
                        ExecutionId = parentExecutionId,
                        ContentType = JsonContentType,
                        Reason = "deterministic-child-dag-snapshot"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var persisted = await payloadStore
                .LoadAsync(artifactId, cancellationToken)
                .ConfigureAwait(false);

            if (persisted is null ||
                !string.Equals(
                    AiCanonicalJson.ComputeSha256(AiCanonicalJson.Canonicalize(persisted)),
                    digest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Immutable child DAG snapshot artifact '{artifactId}' was not durably verified after persistence.");
            }

            return AiStoredPayload.Artifact(
                artifactId,
                digest,
                sizeBytes,
                JsonContentType);
        }

        /// <summary>
        /// Defines the stable execution-level business output frozen for one terminal child execution.
        /// </summary>
        private sealed class ChildExecutionResultSnapshot
        {
            /// <summary>
            /// Gets the inline execution-level output data.
            /// </summary>
            public IReadOnlyDictionary<string, object?> Data { get; init; } =
                new Dictionary<string, object?>();

            /// <summary>
            /// Gets payload-backed execution-level output descriptors when present.
            /// </summary>
            public IReadOnlyDictionary<string, AiStoredPayload>? DataPayloads { get; init; }
        }

        /// <summary>
        /// Defines the canonical historical payload persisted for one delegation policy decision.
        /// </summary>
        private sealed class DelegationPolicyDecisionSnapshot
        {
            /// <summary>
            /// Gets a value indicating whether the evaluated delegation was approved.
            /// </summary>
            public bool Approved { get; init; }

            /// <summary>
            /// Gets the durable decision reason.
            /// </summary>
            public string? Reason { get; init; }

            /// <summary>
            /// Gets the ordered policy results that contributed to the decision.
            /// </summary>
            public IReadOnlyCollection<DelegationPolicyResultSnapshot> Results { get; init; } =
                Array.Empty<DelegationPolicyResultSnapshot>();
        }

        /// <summary>
        /// Defines the stable serializable subset of one policy execution result.
        /// </summary>
        private sealed class DelegationPolicyResultSnapshot
        {
            /// <summary>
            /// Gets the policy result kind produced by the existing policy engine.
            /// </summary>
            public AiPolicyResultKind Kind { get; init; }

            /// <summary>
            /// Gets the optional policy result message.
            /// </summary>
            public string? Message { get; init; }
        }

    }
}
