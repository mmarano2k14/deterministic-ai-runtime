using System.Net;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Tests the bounded MCP HTTP 429 backpressure retry policy.
    /// </summary>
    public sealed class McpBackpressureRetryHelperTests
    {
        /// <summary>
        /// Verifies that transient HTTP 429 responses are retried and the eventual result is returned.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Retry_TooManyRequests_And_Return_Result()
        {
            var attemptCount = 0;
            var retryCount = 0;

            var result =
                await McpBackpressureRetryHelper
                    .ExecuteAsync(
                        () =>
                        {
                            attemptCount++;

                            if (attemptCount < 3)
                            {
                                throw new HttpRequestException(
                                    "throttled",
                                    null,
                                    HttpStatusCode.TooManyRequests);
                            }

                            return Task.FromResult("ok");
                        },
                        "test.retry",
                        maximumAttemptCount: 4,
                        onRetry: (_, _, _) => retryCount++)
                    .ConfigureAwait(false);

            Assert.Equal("ok", result);
            Assert.Equal(3, attemptCount);
            Assert.Equal(2, retryCount);
        }

        /// <summary>
        /// Verifies that non-429 HTTP failures are not retried.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Not_Retry_NonTooManyRequests_Failure()
        {
            var attemptCount = 0;

            var exception =
                await Assert.ThrowsAsync<HttpRequestException>(
                    () => McpBackpressureRetryHelper.ExecuteAsync<string>(
                        () =>
                        {
                            attemptCount++;

                            throw new HttpRequestException(
                                "server error",
                                null,
                                HttpStatusCode.InternalServerError);
                        },
                        "test.no-retry"))
                    .ConfigureAwait(false);

            Assert.Equal(
                HttpStatusCode.InternalServerError,
                exception.StatusCode);
            Assert.Equal(1, attemptCount);
        }
    }
}
