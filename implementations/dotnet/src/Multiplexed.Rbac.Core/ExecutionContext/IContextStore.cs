namespace Multiplexed.Rbac.Core.ExecutionContext
{
    public enum InFlightAcquireResult
    {
        Acquired = 0,
        ContextNotFound = 1,
        LimitExceeded = 2
    }

    public interface IContextStore
    {
        Task<string> StoreAsync(ExecutionContext context);

        Task<ExecutionContext?> GetAsync(string key);

        Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight);

        /// <summary>
        /// Acquires an in-flight slot while preserving the reason an acquire failed.
        /// Existing stores that only implement TryAcquireInFlightAsync remain compatible;
        /// specialized stores should override this method when they can classify atomically.
        /// </summary>
        async Task<InFlightAcquireResult> AcquireInFlightAsync(string key, int maxInFlight)
        {
            if (await TryAcquireInFlightAsync(key, maxInFlight).ConfigureAwait(false))
            {
                return InFlightAcquireResult.Acquired;
            }

            return await GetAsync(key).ConfigureAwait(false) is null
                ? InFlightAcquireResult.ContextNotFound
                : InFlightAcquireResult.LimitExceeded;
        }

        Task ReleaseInFlightAsync(string key);

        /// <summary>
        /// Rotates the current context key and keeps the previous key alive
        /// for the provided overlap window.
        /// </summary>
        Task<(string newKey, ExecutionContext context)> RotateAsync(string key, TimeSpan overlapWindow);

        Task<string> SeedAsync(ExecutionContext context);
    }
}