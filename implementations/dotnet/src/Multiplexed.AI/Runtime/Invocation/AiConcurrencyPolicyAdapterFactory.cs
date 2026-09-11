using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Opt-in server capability map for the concurrency checkpoint. It stores transports
    /// by language, never tenant policies by name, and creates a fresh immutable adapter
    /// for each evaluation. Registration alone does not install a hosted worker.
    /// </summary>
    public sealed class AiConcurrencyPolicyAdapterFactory
    {
        private readonly IReadOnlyDictionary<string, IAiConcurrencyPolicyTransport> _transports;
        private readonly TimeSpan _timeout;

        public AiConcurrencyPolicyAdapterFactory(
            IEnumerable<IAiConcurrencyPolicyTransport> transports,
            AiConcurrencyPolicyInvocationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(transports);
            _timeout = (options ?? new AiConcurrencyPolicyInvocationOptions()).EvaluationTimeout;
            if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Policy timeout must be positive and at most 30 seconds.");
            }

            var installed = new Dictionary<string, IAiConcurrencyPolicyTransport>(StringComparer.Ordinal);
            foreach (var transport in transports)
            {
                ArgumentNullException.ThrowIfNull(transport);
                var language = transport.ExecutionLanguage;
                if (language is not (AiExecutionLanguages.DotNet or AiExecutionLanguages.Python or AiExecutionLanguages.TypeScript))
                {
                    throw new InvalidOperationException("A concurrency policy transport requires a canonical execution language.");
                }
                if (!installed.TryAdd(language, transport))
                {
                    throw new InvalidOperationException($"Multiple concurrency policy transports are registered for '{language}'.");
                }
            }
            _transports = installed;
        }

        /// <summary>
        /// Binds already compiled metadata. No language re-resolution, policy evaluation,
        /// worker startup, external call or store read occurs here. Tenant scalars are
        /// copied from the record, or the runtime's restored execution-scoped accessor
        /// when admission uses its historical synthetic record. Missing identity fails.
        /// </summary>
        public IAiPolicy Bind(
            AiStepExecutionContext stepContext,
            AiConfiguredPolicyDefinition declaration,
            AiPolicyInvocationBinding binding)
        {
            ArgumentNullException.ThrowIfNull(stepContext);
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(binding);
            ValidateBinding(stepContext, declaration, binding);

            if (!_transports.TryGetValue(binding.Invocation.ExecutionLanguage!, out var transport))
            {
                throw new NotSupportedException(
                    $"No custom concurrency policy transport is installed for '{binding.Invocation.ExecutionLanguage}'; native fallback is forbidden.");
            }

            var snapshot = stepContext.Record.ExecutionContextSnapshot;
            string? tenantId;
            string? tenantGroupId;
            if (snapshot is not null)
            {
                tenantId = snapshot.TenantId;
                tenantGroupId = snapshot.TenantGroupId;
            }
            else
            {
                // The DAG runners restore this AsyncLocal context before admission. Do
                // not invent a tenant from pipeline config or cache a mutable accessor.
                var accessor = stepContext.Services.GetService(typeof(IExecutionContextAccessor)) as IExecutionContextAccessor;
                var current = accessor?.Current;
                tenantId = current?.TenantId;
                tenantGroupId = current?.TenantGroupId;
            }
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new InvalidOperationException("Custom policy evaluation requires a trusted runtime tenant context.");
            }

            return new AiConcurrencyPolicyAdapter(
                transport, binding, tenantId, tenantGroupId,
                stepContext.ExecutionId, stepContext.StepName, stepContext.StepKey,
                stepContext.CancellationToken, _timeout);
        }

        private static void ValidateBinding(
            AiStepExecutionContext context,
            AiConfiguredPolicyDefinition declaration,
            AiPolicyInvocationBinding binding)
        {
            if (context.Record.ExecutionMode != AiExecutionMode.Dag || context.ConcurrencyAdmissionDefinition is null)
            {
                throw new NotSupportedException("Custom concurrency policies require the explicit DAG admission checkpoint.");
            }

            var invocation = binding.Invocation;
            if (string.IsNullOrWhiteSpace(declaration.Name) || declaration.Name != binding.PolicyName ||
                declaration.Invocation?.Kind != AiInvocationKind.Custom || invocation.Kind != AiInvocationKind.Custom ||
                (declaration.Kind is not null && !string.Equals(declaration.Kind, "Concurrency", StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(invocation.ImplementationRef) ||
                declaration.Invocation.ImplementationRef != invocation.ImplementationRef ||
                declaration.Invocation.ConnectionRef is not null || declaration.Invocation.Tool is not null ||
                invocation.ConnectionRef is not null || invocation.Tool is not null ||
                invocation.ExecutionLanguage is not (AiExecutionLanguages.DotNet or AiExecutionLanguages.Python or AiExecutionLanguages.TypeScript) ||
                invocation.LanguageSource is not (AiExecutionLanguageSource.Pipeline or AiExecutionLanguageSource.Step or AiExecutionLanguageSource.Policy) ||
                !Enum.IsDefined(binding.Scope) ||
                (binding.Scope == AiPolicyBindingScope.Pipeline && binding.OwnerStepName is not null) ||
                (binding.Scope == AiPolicyBindingScope.Step && binding.OwnerStepName != context.StepName) ||
                (binding.Scope == AiPolicyBindingScope.Pipeline && invocation.LanguageSource == AiExecutionLanguageSource.Step) ||
                (declaration.ExecutionLanguage is not null &&
                    (declaration.ExecutionLanguage != invocation.ExecutionLanguage || invocation.LanguageSource != AiExecutionLanguageSource.Policy)) ||
                (declaration.ExecutionLanguage is null && invocation.LanguageSource == AiExecutionLanguageSource.Policy) ||
                (binding.Scope == AiPolicyBindingScope.Step && declaration.ExecutionLanguage is null &&
                    ((context.InvocationBinding.Kind == AiInvocationKind.Custom &&
                        (context.InvocationBinding.ExecutionLanguage != invocation.ExecutionLanguage ||
                         context.InvocationBinding.LanguageSource != invocation.LanguageSource)) ||
                     (context.InvocationBinding.Kind != AiInvocationKind.Custom && invocation.LanguageSource == AiExecutionLanguageSource.Step))))
            {
                throw new InvalidOperationException("Custom policy declaration does not match its compiled invocation binding.");
            }
        }
    }
}
