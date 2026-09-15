using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>Creates fresh contextual Delegation adapters from already compiled bindings.</summary>
    public sealed class AiDelegationPolicyAdapterFactory
    {
        private readonly IReadOnlyDictionary<string, IAiDelegationPolicyTransport> _transports;
        private readonly TimeSpan _timeout;

        public AiDelegationPolicyAdapterFactory(
            IEnumerable<IAiDelegationPolicyTransport> transports,
            AiDelegationPolicyInvocationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(transports);
            _timeout = (options ?? new AiDelegationPolicyInvocationOptions()).EvaluationTimeout;
            if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            var map = new Dictionary<string, IAiDelegationPolicyTransport>(StringComparer.Ordinal);
            foreach (var transport in transports)
            {
                ArgumentNullException.ThrowIfNull(transport);
                if (transport.ExecutionLanguage is not (
                    AiExecutionLanguages.DotNet or
                    AiExecutionLanguages.Python or
                    AiExecutionLanguages.TypeScript))
                {
                    throw new InvalidOperationException(
                        "A Delegation policy transport requires a canonical execution language.");
                }

                if (!map.TryAdd(transport.ExecutionLanguage, transport))
                {
                    throw new InvalidOperationException(
                        $"Multiple Delegation policy transports are registered for '{transport.ExecutionLanguage}'.");
                }
            }

            _transports = map;
        }

        public IAiPolicy Bind(
            AiStepExecutionContext context,
            AiConfiguredPolicyDefinition declaration,
            AiPolicyInvocationBinding binding)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(binding);

            var invocation = binding.Invocation;
            if (!string.Equals(context.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal) ||
                declaration.Name != binding.PolicyName ||
                declaration.Invocation?.Kind != AiInvocationKind.Custom ||
                invocation.Kind != AiInvocationKind.Custom ||
                (declaration.Kind is not null &&
                    !declaration.Kind.Equals("Delegation", StringComparison.OrdinalIgnoreCase)) ||
                declaration.Invocation.ImplementationRef != invocation.ImplementationRef ||
                string.IsNullOrWhiteSpace(invocation.ImplementationRef) ||
                invocation.ExecutionLanguage is not (
                    AiExecutionLanguages.DotNet or
                    AiExecutionLanguages.Python or
                    AiExecutionLanguages.TypeScript) ||
                (binding.Scope == AiPolicyBindingScope.Step && binding.OwnerStepName != context.StepName) ||
                (binding.Scope == AiPolicyBindingScope.Pipeline && binding.OwnerStepName is not null))
            {
                throw new InvalidOperationException(
                    "Custom Delegation declaration does not match its compiled invocation binding.");
            }

            if (!_transports.TryGetValue(invocation.ExecutionLanguage!, out var transport))
            {
                throw new NotSupportedException(
                    $"No custom Delegation policy transport is installed for '{invocation.ExecutionLanguage}'; native fallback is forbidden.");
            }

            var snapshot = context.Record.ExecutionContextSnapshot;
            string? tenantId = snapshot?.TenantId;
            string? tenantGroupId = snapshot?.TenantGroupId;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                var accessor = context.Services.GetService(typeof(IExecutionContextAccessor)) as IExecutionContextAccessor;
                tenantId = accessor?.Current?.TenantId;
                tenantGroupId = accessor?.Current?.TenantGroupId;
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new InvalidOperationException(
                    "Custom Delegation evaluation requires a trusted runtime tenant context.");
            }

            return new AiDelegationPolicyAdapter(
                transport,
                binding,
                declaration,
                tenantId,
                tenantGroupId,
                context.ExecutionId,
                context.StepName,
                context.CancellationToken,
                _timeout);
        }
    }
}
