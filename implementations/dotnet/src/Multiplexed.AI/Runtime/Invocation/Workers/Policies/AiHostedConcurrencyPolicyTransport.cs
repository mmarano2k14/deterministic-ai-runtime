using System.Diagnostics;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Executes one short custom concurrency policy through the existing hosted worker
    /// process transport. It is deliberately non-durable: policy evaluation is side-effect-free,
    /// bounded by the admission deadline, and never creates a journal lease or DAG continuation.
    /// </summary>
    public sealed class AiHostedConcurrencyPolicyTransport : IAiConcurrencyPolicyTransport
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly IAiConcurrencyPolicyCodePreparer _preparer;
        private readonly IAiWorkerInvocationTransport _transport;
        private readonly TimeProvider _time;

        public AiHostedConcurrencyPolicyTransport(
            string executionLanguage,
            IAiConcurrencyPolicyCodePreparer preparer,
            IAiWorkerInvocationTransport transport,
            TimeProvider? timeProvider = null)
        {
            if (executionLanguage is not (AiExecutionLanguages.DotNet or AiExecutionLanguages.Python or AiExecutionLanguages.TypeScript))
            {
                throw new ArgumentOutOfRangeException(nameof(executionLanguage), "A canonical hosted execution language is required.");
            }
            ExecutionLanguage = executionLanguage;
            _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string ExecutionLanguage { get; }

        public async Task<JsonElement> EvaluateAsync(
            AiConcurrencyPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExecutionLanguage != ExecutionLanguage)
            {
                throw new InvalidOperationException("The hosted policy transport language does not match the resolved policy binding.");
            }
            if (request.DeadlineUtc.Offset != TimeSpan.Zero || request.DeadlineUtc <= _time.GetUtcNow())
            {
                throw new TimeoutException("Custom policy evaluation deadline has already elapsed.");
            }

            var code = await _preparer.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (code.Target.ExecutionLanguage != ExecutionLanguage || code.Runtime.ExecutionLanguage != ExecutionLanguage ||
                code.Target.ImplementationRef != request.ImplementationRef)
            {
                throw new InvalidOperationException("Hosted policy preparation substituted the resolved language or implementation.");
            }

            var operationId = "policy-eval-" + request.RequestId;
            var workerId = "policy-" + Guid.NewGuid().ToString("N");
            var inputs = JsonSerializer.SerializeToElement(request, Json);
            var invocation = new AiWorkerInvocationRequest(
                1,
                "invoke",
                request.RequestId,
                operationId,
                operationId,
                workerId,
                1,
                request.Context.TenantId,
                request.Context.ExecutionId,
                request.Context.StepName,
                0,
                request.DeadlineUtc,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null,
                inputs,
                code);

            var result = await _transport.InvokeAsync(
                invocation,
                static token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                throw new InvalidOperationException("Hosted custom policy execution returned a technical failure result.");
            }

            using var document = JsonDocument.Parse(result.PayloadJson, new JsonDocumentOptions { MaxDepth = 36 });
            return document.RootElement.Clone();
        }
    }
}
