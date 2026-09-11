using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>
    /// Uses the existing DAG executor: prepare once, Park while pending, return the saved
    /// result on re-entry. No native discovery attribute and no business retry/worker loop.
    /// </summary>
    public sealed class AiDurableInvocationStepAdapter : IAiStep
    {
        private readonly AiStepInvocationAdapterContext _metadata;
        internal AiDurableInvocationStepAdapter(AiStepInvocationAdapterContext metadata) => _metadata = metadata;
        public string Name => _metadata.StepName;

        public async Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            if (context.Record.ExecutionMode != AiExecutionMode.Dag || context.Record.IsTerminal ||
                context.ConcurrencyAdmissionDefinition is not null || context.InvocationBinding != _metadata.Binding ||
                context.StepName != Name || context.StepKey != _metadata.StepKey ||
                context.Record.PipelineName != _metadata.PipelineName || context.State.ExecutionId != context.ExecutionId ||
                !context.State.Steps.TryGetValue(Name, out var currentStep) || currentStep.Status != AiStepExecutionStatus.Running)
                throw new InvalidOperationException("A durable adapter executes only its claimed DAG call site, never admission or sequential work.");

            var scope = await AiDurableInvocationDagIdentity.CaptureAsync(context, token).ConfigureAwait(false);
            var pinned = await context.Services.GetRequiredService<AiDurableInvocationDagBinding>()
                .ReadAsync(context.Record, scope, Name, token).ConfigureAwait(false);
            if (pinned.PipelineVersion != _metadata.PipelineVersion || pinned.StepKey != _metadata.StepKey ||
                pinned.ImplementationRef != _metadata.Binding.ImplementationRef || pinned.ExecutionLanguage != _metadata.Binding.ExecutionLanguage)
                throw new InvalidOperationException("The executable adapter does not match the pinned durable definition.");
            var identity = new AiDurableInvocationIdentity(scope.TenantId, context.ExecutionId, Name);
            var journal = context.Services.GetRequiredService<AiDurableInvocationJournal>();
            var record = await journal.GetAsync(scope, identity, token).ConfigureAwait(false);
            if (record is null)
            {
                var target = await context.Services.GetRequiredService<IAiDurableInvocationTargetResolver>()
                    .ResolveAsync(pinned, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The exact authorized publication material is unavailable.");
                AiDurableInvocationDagBinding.RequireTarget(pinned, target);
                AiDurableInvocationDagIdentity.RequireCurrent(context);
                var inputs = await context.GetHelper().GetResolvedInputsAsync(
                    includeReservedVariables: false, cancellationToken: token).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(inputs);
                AiDurableInvocationDagIdentity.RequireCurrent(context);
                record = await journal.PrepareAsync(new AiDurableInvocationDefinition(identity, scope, target, json), token)
                    .ConfigureAwait(false);
            }
            // Recovery reads the frozen inputs and target, not the current inputs or latest publication.
            AiDurableInvocationDagBinding.RequireTarget(pinned, record.Definition.Target);
            AiDurableInvocationDagIdentity.RequireCurrent(context);
            token.ThrowIfCancellationRequested();
            if (record.ContinuationStatus == AiDurableInvocationContinuationStatus.Suppressed)
                throw new InvalidOperationException("A suppressed invocation cannot be reactivated.");
            return AiDurableInvocationValidation.Terminal(record)
                ? AiDurableInvocationResultMapper.Map(record)
                : AiStepResult.Park("Waiting for the durable custom invocation result.");
        }
    }
}
