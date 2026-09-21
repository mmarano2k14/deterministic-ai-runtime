using System.Diagnostics;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>Executes one delegation/v1 policy through the existing hosted worker transport.</summary>
    public sealed class AiHostedDelegationPolicyTransport : IAiDelegationPolicyTransport
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly IAiDelegationPolicyCodePreparer _preparer;
        private readonly IAiWorkerInvocationTransport _transport;
        private readonly TimeProvider _time;

        public AiHostedDelegationPolicyTransport(
            string executionLanguage,
            IAiDelegationPolicyCodePreparer preparer,
            IAiWorkerInvocationTransport transport,
            TimeProvider? timeProvider = null)
        {
            if (!AiExecutionLanguages.IsSupported(executionLanguage))
            {
                throw new ArgumentOutOfRangeException(nameof(executionLanguage));
            }

            ExecutionLanguage = executionLanguage;
            _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string ExecutionLanguage { get; }

        public async Task<JsonElement> EvaluateAsync(
            AiDelegationPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (request.ExecutionLanguage != ExecutionLanguage)
            {
                throw new InvalidOperationException(
                    "Hosted Delegation transport language does not match the resolved policy binding.");
            }

            if (request.DeadlineUtc.Offset != TimeSpan.Zero || request.DeadlineUtc <= _time.GetUtcNow())
            {
                throw new TimeoutException("Custom Delegation policy deadline has already elapsed.");
            }

            var code = await _preparer.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            if (code.Target.ExecutionLanguage != ExecutionLanguage ||
                code.Runtime.ExecutionLanguage != ExecutionLanguage ||
                code.Target.ImplementationRef != request.ImplementationRef)
            {
                throw new InvalidOperationException(
                    "Hosted Delegation preparation substituted language or implementation.");
            }

            var operationId = "delegation-policy-" + request.RequestId;
            var workerId = "delegation-policy-" + Guid.NewGuid().ToString("N");
            var invocation = new AiWorkerInvocationRequest(
                1,
                "invoke",
                request.RequestId,
                operationId,
                operationId,
                workerId,
                1,
                request.Context.TenantId,
                request.Context.ParentExecutionId,
                request.Context.ParentCallSiteId,
                0,
                request.DeadlineUtc,
                Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null,
                JsonSerializer.SerializeToElement(request, Json),
                code);

            var result = await _transport
                .InvokeAsync(
                    invocation,
                    static token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    "Hosted custom Delegation policy execution returned a technical failure result.");
            }

            using var document = JsonDocument.Parse(
                result.PayloadJson,
                new JsonDocumentOptions { MaxDepth = 36 });
            return document.RootElement.Clone();
        }
    }
}
