using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Rbac.Core.ExecutionContext
{
    /// <summary>
    /// Maps durable execution-context snapshots back to live RBAC execution contexts.
    /// </summary>
    /// <remarks>
    /// The mapper preserves the complete durable tenant, namespace, runtime-counter, and TTL identity while
    /// cloning namespace collections so background execution cannot mutate the persisted snapshot instance.
    /// </remarks>
    public static class ExecutionContextSnapshotMapper
    {
        /// <summary>
        /// Creates a live RBAC execution context from the supplied durable snapshot.
        /// </summary>
        /// <param name="snapshot">The authoritative durable execution-context snapshot.</param>
        /// <returns>A live RBAC execution context containing the same durable identity and runtime metadata.</returns>
        public static ExecutionContext ToExecutionContext(
            ExecutionContextSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            return new ExecutionContext
            {
                ContextKey = snapshot.ContextKey,
                Project = snapshot.Project,
                UserId = snapshot.UserId,
                TenantId = snapshot.TenantId,
                TenantGroupId = snapshot.TenantGroupId,
                CurrentNamespace = snapshot.CurrentNamespace,
                Namespaces = snapshot.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = snapshot.InFlightCount,
                TtlSeconds = snapshot.TtlSeconds
            };
        }
    }
}
