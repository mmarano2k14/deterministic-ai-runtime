using StackExchange.Redis;

namespace Multiplexed.Rbac.Core.Stores.Cache
{
    /// <summary>
    /// Diagnostic-only hooks emitted by the Redis RBAC context store.
    /// </summary>
    public static class RedisContextStoreDiagnostics
    {
        public static event Action<IDatabase, RedisValue>? ContextReadCompleted;

        public static event Action<IDatabase>? ContextRotateLuaCompleted;

        internal static void NotifyContextReadCompleted(
            IDatabase database,
            RedisValue value)
        {
            try
            {
                ContextReadCompleted?.Invoke(database, value);
            }
            catch
            {
                // Diagnostics must never change RBAC runtime behavior.
            }
        }

        internal static void NotifyContextRotateLuaCompleted(
            IDatabase database)
        {
            try
            {
                ContextRotateLuaCompleted?.Invoke(database);
            }
            catch
            {
                // Diagnostics must never change RBAC runtime behavior.
            }
        }
    }
}
