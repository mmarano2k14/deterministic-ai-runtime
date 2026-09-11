using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// One short evaluation through the existing IAiPolicy engine. It owns response
    /// validation, not admission, claims, retries, durable waits or policy events.
    /// Each adapter has its own identity; the shared transport must be concurrency-safe.
    /// </summary>
    [AiPolicyDiscoveryIgnore]
    internal sealed class AiConcurrencyPolicyAdapter : IAiPolicy, IAiPolicyInvocationIdentity
    {
        private readonly IAiConcurrencyPolicyTransport _transport;
        private readonly AiPolicyInvocationBinding _binding;
        private readonly string _tenantId;
        private readonly string? _tenantGroupId;
        private readonly string _executionId;
        private readonly string _stepName;
        private readonly string _stepKey;
        private readonly CancellationToken _executionCancellation;
        private readonly TimeSpan _timeout;
        private readonly string _requestId = Guid.NewGuid().ToString("N");
        private int _started;

        public AiConcurrencyPolicyAdapter(
            IAiConcurrencyPolicyTransport transport, AiPolicyInvocationBinding binding,
            string tenantId, string? tenantGroupId, string executionId, string stepName,
            string stepKey, CancellationToken executionCancellation, TimeSpan timeout)
        {
            _transport = transport;
            _binding = binding;
            _tenantId = tenantId;
            _tenantGroupId = tenantGroupId;
            _executionId = executionId;
            _stepName = stepName;
            _stepKey = stepKey;
            _executionCancellation = executionCancellation;
            _timeout = timeout;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiPolicyInvocationMetadataKeys.RequestId] = _requestId,
                [AiPolicyInvocationMetadataKeys.InvocationKind] = "Custom",
                [AiPolicyInvocationMetadataKeys.ExecutionLanguage] = binding.Invocation.ExecutionLanguage!,
                [AiPolicyInvocationMetadataKeys.ImplementationRef] = binding.Invocation.ImplementationRef!,
                [AiPolicyInvocationMetadataKeys.Scope] = binding.Scope.ToString(),
                [AiPolicyInvocationMetadataKeys.AdapterType] = nameof(AiConcurrencyPolicyAdapter)
            };
            if (binding.OwnerStepName is not null) metadata[AiPolicyInvocationMetadataKeys.OwnerStepName] = binding.OwnerStepName;
            InvocationMetadata = new ReadOnlyDictionary<string, string>(metadata);
        }

        public string Key => _binding.PolicyName;
        public string PolicyName => Key;
        public AiPolicyKind Kind => AiPolicyKind.Concurrency;
        public IReadOnlyDictionary<string, string> InvocationMetadata { get; }

        public async Task<AiPolicyResult> ExecuteAsync(object context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionCancellation.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("A custom policy adapter is single-evaluation; bind a fresh invocation.");
            }
            if (context is not AiConcurrencyPolicyContext policyContext)
            {
                throw new InvalidOperationException("A custom concurrency policy requires its typed concurrency context.");
            }

            var input = policyContext.Concurrency;
            if (input.ExecutionId != _executionId || input.StepId != _stepName || input.StepKey != _stepKey ||
                string.IsNullOrWhiteSpace(input.PipelineKey) || string.IsNullOrWhiteSpace(input.RuntimeInstanceId))
            {
                throw new InvalidOperationException("The policy evaluation context does not match its bound execution and step.");
            }
            var config = AiPolicyJsonSnapshot.Create(policyContext.Config);
            cancellationToken.ThrowIfCancellationRequested();
            _executionCancellation.ThrowIfCancellationRequested();
            var request = new AiConcurrencyPolicyRequest(
                _requestId, Key, _binding.Scope.ToString(), _binding.OwnerStepName,
                _binding.Invocation.ExecutionLanguage!, _binding.Invocation.ImplementationRef!,
                DateTimeOffset.UtcNow.Add(_timeout),
                new AiConcurrencyPolicyInput(
                    _tenantId, _tenantGroupId, _executionId, input.PipelineKey, _stepName,
                    _stepKey, input.RuntimeInstanceId, input.Provider, input.Model, input.Operation),
                config);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _executionCancellation);
            deadline.CancelAfter(_timeout);
            try
            {
                // WaitAsync also bounds an asynchronous transport that ignores its token.
                // It cannot interrupt a transport blocking synchronously before returning.
                var pending = _transport.EvaluateAsync(request, deadline.Token)
                    ?? throw new InvalidOperationException("The custom concurrency policy transport returned no task.");
                var response = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return AiConcurrencyPolicyResponseReader.Read(response, _requestId);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested && !_executionCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Custom concurrency policy evaluation exceeded the server deadline.", ex);
            }
        }
    }
}
