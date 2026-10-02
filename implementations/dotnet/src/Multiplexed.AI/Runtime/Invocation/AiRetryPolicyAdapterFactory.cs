using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>Creates fresh contextual Retry adapters from already compiled bindings.</summary>
    public sealed class AiRetryPolicyAdapterFactory
    {
        private readonly IReadOnlyDictionary<string,IAiRetryPolicyTransport> _transports; private readonly TimeSpan _timeout; private readonly TimeProvider _timeProvider;
        public AiRetryPolicyAdapterFactory(IEnumerable<IAiRetryPolicyTransport> transports, AiRetryPolicyInvocationOptions? options=null, TimeProvider? timeProvider=null)
        {
            ArgumentNullException.ThrowIfNull(transports); _timeout=(options??new()).EvaluationTimeout; _timeProvider=timeProvider??TimeProvider.System;
            if (_timeout<=TimeSpan.Zero || _timeout>TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(options));
            var map=new Dictionary<string,IAiRetryPolicyTransport>(StringComparer.Ordinal);
            foreach(var transport in transports)
            {
                ArgumentNullException.ThrowIfNull(transport);
                if (!AiExecutionLanguages.IsSupported(transport.ExecutionLanguage))
                    throw new InvalidOperationException("A Retry policy transport requires a canonical execution language.");
                if(!map.TryAdd(transport.ExecutionLanguage,transport)) throw new InvalidOperationException($"Multiple Retry policy transports are registered for '{transport.ExecutionLanguage}'.");
            }
            _transports=map;
        }
        public IAiPolicy Bind(AiStepExecutionContext context, AiConfiguredPolicyDefinition declaration, AiPolicyInvocationBinding binding)
        {
            ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(declaration); ArgumentNullException.ThrowIfNull(binding);
            var invocation=binding.Invocation;
            if (declaration.Name!=binding.PolicyName || declaration.Invocation?.Kind!=AiInvocationKind.Custom || invocation.Kind!=AiInvocationKind.Custom ||
                (declaration.Kind is not null && !declaration.Kind.Equals("Retry",StringComparison.OrdinalIgnoreCase)) ||
                declaration.Invocation.ImplementationRef!=invocation.ImplementationRef || string.IsNullOrWhiteSpace(invocation.ImplementationRef) ||
                !AiExecutionLanguages.IsSupported(invocation.ExecutionLanguage) ||
                (binding.Scope==AiPolicyBindingScope.Step && binding.OwnerStepName!=context.StepName) ||
                (binding.Scope==AiPolicyBindingScope.Pipeline && binding.OwnerStepName is not null))
                throw new InvalidOperationException("Custom Retry declaration does not match its compiled invocation binding.");
            if(!_transports.TryGetValue(invocation.ExecutionLanguage!,out var transport))
                throw new NotSupportedException($"No custom Retry policy transport is installed for '{invocation.ExecutionLanguage}'; native fallback is forbidden.");
            var snapshot=context.Record.ExecutionContextSnapshot; string? tenant=snapshot?.TenantId; string? group=snapshot?.TenantGroupId;
            if(string.IsNullOrWhiteSpace(tenant))
            { var accessor=context.Services.GetService(typeof(IExecutionContextAccessor)) as IExecutionContextAccessor; tenant=accessor?.Current?.TenantId; group=accessor?.Current?.TenantGroupId; }
            if(string.IsNullOrWhiteSpace(tenant)) throw new InvalidOperationException("Custom Retry evaluation requires a trusted runtime tenant context.");
            var pipelineKey=context.State.PipelineName ?? context.StepName;
            return new AiRetryPolicyAdapter(transport,binding,declaration,tenant,group,context.ExecutionId,pipelineKey,context.StepName,context.StepKey,context.CancellationToken,_timeout,_timeProvider);
        }
    }
}
