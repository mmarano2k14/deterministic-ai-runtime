using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Coordinates durable child-DAG delegation policy binding and compare-and-swap decision commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Policy configuration is resolved and frozen before the authoritative relation is created. Evaluation later
    /// consumes only that frozen binding, so recovery never re-resolves live step or pipeline policy configuration.
    /// </para>
    /// <para>
    /// Concurrent evaluators may execute policies, but exactly one evaluator may commit the transition from
    /// <see cref="AiChildExecutionRelationStatus.DelegationPolicyPending"/> to an approved or denied state.
    /// A losing evaluator reloads and returns the already committed durable relation.
    /// </para>
    /// </remarks>
    public sealed class AiChildDelegationPolicyCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly IAiPolicyEngineFactory policyEngineFactory;
        private readonly AiChildDagSnapshotService snapshotService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildDelegationPolicyCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative child relation store.</param>
        /// <param name="policyEngineFactory">The existing runtime policy engine factory.</param>
        /// <param name="snapshotService">The immutable child DAG snapshot service.</param>
        public AiChildDelegationPolicyCoordinator(
            IAiChildExecutionRelationStore relationStore,
            IAiPolicyEngineFactory policyEngineFactory,
            AiChildDagSnapshotService snapshotService)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.policyEngineFactory = policyEngineFactory ?? throw new ArgumentNullException(nameof(policyEngineFactory));
            this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        }

        /// <summary>
        /// Resolves the current parent delegation policy binding and freezes it for initial relation creation.
        /// </summary>
        /// <param name="stepContext">The parent step context whose step and pipeline configuration define policy precedence.</param>
        /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
        /// <returns>The immutable delegation policy binding snapshot that must be stored on the initial relation.</returns>
        public async Task<AiStoredPayload> ResolveAndFreezeBindingAsync(
            AiStepExecutionContext stepContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stepContext);

            var engine = this.policyEngineFactory.Create<IAiChildDelegationPolicyEngine>(
                AiPolicyKind.Delegation,
                stepContext);

            var definition = await engine
                .ResolveDefinitionAsync(cancellationToken)
                .ConfigureAwait(false);

            return await this.snapshotService
                .FreezeDelegationPolicyBindingAsync(
                    definition,
                    stepContext.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Evaluates and durably commits the delegation decision for one authoritative child invocation identity.
        /// </summary>
        /// <param name="identity">The authoritative typed child invocation identity.</param>
        /// <param name="stepContext">The parent step context used only to create the existing step-scoped policy engine.</param>
        /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
        /// <returns>The committed relation, including the durable winner when this evaluator loses the CAS race.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation does not exist, is in an invalid state for delegation evaluation, or a policy
        /// returns a non-success result that is neither an explicit block nor an approval.
        /// </exception>
        public async Task<AiChildExecutionRelation> EvaluateAsync(
            AiChildInvocationIdentity identity,
            AiStepExecutionContext stepContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(stepContext);

            var relation = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Child delegation policy cannot be evaluated before the authoritative relation exists.");

            if (relation.Status is AiChildExecutionRelationStatus.DelegationApproved or
                AiChildExecutionRelationStatus.DelegationDenied)
            {
                return relation;
            }

            if (relation.Status != AiChildExecutionRelationStatus.DelegationPolicyPending)
            {
                throw new InvalidOperationException(
                    $"Child delegation policy cannot be evaluated from relation status '{relation.Status}'.");
            }

            if (relation.ChildExecutionId is not null)
            {
                throw new InvalidOperationException(
                    "A delegation-pending relation cannot already contain a child execution identifier.");
            }

            var frozenDefinition = await this.snapshotService
                .LoadDelegationPolicyBindingAsync(
                    relation.DelegationPolicyBindingSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);

            var engine = this.policyEngineFactory.Create<IAiChildDelegationPolicyEngine>(
                AiPolicyKind.Delegation,
                stepContext);

            var results = await engine
                .EvaluateAsync(
                    relation,
                    frozenDefinition,
                    cancellationToken)
                .ConfigureAwait(false);

            var decision = ResolveDecision(results);
            var decisionSnapshot = await this.snapshotService
                .FreezeDelegationPolicyDecisionAsync(
                    decision.Approved,
                    decision.Reason,
                    results,
                    relation.ParentExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            relation.Status = decision.Approved
                ? AiChildExecutionRelationStatus.DelegationApproved
                : AiChildExecutionRelationStatus.DelegationDenied;
            relation.DelegationPolicyDecisionSnapshot = decisionSnapshot;
            relation.DelegationEvaluatedAtUtc = DateTimeOffset.UtcNow;

            var committed = await this.relationStore
                .TryReplaceAsync(
                    relation,
                    AiChildExecutionRelationStatus.DelegationPolicyPending,
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed)
            {
                return relation;
            }

            return await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Child delegation policy CAS lost and the committed relation could not be reloaded.");
        }

        /// <summary>
        /// Converts existing policy results into the durable child delegation decision semantics.
        /// </summary>
        /// <param name="results">The ordered policy results.</param>
        /// <returns>The resolved approval flag and durable decision reason.</returns>
        private static (bool Approved, string Reason) ResolveDecision(
            IReadOnlyCollection<AiPolicyResult> results)
        {
            var denied = results.FirstOrDefault(result => result.Kind == AiPolicyResultKind.Block);
            if (denied is not null)
            {
                return (
                    false,
                    string.IsNullOrWhiteSpace(denied.Message)
                        ? "Child delegation was denied by policy."
                        : denied.Message!);
            }

            var failed = results.FirstOrDefault(result => !result.IsSuccess);
            if (failed is not null)
            {
                throw new InvalidOperationException(
                    $"Child delegation policy evaluation produced non-terminal result kind '{failed.Kind}'. " +
                    "The durable relation remains policy-pending and no child-side effect is permitted.");
            }

            var approvalReason = results
                .Select(result => result.Message)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));

            return (
                true,
                approvalReason ?? (results.Count == 0
                    ? "No child delegation policies were configured; delegation is allowed by default."
                    : "Child delegation was approved by policy."));
        }
    }
}
