using Multiplexed.Rbac.Core.Stores.Cache;
using System.Runtime.CompilerServices;

namespace Multiplexed.AI.Runtime.Observability.Performance
{
    internal static class AiRedisReadAttributionModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!AiRedisReadAttributionDiagnostics.IsEnabled)
            {
                return;
            }

            RedisContextStoreDiagnostics.ContextReadCompleted +=
                static (database, value) =>
                    AiRedisReadAttributionDiagnostics.Record(
                        database,
                        AiRedisReadAttributionOperations.RbacExecutionContextLoad,
                        "GET",
                        value);

            RedisContextStoreDiagnostics.ContextRotateLuaCompleted +=
                static database =>
                    AiRedisReadAttributionDiagnostics.RecordInvocation(
                        database,
                        AiRedisReadAttributionOperations.LuaRbacContext);
        }
    }
}
