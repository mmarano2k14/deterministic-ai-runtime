using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Contextual hosted adapter for the existing Delegation checkpoint.
    /// The adapter can only return policy evidence; durable relation mutation remains outside this type.
    /// </summary>
    [AiPolicyDiscoveryIgnore]
    internal sealed class AiDelegationPolicyAdapter : IAiPolicy, IAiPolicyInvocationIdentity
    {
        private readonly IAiDelegationPolicyTransport _transport;
        private readonly AiPolicyInvocationBinding _binding;
        private readonly AiConfiguredPolicyDefinition _declaration;
        private readonly string _tenantId;
        private readonly string? _tenantGroupId;
        private readonly string _parentExecutionId;
        private readonly string _parentStepName;
        private readonly CancellationToken _executionCancellation;
        private readonly TimeSpan _timeout;
        private readonly string _requestId = Guid.NewGuid().ToString("N");
        private int _started;

        public AiDelegationPolicyAdapter(
            IAiDelegationPolicyTransport transport,
            AiPolicyInvocationBinding binding,
            AiConfiguredPolicyDefinition declaration,
            string tenantId,
            string? tenantGroupId,
            string parentExecutionId,
            string parentStepName,
            CancellationToken executionCancellation,
            TimeSpan timeout)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
            _tenantId = tenantId;
            _tenantGroupId = tenantGroupId;
            _parentExecutionId = parentExecutionId;
            _parentStepName = parentStepName;
            _executionCancellation = executionCancellation;
            _timeout = timeout;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiPolicyInvocationMetadataKeys.RequestId] = _requestId,
                [AiPolicyInvocationMetadataKeys.InvocationKind] = "Custom",
                [AiPolicyInvocationMetadataKeys.ExecutionLanguage] = binding.Invocation.ExecutionLanguage!,
                [AiPolicyInvocationMetadataKeys.ImplementationRef] = binding.Invocation.ImplementationRef!,
                [AiPolicyInvocationMetadataKeys.Scope] = binding.Scope.ToString(),
                [AiPolicyInvocationMetadataKeys.AdapterType] = nameof(AiDelegationPolicyAdapter)
            };
            if (binding.OwnerStepName is not null)
            {
                metadata[AiPolicyInvocationMetadataKeys.OwnerStepName] = binding.OwnerStepName;
            }

            InvocationMetadata = new ReadOnlyDictionary<string, string>(metadata);
        }

        public string Key => _binding.PolicyName;
        public string PolicyName => Key;
        public AiPolicyKind Kind => AiPolicyKind.Delegation;
        public IReadOnlyDictionary<string, string> InvocationMetadata { get; }

        public async Task<AiPolicyResult> ExecuteAsync(
            object context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionCancellation.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("A custom Delegation policy adapter is single-evaluation.");
            }

            if (context is not AiChildDelegationPolicyContext delegation)
            {
                throw new InvalidOperationException("A custom Delegation policy requires its typed delegation context.");
            }

            var relation = delegation.Relation;
            if (relation.Status != AiChildExecutionRelationStatus.DelegationPolicyPending ||
                relation.ChildExecutionId is not null)
            {
                throw new InvalidOperationException(
                    "Custom Delegation evaluation is valid only while the durable relation is delegation-pending and unallocated.");
            }

            if (!string.Equals(relation.TenantId, _tenantId, StringComparison.Ordinal) ||
                !string.Equals(relation.ParentExecutionId, _parentExecutionId, StringComparison.Ordinal) ||
                !string.Equals(relation.ParentCallSiteId, _parentStepName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Delegation evaluation context does not match its bound tenant, parent execution, and call site.");
            }

            var request = new AiDelegationPolicyRequest(
                _requestId,
                Key,
                _binding.Scope.ToString(),
                _binding.OwnerStepName,
                _binding.Invocation.ExecutionLanguage!,
                _binding.Invocation.ImplementationRef!,
                DateTimeOffset.UtcNow.Add(_timeout),
                new AiDelegationPolicyInput(
                    _tenantId,
                    _tenantGroupId,
                    _parentExecutionId,
                    relation.ParentCallSiteId,
                    relation.ChildDagId,
                    relation.ChildDagDefinitionVersion,
                    relation.ChildInvocationKey,
                    relation.InvocationGeneration),
                AiPolicyJsonSnapshot.Create(_declaration.Config));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _executionCancellation);
            deadline.CancelAfter(_timeout);

            try
            {
                var pending = _transport.EvaluateAsync(request, deadline.Token)
                    ?? throw new InvalidOperationException("The custom Delegation transport returned no task.");
                var response = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();

                var result = AiCustomPolicyFamilyContracts.ReadDelegationV1(response, _requestId);
                return result.Decision switch
                {
                    AiDelegationPolicyTransportDecision.Approve => AiPolicyResult.Success(result.Reason),
                    AiDelegationPolicyTransportDecision.Deny => AiPolicyResult.Block(result.Reason),
                    _ => throw new InvalidOperationException("Unknown Delegation policy transport decision.")
                };
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested &&
                !_executionCancellation.IsCancellationRequested &&
                deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Custom Delegation policy evaluation exceeded the server deadline.",
                    ex);
            }
        }
    }
}
