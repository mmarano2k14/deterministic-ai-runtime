using System.Net;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Executes MCP proof operations with a bounded retry policy for transient HTTP 429 backpressure.
    /// </summary>
    internal static class McpBackpressureRetryHelper
    {
        private static readonly TimeSpan InitialRetryDelay =
            TimeSpan.FromMilliseconds(100);

        private static readonly TimeSpan MaximumRetryDelay =
            TimeSpan.FromSeconds(2);

        /// <summary>
        /// Executes one MCP operation and retries only transient HTTP 429 responses.
        /// </summary>
        /// <typeparam name="TResult">The operation result type.</typeparam>
        /// <param name="operation">The MCP operation.</param>
        /// <param name="operationName">A diagnostic operation name.</param>
        /// <param name="maximumAttemptCount">The maximum total attempt count.</param>
        /// <param name="onRetry">An optional retry observer.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The MCP operation result.</returns>
        public static async Task<TResult> ExecuteAsync<TResult>(
            Func<Task<TResult>> operation,
            string operationName,
            int maximumAttemptCount = 8,
            Action<string, int, TimeSpan>? onRetry = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumAttemptCount);

            var retryDelay =
                InitialRetryDelay;

            for (var attempt = 1;
                 attempt <= maximumAttemptCount;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await operation()
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                    when (exception.StatusCode ==
                        HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= maximumAttemptCount)
                    {
                        throw new HttpRequestException(
                            $"MCP operation '{operationName}' remained throttled after '{attempt}' attempts.",
                            exception,
                            HttpStatusCode.TooManyRequests);
                    }

                    onRetry?.Invoke(
                        operationName,
                        attempt,
                        retryDelay);

                    await Task
                        .Delay(retryDelay, cancellationToken)
                        .ConfigureAwait(false);

                    retryDelay =
                        TimeSpan.FromMilliseconds(
                            Math.Min(
                                retryDelay.TotalMilliseconds * 2,
                                MaximumRetryDelay.TotalMilliseconds));
                }
            }

            throw new InvalidOperationException(
                $"MCP operation '{operationName}' exhausted its bounded retry loop without a result.");
        }
    }
}
