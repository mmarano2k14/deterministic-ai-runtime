using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.AI.Retry;

namespace Multiplexed.AI.Runtime.Invocation
{
    [AiPolicyDiscoveryIgnore]
    internal sealed class AiRetryPolicyAdapter : IAiPolicy, IAiPolicyInvocationIdentity
    {
        private readonly IAiRetryPolicyTransport _transport;
        private readonly AiPolicyInvocationBinding _binding;
        private readonly AiConfiguredPolicyDefinition _declaration;
        private readonly string _tenantId; private readonly string? _tenantGroupId; private readonly string _executionId;
        private readonly string _pipelineKey; private readonly string _stepName; private readonly string _stepKey;
        private readonly CancellationToken _executionCancellation; private readonly TimeSpan _timeout;
        private readonly string _requestId = Guid.NewGuid().ToString("N"); private int _started;

        public AiRetryPolicyAdapter(IAiRetryPolicyTransport transport, AiPolicyInvocationBinding binding, AiConfiguredPolicyDefinition declaration,
            string tenantId, string? tenantGroupId, string executionId, string pipelineKey, string stepName, string stepKey,
            CancellationToken executionCancellation, TimeSpan timeout)
        {
            _transport=transport; _binding=binding; _declaration=declaration; _tenantId=tenantId; _tenantGroupId=tenantGroupId;
            _executionId=executionId; _pipelineKey=pipelineKey; _stepName=stepName; _stepKey=stepKey; _executionCancellation=executionCancellation; _timeout=timeout;
            var metadata = new Dictionary<string,string>(StringComparer.Ordinal)
            {
                [AiPolicyInvocationMetadataKeys.RequestId]=_requestId, [AiPolicyInvocationMetadataKeys.InvocationKind]="Custom",
                [AiPolicyInvocationMetadataKeys.ExecutionLanguage]=binding.Invocation.ExecutionLanguage!,
                [AiPolicyInvocationMetadataKeys.ImplementationRef]=binding.Invocation.ImplementationRef!,
                [AiPolicyInvocationMetadataKeys.Scope]=binding.Scope.ToString(), [AiPolicyInvocationMetadataKeys.AdapterType]=nameof(AiRetryPolicyAdapter)
            };
            if (binding.OwnerStepName is not null) metadata[AiPolicyInvocationMetadataKeys.OwnerStepName]=binding.OwnerStepName;
            InvocationMetadata=new ReadOnlyDictionary<string,string>(metadata);
        }
        public string Key=>_binding.PolicyName; public string PolicyName=>Key; public AiPolicyKind Kind=>AiPolicyKind.Retry;
        public IReadOnlyDictionary<string,string> InvocationMetadata { get; }

        public async Task<AiPolicyResult> ExecuteAsync(object context, CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested(); _executionCancellation.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _started,1)!=0) throw new InvalidOperationException("A custom Retry policy adapter is single-evaluation.");
            if (context is not AiRetryContext retry) throw new InvalidOperationException("A custom Retry policy requires its typed retry context.");
            if (retry.ExecutionId!=_executionId || retry.StepId!=_stepName || retry.StepKey!=_stepKey)
                throw new InvalidOperationException("Retry evaluation context does not match its bound execution and step.");
            var request = new AiRetryPolicyRequest(_requestId, Key, _binding.Scope.ToString(), _binding.OwnerStepName,
                _binding.Invocation.ExecutionLanguage!, _binding.Invocation.ImplementationRef!, DateTimeOffset.UtcNow.Add(_timeout),
                new AiRetryPolicyInput(_tenantId,_tenantGroupId,_executionId,_pipelineKey,_stepName,_stepKey,retry.RetryCount,retry.MaxRetries,
                    retry.FailureReason,retry.Exception?.GetType().FullName,retry.FailedAtUtc), AiPolicyJsonSnapshot.Create(_declaration.Config));
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_executionCancellation); deadline.CancelAfter(_timeout);
            try
            {
                var pending=_transport.EvaluateAsync(request,deadline.Token) ?? throw new InvalidOperationException("The custom Retry transport returned no task.");
                var response=await pending.WaitAsync(deadline.Token).ConfigureAwait(false); deadline.Token.ThrowIfCancellationRequested();
                var result=AiCustomPolicyFamilyContracts.ReadRetryV1(response,_requestId);
                return result.Decision switch
                {
                    AiRetryPolicyTransportDecision.Pass => AiPolicyResult.Success(result.Reason),
                    AiRetryPolicyTransportDecision.Retry => AiPolicyResult.Retry(new AiRetryPolicyOutcome { IsRetryable=true, Reason=result.Reason, SuggestedDelay=result.SuggestedDelay }, result.Reason),
                    AiRetryPolicyTransportDecision.Stop => AiPolicyResult.Block(new AiRetryPolicyOutcome { IsRetryable=false, Reason=result.Reason }, result.Reason),
                    _ => throw new InvalidOperationException("Unknown Retry policy transport decision.")
                };
            }
            catch(OperationCanceledException ex) when(!cancellationToken.IsCancellationRequested && !_executionCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("Custom Retry policy evaluation exceeded the server deadline.",ex); }
        }
    }
}
