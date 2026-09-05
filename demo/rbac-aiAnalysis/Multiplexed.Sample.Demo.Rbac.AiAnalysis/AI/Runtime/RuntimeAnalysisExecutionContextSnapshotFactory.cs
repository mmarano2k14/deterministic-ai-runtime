using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext =
    Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RuntimeAnalysisExecutionContextSnapshotFactory :
        IExecutionContextSnapshotProvider
    {
        private static readonly AsyncLocal<ExecutionContextSnapshot?> AmbientSnapshot =
            new();

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
            var ambient = AmbientSnapshot.Value;

            return ambient is not null
                ? CloneSnapshot(ambient)
                : Create();
        }

        public IDisposable PushSnapshot(
            ExecutionContextSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(
                snapshot);

            var previous = AmbientSnapshot.Value;

            AmbientSnapshot.Value =
                CloneSnapshot(snapshot);

            return new SnapshotScope(
                previous);
        }

        private static ExecutionContextSnapshot CloneSnapshot(
            ExecutionContextSnapshot source)
        {
            ArgumentNullException.ThrowIfNull(
                source);

            return new ExecutionContextSnapshot
            {
                ContextKey = source.ContextKey,
                Project = source.Project,
                UserId = source.UserId,
                TenantId = source.TenantId,
                TenantGroupId = source.TenantGroupId,
                CurrentNamespace = source.CurrentNamespace,
                Namespaces = source.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = source.InFlightCount,
                TtlSeconds = source.TtlSeconds,
                CreatedAtUtc = source.CreatedAtUtc
            };
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

        private sealed class SnapshotScope :
            IDisposable
        {
            private readonly ExecutionContextSnapshot? _previous;
            private int _disposed;

            public SnapshotScope(
                ExecutionContextSnapshot? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(
                        ref _disposed,
                        1) == 1)
                {
                    return;
                }

                AmbientSnapshot.Value =
                    _previous;
            }
        }
    }
}
