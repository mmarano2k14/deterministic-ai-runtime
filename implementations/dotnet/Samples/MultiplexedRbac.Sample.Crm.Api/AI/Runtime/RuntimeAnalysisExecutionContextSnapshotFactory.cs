using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext =
    Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisExecutionContextSnapshotFactory :
        IExecutionContextSnapshotProvider
    {
        private readonly IExecutionContextAccessor _executionContextAccessor;

        public RuntimeAnalysisExecutionContextSnapshotFactory(
            IExecutionContextAccessor executionContextAccessor)
        {
            _executionContextAccessor =
                executionContextAccessor
                ?? throw new ArgumentNullException(
                    nameof(executionContextAccessor));
        }

        public ExecutionContextSnapshot Create()
        {
            var current = _executionContextAccessor.Current
                ?? throw new InvalidOperationException(
                    "No RBAC execution context is available for runtime analysis.");

            ValidateRequiredContext(
                current);

            return new ExecutionContextSnapshot
            {
                ContextKey = current.ContextKey,
                Project = current.Project,
                UserId = current.UserId,
                TenantId = current.TenantId,
                TenantGroupId = current.TenantGroupId,
                CurrentNamespace = current.CurrentNamespace,
                Namespaces = current.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = current.InFlightCount,
                TtlSeconds = current.TtlSeconds,
                CreatedAtUtc = current.CreatedAtUtc
            };
        }

        public ExecutionContextSnapshot MapToSnapshot()
        {
            return Create();
        }

        private static void ValidateRequiredContext(
            RbacExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            RequireValue(
                context.ContextKey,
                nameof(context.ContextKey));
            RequireValue(
                context.Project,
                nameof(context.Project));
            RequireValue(
                context.UserId,
                nameof(context.UserId));
            RequireValue(
                context.TenantId,
                nameof(context.TenantId));
            RequireValue(
                context.TenantGroupId,
                nameof(context.TenantGroupId));
            RequireValue(
                context.CurrentNamespace,
                nameof(context.CurrentNamespace));
        }

        private static void RequireValue(
            string value,
            string name)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                throw new InvalidOperationException(
                    $"The RBAC execution context field '{name}' is required for durable runtime analysis execution.");
            }
        }
    }
}
