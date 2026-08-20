using System.Globalization;
using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Dispatch
{
    /// <summary>
    /// Dispatches an allocated child DAG through the existing shared/global runtime queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component does not create a second scheduler or queue. It reconstructs the exact frozen declarative
    /// pipeline JSON and invocation input, supplies the already allocated child execution identifier, and forces
    /// the existing shared runtime controller through its queue-first path.
    /// </para>
    /// <para>
    /// The deterministic shared run identifier is derived from <see cref="AiChildExecutionRelation.ChildExecutionId"/>.
    /// Repeated physical submissions therefore converge on the same shared run and queue item.
    /// </para>
    /// </remarks>
    public sealed class AiChildExecutionDispatcher
    {
        private const string SharedRunPrefix = "child-execution-";

        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly AiChildDagSnapshotService snapshotService;
        private readonly IAiSharedRuntimeController sharedRuntimeController;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionDispatcher"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative child execution relation store.</param>
        /// <param name="snapshotService">The immutable child DAG snapshot service.</param>
        /// <param name="sharedRuntimeController">The existing shared runtime controller.</param>
        public AiChildExecutionDispatcher(
            IAiChildExecutionRelationStore relationStore,
            AiChildDagSnapshotService snapshotService,
            IAiSharedRuntimeController sharedRuntimeController)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            this.sharedRuntimeController = sharedRuntimeController ?? throw new ArgumentNullException(nameof(sharedRuntimeController));
        }

        /// <summary>
        /// Dispatches the exact allocated child execution through the existing shared/global queue.
        /// </summary>
        /// <param name="identity">The authoritative typed logical invocation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared runtime controller result for the deterministic child submission.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation is missing, has not allocated an execution identifier, does not carry the
        /// delegated tenant context, contains invalid immutable snapshots, or the shared controller rejects the run.
        /// </exception>
        public async Task<AiSharedRuntimeControllerResult> DispatchAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var relation = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A child execution cannot be dispatched before the authoritative relation exists.");

            EnsureDispatchable(relation);

            var pipelineJson = await this.snapshotService
                .LoadDefinitionJsonAsync(relation.FrozenChildDagDefinition, cancellationToken)
                .ConfigureAwait(false);

            var invocationJson = await this.snapshotService
                .LoadAndVerifyAsync(relation.FrozenInvocationInput, cancellationToken)
                .ConfigureAwait(false);

            var input = MaterializeInvocationInput(invocationJson);
            var metadata = BuildMetadata(relation);
            var sharedRunId = string.Concat(SharedRunPrefix, relation.ChildExecutionId);

            var result = await this.sharedRuntimeController
                .SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                        TenantId = relation.TenantId,
                        PipelineKey = relation.ChildDagId,
                        CorrelationId = relation.ChildInvocationKey,
                        Source = "child-dag-composition",
                        Reason = "dispatch-allocated-child-execution",
                        Metadata = metadata,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = relation.ChildDagId,
                            RequestedExecutionId = relation.ChildExecutionId,
                            PipelineDefinitionSnapshot = relation.FrozenChildDagDefinition,
                            PipelineJson = pipelineJson,
                            Input = input,
                            ExecutionContextSnapshot = relation.DelegatedExecutionContextSnapshot,
                            Metadata = metadata
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.Run is null)
            {
                throw new InvalidOperationException(
                    result.FailureReason ??
                    $"Shared runtime controller rejected child execution '{relation.ChildExecutionId}'.");
            }

            if (!string.Equals(result.SharedRunId, sharedRunId, StringComparison.Ordinal) ||
                !string.Equals(result.Run.RunRequest.RequestedExecutionId, relation.ChildExecutionId, StringComparison.Ordinal) ||
                !string.Equals(
                    result.Run.RunRequest.PipelineDefinitionSnapshot?.ContentHash,
                    relation.FrozenChildDagDefinition.ContentHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.Run.RunRequest.PipelineJson, pipelineJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shared runtime dispatch for child execution '{relation.ChildExecutionId}' did not preserve the exact durable identity and definition.");
            }

            return result;
        }

        /// <summary>
        /// Validates that the authoritative relation is ready for child creation/dispatch.
        /// </summary>
        /// <param name="relation">The authoritative relation.</param>
        private static void EnsureDispatchable(AiChildExecutionRelation relation)
        {
            if (relation.Status is not (
                AiChildExecutionRelationStatus.ChildAllocated or
                AiChildExecutionRelationStatus.Waiting))
            {
                throw new InvalidOperationException(
                    $"Child execution cannot be dispatched from relation status '{relation.Status}'.");
            }

            if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
            {
                throw new InvalidOperationException(
                    "Child execution cannot be dispatched before its exact execution identifier is durably allocated.");
            }

            if (string.IsNullOrWhiteSpace(relation.ControlPlaneId))
            {
                throw new InvalidOperationException(
                    "Child execution dispatch requires the durable logical control-plane authority captured with the invocation preparation snapshot.");
            }

            if (relation.DelegatedExecutionContextSnapshot is null)
            {
                throw new InvalidOperationException(
                    "Child execution dispatch requires the durable delegated execution context snapshot.");
            }

            if (!string.Equals(
                    relation.DelegatedExecutionContextSnapshot.TenantId,
                    relation.TenantId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Delegated child execution context tenant does not match the authoritative relation tenant.");
            }
        }

        /// <summary>
        /// Materializes canonical invocation JSON into the runtime input shape already supported by the worker.
        /// </summary>
        /// <param name="canonicalJson">The verified canonical invocation JSON.</param>
        /// <returns>The runtime input object.</returns>
        private static object? MaterializeInvocationInput(string canonicalJson)
        {
            using var document = JsonDocument.Parse(canonicalJson);
            var root = document.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => root.GetString(),
                JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(canonicalJson),
                _ => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [AiExecutionKeys.Input] = root.Clone()
                }
            };
        }

        /// <summary>
        /// Builds deterministic diagnostic metadata for the existing shared queue path.
        /// </summary>
        /// <param name="relation">The authoritative relation.</param>
        /// <returns>The delegated metadata enriched with durable child identities and snapshot hashes.</returns>
        private static IReadOnlyDictionary<string, string> BuildMetadata(AiChildExecutionRelation relation)
        {
            if (string.IsNullOrWhiteSpace(relation.ControlPlaneId))
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' does not contain the durable logical control-plane authority required for dispatch metadata.");
            }

            var metadata = new Dictionary<string, string>(relation.DelegatedMetadata, StringComparer.Ordinal)
            {
                [AiChildDagMetadataKeys.InvocationKey] = relation.ChildInvocationKey,
                [AiChildDagMetadataKeys.InvocationGeneration] = relation.InvocationGeneration.ToString(CultureInfo.InvariantCulture),
                [AiChildDagMetadataKeys.ExecutionId] = relation.ChildExecutionId!,
                [AiChildDagMetadataKeys.DefinitionVersion] = relation.ChildDagDefinitionVersion,
                [AiChildDagMetadataKeys.DefinitionDigest] = relation.FrozenChildDagDefinition.ContentHash ?? string.Empty,
                [AiChildDagMetadataKeys.InputDigest] = relation.FrozenInvocationInput.ContentHash ?? string.Empty,
                [AiChildDagMetadataKeys.ParentExecutionId] = relation.ParentExecutionId,
                [AiChildDagMetadataKeys.ParentCallSiteId] = relation.ParentCallSiteId
            };

            return metadata;
        }
    }
}
