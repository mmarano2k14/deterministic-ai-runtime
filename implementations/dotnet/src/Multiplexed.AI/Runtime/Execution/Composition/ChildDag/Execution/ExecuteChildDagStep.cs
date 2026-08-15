using System.Globalization;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution
{
    /// <summary>
    /// Executes one deterministic child DAG invocation from a normal parent DAG step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the native composition primitive for child DAG execution. It does not create a second scheduler,
    /// queue, policy engine, or recovery model. Instead it coordinates the durable components already used by the
    /// normal execution runtime: immutable snapshots, the authoritative parent-child relation, the existing Policy
    /// Engine, exact child execution allocation, the existing shared/global queue, and durable external waiting.
    /// </para>
    /// <para>
    /// The parent step configuration must provide <c>childDagId</c>, <c>childDagVersion</c>, and
    /// <c>logicalInvocationKey</c>. It may also embed the same declarative JSON-compatible
    /// <see cref="AiPipelineDefinition"/> under <c>childDagDefinition</c>. When present, that exact declarative
    /// definition is used for initial freezing instead of resolving mutable live provider state. Keeping the child
    /// definition version in the declarative parent definition allows recovery to reconstruct the same typed
    /// invocation identity after the relation or preparation snapshot exists.
    /// </para>
    /// </remarks>
    [AiStep(StepKey)]
    public sealed class ExecuteChildDagStep : IAiStep
    {
        /// <summary>
        /// Gets the registry key used by declarative pipeline definitions.
        /// </summary>
        public const string StepKey = "execution.child-dag";

        /// <summary>
        /// Gets the declarative configuration key containing the logical child pipeline identifier.
        /// </summary>
        public const string ChildDagIdConfigKey = "childDagId";

        /// <summary>
        /// Gets the declarative configuration key containing the exact child pipeline definition version.
        /// </summary>
        public const string ChildDagVersionConfigKey = "childDagVersion";

        /// <summary>
        /// Gets the declarative configuration key containing the canonical logical invocation key.
        /// </summary>
        public const string LogicalInvocationKeyConfigKey = "logicalInvocationKey";

        /// <summary>
        /// Gets the optional declarative configuration key containing an inline JSON-compatible child definition.
        /// </summary>
        public const string ChildDagDefinitionConfigKey = "childDagDefinition";
        private const int MaximumGenerationTraversal = 1024;

        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly IAiPipelineDefinitionSourceSelector pipelineDefinitionSourceSelector;
        private readonly AiChildDagSnapshotService snapshotService;
        private readonly AiChildDelegationPolicyCoordinator delegationPolicyCoordinator;
        private readonly AiChildExecutionAllocator allocator;
        private readonly AiChildExecutionDispatcher dispatcher;
        private readonly AiChildExecutionWaitingCoordinator waitingCoordinator;
        private readonly AiChildInvocationGenerationCoordinator generationCoordinator;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecuteChildDagStep"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative parent-child relation store.</param>
        /// <param name="pipelineDefinitionSourceSelector">The existing declarative pipeline definition source selector.</param>
        /// <param name="snapshotService">The immutable child DAG snapshot service.</param>
        /// <param name="delegationPolicyCoordinator">The existing Policy Engine delegation coordinator.</param>
        /// <param name="allocator">The exact child execution identifier allocator.</param>
        /// <param name="dispatcher">The existing shared/global queue child dispatcher.</param>
        /// <param name="waitingCoordinator">The durable child waiting-state coordinator.</param>
        /// <param name="generationCoordinator">The explicit retry-generation coordinator.</param>
        public ExecuteChildDagStep(
            IAiChildExecutionRelationStore relationStore,
            IAiPipelineDefinitionSourceSelector pipelineDefinitionSourceSelector,
            AiChildDagSnapshotService snapshotService,
            AiChildDelegationPolicyCoordinator delegationPolicyCoordinator,
            AiChildExecutionAllocator allocator,
            AiChildExecutionDispatcher dispatcher,
            AiChildExecutionWaitingCoordinator waitingCoordinator,
            AiChildInvocationGenerationCoordinator generationCoordinator)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.pipelineDefinitionSourceSelector = pipelineDefinitionSourceSelector ?? throw new ArgumentNullException(nameof(pipelineDefinitionSourceSelector));
            this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            this.delegationPolicyCoordinator = delegationPolicyCoordinator ?? throw new ArgumentNullException(nameof(delegationPolicyCoordinator));
            this.allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.waitingCoordinator = waitingCoordinator ?? throw new ArgumentNullException(nameof(waitingCoordinator));
            this.generationCoordinator = generationCoordinator ?? throw new ArgumentNullException(nameof(generationCoordinator));
        }

        /// <inheritdoc />
        public string Name => StepKey;

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper = context.GetHelper();
            var childDagId = await helper
                .GetRequiredConfigAsync<string>(ChildDagIdConfigKey, cancellationToken)
                .ConfigureAwait(false);
            var childDagVersion = await helper
                .GetRequiredConfigAsync<string>(ChildDagVersionConfigKey, cancellationToken)
                .ConfigureAwait(false);
            var logicalInvocationKey = await helper
                .GetRequiredConfigAsync<string>(LogicalInvocationKeyConfigKey, cancellationToken)
                .ConfigureAwait(false);

            ArgumentException.ThrowIfNullOrWhiteSpace(childDagId);
            ArgumentException.ThrowIfNullOrWhiteSpace(childDagVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(logicalInvocationKey);

            var executionContextSnapshot = context.Record.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    "ExecuteChildDag requires the parent execution context snapshot to be durable before child composition begins.");

            if (string.IsNullOrWhiteSpace(executionContextSnapshot.TenantId))
            {
                throw new InvalidOperationException(
                    "ExecuteChildDag requires a non-empty durable TenantId in the parent execution context snapshot.");
            }

            var identity = CreateIdentity(
                executionContextSnapshot.TenantId,
                context.ExecutionId,
                context.StepName,
                childDagId,
                childDagVersion,
                logicalInvocationKey,
                invocationGeneration: 0);

            var relation = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = await CreateInitialRelationAsync(
                        identity,
                        context,
                        helper,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            relation = await ResolveActiveGenerationAsync(relation, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (relation.Status)
                {
                    case AiChildExecutionRelationStatus.DelegationPolicyPending:
                        relation = await this.delegationPolicyCoordinator
                            .EvaluateAsync(relation.ToInvocationIdentity(), context, cancellationToken)
                            .ConfigureAwait(false);
                        continue;

                    case AiChildExecutionRelationStatus.DelegationDenied:
                        return CreateDelegationDeniedResult(relation);

                    case AiChildExecutionRelationStatus.DelegationApproved:
                        relation = await this.allocator
                            .AllocateAsync(relation.ToInvocationIdentity(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;

                    case AiChildExecutionRelationStatus.ChildAllocated:
                        await this.dispatcher
                            .DispatchAsync(relation.ToInvocationIdentity(), cancellationToken)
                            .ConfigureAwait(false);

                        relation = await this.waitingCoordinator
                            .EnsureWaitingAsync(relation.ToInvocationIdentity(), cancellationToken)
                            .ConfigureAwait(false);

                        if (relation.Status == AiChildExecutionRelationStatus.Completed)
                        {
                            continue;
                        }

                        return AiStepResult.Park(
                            $"Waiting for child execution '{relation.ChildExecutionId}' to complete.");

                    case AiChildExecutionRelationStatus.Waiting:
                        return AiStepResult.Park(
                            $"Waiting for child execution '{relation.ChildExecutionId}' to complete.");

                    case AiChildExecutionRelationStatus.Completed:
                        return CreateCompletedResult(relation);

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported child execution relation status '{relation.Status}'.");
                }
            }
        }

        /// <summary>
        /// Creates the complete initial durable relation only after all immutable invocation inputs have been frozen.
        /// </summary>
        /// <param name="identity">The generation-zero typed invocation identity.</param>
        /// <param name="context">The current parent step context.</param>
        /// <param name="helper">The existing step context helper.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative initial relation, including a concurrent winner when another writer created it first.</returns>
        private async Task<AiChildExecutionRelation> CreateInitialRelationAsync(
            AiChildInvocationIdentity identity,
            AiStepExecutionContext context,
            IAiStepContextHelper helper,
            CancellationToken cancellationToken)
        {
            var preparation = await this.snapshotService
                .TryLoadInvocationPreparationAsync(identity, cancellationToken)
                .ConfigureAwait(false);

            if (preparation is null)
            {
                var definition = await ResolveInitialChildDefinitionAsync(
                        identity,
                        helper,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.Equals(definition.Name, identity.ChildDagId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Resolved child DAG definition name '{definition.Name}' does not match requested child DAG id '{identity.ChildDagId}'.");
                }

                if (!string.Equals(definition.Version, identity.ChildDagDefinitionVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Resolved child DAG definition version '{definition.Version ?? string.Empty}' does not match requested version '{identity.ChildDagDefinitionVersion}'.");
                }

                if (definition.ExecutionMode != AiExecutionMode.Dag)
                {
                    throw new InvalidOperationException(
                        $"Pipeline '{definition.Name}' is configured for execution mode '{definition.ExecutionMode}' and cannot be invoked as a child DAG.");
                }

                var frozenDefinition = await this.snapshotService
                    .FreezeDefinitionAsync(definition, context.ExecutionId, cancellationToken)
                    .ConfigureAwait(false);
                var frozenInvocationInput = await this.snapshotService
                    .FreezeInvocationInputAsync(
                        await helper.GetResolvedInputsAsync(
                                includeReservedVariables: false,
                                cancellationToken)
                            .ConfigureAwait(false),
                        context.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                var frozenPolicyBinding = await this.delegationPolicyCoordinator
                    .ResolveAndFreezeBindingAsync(context, cancellationToken)
                    .ConfigureAwait(false);

                preparation = await this.snapshotService
                    .FreezeInvocationPreparationAsync(
                        identity,
                        frozenDefinition,
                        frozenInvocationInput,
                        context.Record.ExecutionContextSnapshot
                            ?? throw new InvalidOperationException(
                                "ExecuteChildDag requires a durable parent execution context snapshot."),
                        CreateDelegatedMetadata(context.State.Metadata),
                        frozenPolicyBinding,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var relation = new AiChildExecutionRelation
            {
                TenantId = preparation.Identity.TenantId,
                ParentExecutionId = preparation.Identity.ParentExecutionId,
                ParentCallSiteId = preparation.Identity.ParentCallSiteId,
                ChildDagId = preparation.Identity.ChildDagId,
                ChildDagDefinitionVersion = preparation.Identity.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = preparation.FrozenChildDagDefinition,
                CanonicalLogicalInvocationKey = preparation.Identity.CanonicalLogicalInvocationKey,
                ChildInvocationKey = preparation.ChildInvocationKey,
                InvocationGeneration = preparation.Identity.InvocationGeneration,
                FrozenInvocationInput = preparation.FrozenInvocationInput,
                DelegatedExecutionContextSnapshot = preparation.DelegatedExecutionContextSnapshot,
                DelegatedMetadata = preparation.DelegatedMetadata,
                DelegationPolicyBindingSnapshot = preparation.DelegationPolicyBindingSnapshot,
                Status = AiChildExecutionRelationStatus.DelegationPolicyPending,
                ContinuationStatus = AiChildContinuationStatus.None,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            return await this.relationStore
                .GetOrCreateAsync(relation, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the exact declarative child definition used for initial invocation preparation.
        /// </summary>
        /// <param name="identity">The typed generation-zero invocation identity.</param>
        /// <param name="helper">The existing parent step context helper.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The inline JSON-compatible child definition when configured; otherwise the definition resolved through
        /// the existing pipeline definition source selector.
        /// </returns>
        /// <remarks>
        /// Inline definitions use the existing <see cref="AiPipelineDefinition"/> contract and are not a second
        /// pipeline format. Once immutable preparation or relation state exists, recovery no longer calls this
        /// method and therefore does not depend on live provider state.
        /// </remarks>
        private async Task<AiPipelineDefinition> ResolveInitialChildDefinitionAsync(
            AiChildInvocationIdentity identity,
            IAiStepContextHelper helper,
            CancellationToken cancellationToken)
        {
            var inlineDefinition = await helper
                .GetConfigAsync<AiPipelineDefinition>(ChildDagDefinitionConfigKey, cancellationToken)
                .ConfigureAwait(false);

            if (inlineDefinition is not null)
            {
                return inlineDefinition;
            }

            var provider = this.pipelineDefinitionSourceSelector.Select(identity.ChildDagId);
            return await provider
                .GetDefinitionAsync(identity.ChildDagId, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Follows only explicitly committed generation decisions until the currently active relation is reached.
        /// </summary>
        /// <param name="relation">The relation from which generation traversal starts.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The active durable relation.</returns>
        private async Task<AiChildExecutionRelation> ResolveActiveGenerationAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken)
        {
            for (var traversal = 0; traversal < MaximumGenerationTraversal; traversal++)
            {
                if (relation.NextInvocationGeneration is null)
                {
                    return relation;
                }

                var expectedNext = relation.InvocationGeneration + 1;
                if (relation.NextInvocationGeneration.Value != expectedNext)
                {
                    throw new InvalidOperationException(
                        $"Child relation '{relation.ChildInvocationKey}' contains non-contiguous generation transition " +
                        $"'{relation.InvocationGeneration}' -> '{relation.NextInvocationGeneration.Value}'.");
                }

                var nextIdentity = CreateIdentity(
                    relation.TenantId,
                    relation.ParentExecutionId,
                    relation.ParentCallSiteId,
                    relation.ChildDagId,
                    relation.ChildDagDefinitionVersion,
                    relation.CanonicalLogicalInvocationKey,
                    expectedNext);

                var next = await this.relationStore
                    .GetAsync(nextIdentity, cancellationToken)
                    .ConfigureAwait(false);

                relation = next ?? await this.generationCoordinator
                    .PrepareNextGenerationAsync(
                        relation.ToInvocationIdentity(),
                        relation.NextInvocationGenerationDecisionReason ?? "Resume durable child retry generation.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Child invocation generation traversal exceeded the safety limit of {MaximumGenerationTraversal} generations.");
        }

        /// <summary>
        /// Creates one immutable typed child invocation identity.
        /// </summary>
        /// <param name="tenantId">The tenant that owns the invocation.</param>
        /// <param name="parentExecutionId">The durable parent execution identifier.</param>
        /// <param name="parentCallSiteId">The stable parent call-site identifier.</param>
        /// <param name="childDagId">The logical child DAG identifier.</param>
        /// <param name="childDagVersion">The exact frozen child DAG definition version.</param>
        /// <param name="logicalInvocationKey">The canonical business invocation key.</param>
        /// <param name="invocationGeneration">The explicit durable invocation generation.</param>
        /// <returns>The typed identity tuple used by relation persistence and deterministic key derivation.</returns>
        private static AiChildInvocationIdentity CreateIdentity(
            string tenantId,
            string parentExecutionId,
            string parentCallSiteId,
            string childDagId,
            string childDagVersion,
            string logicalInvocationKey,
            int invocationGeneration)
        {
            return new AiChildInvocationIdentity
            {
                TenantId = tenantId,
                ParentExecutionId = parentExecutionId,
                ParentCallSiteId = parentCallSiteId,
                ChildDagId = childDagId,
                ChildDagDefinitionVersion = childDagVersion,
                CanonicalLogicalInvocationKey = logicalInvocationKey,
                InvocationGeneration = invocationGeneration
            };
        }

        /// <summary>
        /// Creates the stable parent result for a durably denied child delegation.
        /// </summary>
        /// <param name="relation">The authoritative denied relation.</param>
        /// <returns>A failed parent-step result that preserves deterministic child invocation metadata.</returns>
        private static AiStepResult CreateDelegationDeniedResult(AiChildExecutionRelation relation)
        {
            return AiStepResult.Fail(
                "Child DAG delegation was denied by the durable parent delegation policy decision.",
                data: CreateResultMetadata(relation));
        }

        /// <summary>
        /// Creates the parent step result from the authoritative durable child result snapshot.
        /// </summary>
        /// <param name="relation">The authoritative completed relation.</param>
        /// <returns>A completed or failed parent-step result backed by the frozen child result.</returns>
        private static AiStepResult CreateCompletedResult(AiChildExecutionRelation relation)
        {
            var childResult = relation.ChildResult
                ?? throw new InvalidOperationException(
                    $"Completed child relation '{relation.ChildInvocationKey}' does not contain an authoritative child result.");
            var metadata = CreateResultMetadata(relation);

            return string.IsNullOrWhiteSpace(relation.ChildFailureReason)
                ? AiStepResult.OkPayload(
                    childResult,
                    output: $"Child execution '{relation.ChildExecutionId}' completed.",
                    data: metadata)
                : AiStepResult.FailPayload(
                    relation.ChildFailureReason,
                    childResult,
                    metadata);
        }

        /// <summary>
        /// Converts parent execution metadata into the existing adapter-neutral string metadata shape.
        /// </summary>
        /// <param name="metadata">The current durable parent execution metadata.</param>
        /// <returns>A stable string-only metadata snapshot suitable for the existing runtime run request contract.</returns>
        /// <remarks>
        /// Only scalar values that map safely to the existing string metadata contract are delegated. Complex parent
        /// metadata remains in parent state and is not promoted into child invocation identity or scheduling semantics.
        /// </remarks>
        private static IReadOnlyDictionary<string, string> CreateDelegatedMetadata(
            IReadOnlyDictionary<string, object?> metadata)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var pair in metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (pair.Value is null)
                {
                    continue;
                }

                var value = pair.Value switch
                {
                    string text => text,
                    bool boolean => boolean.ToString(CultureInfo.InvariantCulture),
                    JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonElement element when element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                    Enum enumValue => enumValue.ToString(),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => null
                };

                if (value is not null)
                {
                    result[pair.Key] = value;
                }
            }

            return result;
        }

        /// <summary>
        /// Builds deterministic parent result metadata for one child invocation generation.
        /// </summary>
        /// <param name="relation">The authoritative child relation.</param>
        /// <returns>Metadata that identifies the exact child invocation generation and authoritative result digest.</returns>
        private static Dictionary<string, object?> CreateResultMetadata(AiChildExecutionRelation relation)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["childInvocationKey"] = relation.ChildInvocationKey,
                ["childExecutionId"] = relation.ChildExecutionId,
                ["childInvocationGeneration"] = relation.InvocationGeneration,
                ["childDagId"] = relation.ChildDagId,
                ["childDagDefinitionVersion"] = relation.ChildDagDefinitionVersion,
                ["childResultDigest"] = relation.ChildResult?.ContentHash
            };
        }
    }
}
