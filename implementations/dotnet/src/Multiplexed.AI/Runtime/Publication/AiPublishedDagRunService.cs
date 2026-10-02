using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Creation;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Pins the entire publication before invoking the existing exact DAG creator. It does
    /// not enqueue/start a run, resolve a latest definition or add an execution engine.
    /// </summary>
    public sealed class AiPublishedDagRunService
    {
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _store;
        private readonly IAiDagExecutionEngineServices _engine;
        public AiPublishedDagRunService(AiPublicationIdentity identity, AiPublicationOptions options,
            AiImmutablePublicationStore store, IAiDagExecutionEngineServices engine)
        { _identity = identity; _options = options; _store = store; _engine = engine; }

        public async Task<AiExecutionRecord> CreateAsync(AiDurableInvocationScope scope, string runKey,
            string publicationRef, string inputsJson, CancellationToken cancellationToken = default)
        {
            var result = await CreateWithDefinitionAsync(
                scope, runKey, publicationRef, inputsJson, cancellationToken).ConfigureAwait(false);
            return result.Record;
        }

        public async Task<(AiExecutionRecord Record, AiPipelineDefinition Definition)> CreateWithDefinitionAsync(
            AiDurableInvocationScope scope, string runKey, string publicationRef, string inputsJson,
            CancellationToken cancellationToken = default)
        {
            AiPublicationJson.Text(runKey, nameof(runKey));
            var inputs = AiDurableInvocationJson.Normalize(inputsJson, true);
            var guard = await _identity.AuthorizeAsync(scope, _options.Execute, cancellationToken).ConfigureAwait(false);
            var executionId = AiPublicationJson.ExecutionId(guard.Partition, runKey);
            var frozen = await _store.ReadVerifiedAsync(publicationRef, guard, cancellationToken).ConfigureAwait(false);
            var publication = frozen.Publication;
            var descriptor = publication.Manifest.Definition;
            var pin = new AiPublicationRunPin(1, guard.Partition, runKey, executionId, guard.UserId,
                publicationRef, publication.PublicationSha256, descriptor.Sha256, inputs, AiPublicationJson.Hash(inputs));
            var existing = _engine.DagStore is not null
                ? await _engine.DagStore.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                : await _engine.Store.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false);
            guard.RequireCurrent();
            var priorPin = await _store.ReadPinAsync(executionId, guard, cancellationToken).ConfigureAwait(false);
            if (existing is not null && priorPin is null)
                throw new InvalidOperationException("An existing published execution has lost its immutable run pin; readmission cannot recreate it.");
            if (priorPin is not null && priorPin != pin)
                throw new InvalidOperationException("The run key already identifies different publication content or inputs.");
            // First-writer immutable storage arbitrates concurrent admissions before any DAG side effect.
            if (priorPin is null)
                await _store.SavePinAsync(pin, guard, cancellationToken).ConfigureAwait(false);
            guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
            if (existing is not null)
            {
                RequireRecord(existing, pin);
                var state = _engine.DagStore is not null
                    ? await _engine.DagStore.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false)
                    : await _engine.Store.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false);
                guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
                if (state is null || state.ExecutionId != executionId || state.PipelineName != frozen.Definition.Name ||
                    existing.PipelineName != frozen.Definition.Name ||
                    !state.Metadata.TryGetValue("pipeline.definition.version", out var version) ||
                    version?.ToString() != frozen.Definition.Version ||
                    !existing.Steps.SequenceEqual(frozen.Definition.Steps.OrderBy(s => s.Order).Select(s => s.Name), StringComparer.Ordinal))
                    throw new InvalidOperationException("Existing published execution record/state pair is inconsistent.");
                // Do not reseed the RBAC context or reinitialize state for an idempotent admission.
                return (existing, frozen.Definition);
            }
            var record = await new AiDagExecutionCreator(_engine).CreateIfAbsentAsync(executionId, frozen.Definition,
                AiStoredPayload.Artifact(descriptor.Key, descriptor.Sha256, descriptor.SizeBytes, "application/json"),
                AiPublicationJson.Read<Dictionary<string, object?>>(inputs), cancellationToken).ConfigureAwait(false);
            guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
            RequireRecord(record, pin);
            return (record, frozen.Definition);
        }

        public async Task<AiPublicationRunPin> ReadPinAsync(
            AiDurableInvocationScope scope,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            AiPublicationJson.Text(executionId, nameof(executionId));
            var guard = await _identity.AuthorizeAsync(scope, _options.Read, cancellationToken).ConfigureAwait(false);
            var pin = await _store.ReadPinAsync(executionId, guard, cancellationToken).ConfigureAwait(false);
            guard.RequireCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            return pin ?? throw new KeyNotFoundException($"Published execution '{executionId}' was not found.");
        }

        private static void RequireRecord(AiExecutionRecord record, AiPublicationRunPin pin)
        {
            var owner = record.ExecutionContextSnapshot;
            if (record.ExecutionId != pin.ExecutionId || record.ExecutionMode != AiExecutionMode.Dag ||
                record.PipelineDefinitionSnapshot?.ContentHash != pin.DefinitionSha256 || owner is null ||
                owner.TenantId != pin.Partition.Scope.TenantId || owner.TenantGroupId != pin.Partition.Scope.TenantGroupId ||
                owner.Project != pin.Partition.Project || owner.CurrentNamespace != pin.Partition.Namespace || owner.UserId != pin.UserId)
                throw new InvalidOperationException("Existing DAG execution does not match the immutable publication admission.");
        }
    }
}
