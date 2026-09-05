using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Context
{
    /// <summary>
    /// Builds demo ExecutionContext instances (construction only).
    /// No storage, no rotation, no IO.
    /// </summary>
    public static class ContextFactory
    {
        public const string Project = "rbac-demo";
        public const string Namespace = "crm";

        /// <summary>
        /// Full access demo profile.
        /// </summary>
        public static ExecutionContext Full(string userId)
        {
            return new ExecutionContext
            {
                ContextKey = "",
                Project = Project,
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = Namespace,
                UserId = userId,

                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = Namespace,
                        Trns = new HashSet<string>
                        {
                            "trn:" + Project + ":crm:billing:invoice:read",
                            "trn:" + Project + ":crm:billing:invoice:refund"
                        }
                    }
                },

                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Read-only demo profile (useful to show deny).
        /// </summary>
        public static ExecutionContext ReadOnly(string userId)
        {
            return new ExecutionContext
            {
                ContextKey = "",
                Project = Project,
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = Namespace,
                UserId = userId,

                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = Namespace,
                        Trns = new HashSet<string>
                        {
                            "trn:" + Project + ":crm:billing:invoice:read"
                        }
                    }
                },

                TtlSeconds = 300
            };
        }
    }
}
