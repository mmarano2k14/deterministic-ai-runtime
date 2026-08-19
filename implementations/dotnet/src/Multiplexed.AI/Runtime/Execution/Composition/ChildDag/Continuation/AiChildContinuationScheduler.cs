using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation
{
    /// <summary>
    /// Enqueues one already-authorized parent continuation through the existing shared/global runtime queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This scheduler is deliberately narrow. It never creates a parent execution and never uses crash-recovery
    /// resume metadata. It only submits a normal continuation for one existing
    /// <see cref="AiStepExecutionStatus.WaitingForExternal"/> step after the durable child relation has already
    /// recorded continuation status <see cref="AiChildContinuationStatus.Scheduled"/>.
    /// </para>
    /// <para>
    /// The logical continuation identifier and its shared-run identifier are deterministic and stable. Reconciliation
    /// therefore re-drives the same durable shared queue item instead of manufacturing parallel physical copies while
    /// an earlier continuation attempt is still queued, claimed, or being accepted by a runtime. The existing shared
    /// controller and queue provide the idempotent create/enqueue boundary for duplicate submissions.
    /// </para>
    /// </remarks>
    public sealed class AiChildContinuationScheduler
    {
        private const string ContinuationSharedRunPrefix = "child-continuation-";
        private const string ParkRepairSharedRunPrefix = "child-park-repair-";
        private const string ContinuationIdentityPrefix = "child-continuation:";
        private const string ParkRepairIdentityPrefix = "child-park-repair:";

        private readonly IAiSharedRuntimeController sharedRuntimeController;
        private readonly IAiSharedQueue sharedQueue;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationScheduler"/> class.
        /// </summary>
        /// <param name="sharedRuntimeController">The existing shared runtime controller.</param>
        /// <param name="sharedQueue">The existing shared/global queue that owns physical continuation delivery.</param>
        public AiChildContinuationScheduler(
            IAiSharedRuntimeController sharedRuntimeController,
            IAiSharedQueue sharedQueue)
        {
            this.sharedRuntimeController = sharedRuntimeController ?? throw new ArgumentNullException(nameof(sharedRuntimeController));
            this.sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
        }

        /// <summary>
        /// Enqueues a durable completed-child continuation for the exact waiting parent step.
        /// </summary>
        /// <param name="relation">The authoritative completed child relation.</param>
        /// <param name="parentRecord">The authoritative nonterminal parent execution record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared runtime controller result for the deterministic continuation submission.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation is not durably scheduled, the parent identity/context is inconsistent, or the
        /// existing shared runtime controller does not preserve the exact continuation identity.
        /// </exception>
        public Task<AiSharedRuntimeControllerResult> EnqueueContinuationAsync(
            AiChildExecutionRelation relation,
            AiExecutionRecord parentRecord,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ArgumentNullException.ThrowIfNull(parentRecord);

            if (relation.Status != AiChildExecutionRelationStatus.Completed ||
                relation.ContinuationStatus != AiChildContinuationStatus.Scheduled)
            {
                throw new InvalidOperationException(
                    $"Parent continuation can be enqueued only from Completed/Scheduled relation state. RelationStatus='{relation.Status}', ContinuationStatus='{relation.ContinuationStatus}'.");
            }

            return EnqueueAsync(
                relation,
                parentRecord,
                CreateDeterministicSharedRunId(ContinuationSharedRunPrefix, relation.ChildInvocationKey),
                string.Concat(ContinuationIdentityPrefix, relation.ChildInvocationKey),
                "resume-parent-after-child-completion",
                cancellationToken);
        }

        /// <summary>
        /// Cancels any still-pending physical delivery for one deterministic completed-child continuation.
        /// </summary>
        /// <remarks>
        /// The durable child relation remains the logical continuation authority. Once parent state proves that the
        /// continuation was consumed or can no longer run, any pending or claimed copy of the deterministic queue
        /// item is obsolete. Cancelling that existing item prevents a stale at-least-once delivery from being
        /// requeued forever while preserving the historical shared-run record and all completed dispatch evidence.
        /// </remarks>
        /// <param name="relation">The authoritative completed child relation.</param>
        /// <param name="reason">The durable reason why further physical continuation delivery is obsolete.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task CancelContinuationDeliveryAsync(
            AiChildExecutionRelation relation,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            if (relation.Status != AiChildExecutionRelationStatus.Completed ||
                relation.ContinuationStatus is not (AiChildContinuationStatus.Pending or AiChildContinuationStatus.Scheduled))
            {
                throw new InvalidOperationException(
                    $"Physical continuation delivery can be cancelled only from Completed/Pending or Completed/Scheduled relation state. " +
                    $"RelationStatus='{relation.Status}', ContinuationStatus='{relation.ContinuationStatus}'.");
            }

            await this.sharedQueue
                .CancelAsync(
                    CreateDeterministicSharedRunId(ContinuationSharedRunPrefix, relation.ChildInvocationKey),
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Enqueues a defensive re-drive when a parent step is parked but the relation never advanced from
        /// <see cref="AiChildExecutionRelationStatus.ChildAllocated"/> to waiting.
        /// </summary>
        /// <param name="relation">The authoritative suspicious child relation.</param>
        /// <param name="parentRecord">The authoritative nonterminal parent execution record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared runtime controller result for the deterministic repair submission.</returns>
        public Task<AiSharedRuntimeControllerResult> EnqueueParkRepairAsync(
            AiChildExecutionRelation relation,
            AiExecutionRecord parentRecord,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ArgumentNullException.ThrowIfNull(parentRecord);

            if (relation.Status != AiChildExecutionRelationStatus.ChildAllocated)
            {
                throw new InvalidOperationException(
                    $"Parent park repair can be enqueued only while relation status is ChildAllocated. CurrentStatus='{relation.Status}'.");
            }

            return EnqueueAsync(
                relation,
                parentRecord,
                CreateDeterministicSharedRunId(ParkRepairSharedRunPrefix, relation.ChildInvocationKey),
                string.Concat(ParkRepairIdentityPrefix, relation.ChildInvocationKey),
                "repair-parent-park-consistency",
                cancellationToken);
        }

        /// <summary>
        /// Submits one physical external-wait re-drive through the existing shared runtime controller.
        /// </summary>
        private async Task<AiSharedRuntimeControllerResult> EnqueueAsync(
            AiChildExecutionRelation relation,
            AiExecutionRecord parentRecord,
            string sharedRunId,
            string continuationId,
            string reason,
            CancellationToken cancellationToken)
        {
            if (parentRecord.IsTerminal)
            {
                throw new InvalidOperationException(
                    $"Terminal parent execution '{parentRecord.ExecutionId}' cannot accept child continuation '{relation.ChildInvocationKey}'.");
            }

            if (!string.Equals(parentRecord.ExecutionId, relation.ParentExecutionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Parent execution identity mismatch. RelationParentExecutionId='{relation.ParentExecutionId}', RecordExecutionId='{parentRecord.ExecutionId}'.");
            }

            if (string.IsNullOrWhiteSpace(parentRecord.PipelineName))
            {
                throw new InvalidOperationException(
                    $"Parent execution '{parentRecord.ExecutionId}' does not contain a pipeline name required for continuation dispatch.");
            }

            var executionContextSnapshot = parentRecord.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    $"Parent execution '{parentRecord.ExecutionId}' does not contain the durable execution context snapshot required for continuation dispatch.");

            if (!string.Equals(executionContextSnapshot.TenantId, relation.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Parent execution tenant '{executionContextSnapshot.TenantId}' does not match child relation tenant '{relation.TenantId}'.");
            }

            var continuation = new AiRuntimeExternalWaitContinuation
            {
                ExecutionId = relation.ParentExecutionId,
                StepName = relation.ParentCallSiteId,
                ContinuationId = continuationId
            };

            var metadata = BuildMetadata(relation, continuationId, reason);
            var result = await this.sharedRuntimeController
                .SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                        TenantId = relation.TenantId,
                        PipelineKey = parentRecord.PipelineName,
                        CorrelationId = continuationId,
                        Source = "child-dag-composition",
                        Reason = reason,
                        Metadata = metadata,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = parentRecord.PipelineName,
                            ExternalWaitContinuation = continuation,
                            ExecutionContextSnapshot = executionContextSnapshot,
                            Metadata = metadata
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.Run is null)
            {
                throw new InvalidOperationException(
                    result.FailureReason ??
                    $"Shared runtime controller rejected parent continuation '{continuationId}'.");
            }

            var acceptedContinuation = result.Run.RunRequest.ExternalWaitContinuation;
            if (!string.Equals(result.SharedRunId, sharedRunId, StringComparison.Ordinal) ||
                acceptedContinuation is null ||
                !string.Equals(acceptedContinuation.ExecutionId, continuation.ExecutionId, StringComparison.Ordinal) ||
                !string.Equals(acceptedContinuation.StepName, continuation.StepName, StringComparison.Ordinal) ||
                !string.Equals(acceptedContinuation.ContinuationId, continuation.ContinuationId, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(result.Run.RunRequest.RequestedExecutionId))
            {
                throw new InvalidOperationException(
                    $"Shared runtime continuation '{continuationId}' did not preserve the exact parent execution and waiting-step identity.");
            }

            return result;
        }


        /// <summary>
        /// Creates the stable shared-run identity for one logical continuation or park-repair operation.
        /// </summary>
        /// <param name="prefix">The operation-specific shared-run prefix.</param>
        /// <param name="childInvocationKey">The durable child invocation key.</param>
        /// <returns>The deterministic shared-run identifier used by every reconciliation re-drive.</returns>
        private static string CreateDeterministicSharedRunId(
            string prefix,
            string childInvocationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(childInvocationKey);

            return string.Concat(prefix, childInvocationKey);
        }

        /// <summary>
        /// Builds deterministic diagnostic metadata for parent continuation and park-repair submissions.
        /// </summary>
        private static IReadOnlyDictionary<string, string> BuildMetadata(
            AiChildExecutionRelation relation,
            string continuationId,
            string reason)
        {
            var controlPlaneId = relation.ControlPlaneId;
            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' does not contain the durable logical control-plane authority required for continuation dispatch.");
            }

            return new Dictionary<string, string>(relation.DelegatedMetadata, StringComparer.Ordinal)
            {
                ["child.invocation.key"] = relation.ChildInvocationKey,
                ["child.execution.id"] = relation.ChildExecutionId ?? string.Empty,
                ["parent.execution.id"] = relation.ParentExecutionId,
                ["parent.callsite.id"] = relation.ParentCallSiteId,
                ["external.wait.continuation"] = "true",
                ["external.wait.continuation.id"] = continuationId,
                ["continuation.reason"] = reason
            };
        }
    }
}
