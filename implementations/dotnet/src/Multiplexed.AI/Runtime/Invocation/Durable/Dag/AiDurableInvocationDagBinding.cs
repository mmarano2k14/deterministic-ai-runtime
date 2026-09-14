using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Verifies the existing pinned definition; does not publish code or maintain a tenant cache.</summary>
    public sealed class AiDurableInvocationDagBinding
    {
        private readonly AiImmutableJsonPayloadReader _reader;
        public AiDurableInvocationDagBinding(AiImmutableJsonPayloadReader reader) =>
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

        public async Task<AiDurableInvocationTargetRequest> ReadAsync(
            AiExecutionRecord record, AiDurableInvocationScope scope, string stepName,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            AiDurableInvocationValidation.ValidateScope(scope);
            var snapshot = record.ExecutionContextSnapshot;
            if (record.ExecutionMode != AiExecutionMode.Dag || string.IsNullOrWhiteSpace(record.ExecutionId) ||
                snapshot?.TenantId != scope.TenantId || snapshot.TenantGroupId != scope.TenantGroupId)
                throw new InvalidOperationException("Durable invocation requires the exact tenant-owned DAG execution.");
            var descriptor = record.PipelineDefinitionSnapshot
                ?? throw new InvalidOperationException("Durable custom invocation requires an execution-bound immutable definition.");
            var json = await _reader.LoadAndVerifyAsync(descriptor, cancellationToken).ConfigureAwait(false);
            var definition = AiCanonicalJson.Deserialize<AiPipelineDefinition>(json);
            if (definition.ExecutionMode != AiExecutionMode.Dag || definition.Name != record.PipelineName ||
                string.IsNullOrWhiteSpace(definition.Version))
                throw new InvalidOperationException("The pinned definition does not match the durable DAG execution.");
            var matches = definition.Steps.Where(item => item.Name == stepName).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The pinned definition must contain exactly one matching call site.");
            var step = matches[0];
            var binding = new AiInvocationBindingResolver().ResolveStep(definition, step);
            if (binding.Kind != AiInvocationKind.Custom || string.IsNullOrWhiteSpace(binding.ImplementationRef))
                throw new InvalidOperationException("The durable operation does not belong to a pinned custom call site.");
            return new AiDurableInvocationTargetRequest(scope, record.ExecutionId,
                descriptor.ContentHash!.ToLowerInvariant(), definition.Name, definition.Version, step.Name,
                step.StepKey, binding.ImplementationRef, binding.ExecutionLanguage!);
        }

        internal static void RequireTarget(AiDurableInvocationTargetRequest request, AiDurableInvocationTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target.PipelineName != request.PipelineName || target.PipelineVersion != request.PipelineVersion ||
                target.DefinitionSha256 != request.DefinitionSha256 || target.ImplementationRef != request.ImplementationRef ||
                target.ExecutionLanguage != request.ExecutionLanguage)
                throw new InvalidOperationException("Publication material does not match the exact pinned call site.");
        }
    }
}
