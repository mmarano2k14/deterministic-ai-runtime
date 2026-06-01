namespace Multiplexed.AI.McpServer.Models.Responses
{
    /// <summary>
    /// Represents a common response envelope returned by MCP control-plane tools.
    /// </summary>
    /// <typeparam name="TData">The response data type.</typeparam>
    public sealed class AiMcpToolResponse<TData>
    {
        /// <summary>
        /// Gets whether the MCP tool operation succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the response message.
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// Gets the failure reason when the operation did not succeed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets the response data.
        /// </summary>
        public TData? Data { get; init; }

        /// <summary>
        /// Creates a successful MCP tool response.
        /// </summary>
        public static AiMcpToolResponse<TData> Ok(TData data, string? message = null)
        {
            return new AiMcpToolResponse<TData>
            {
                Success = true,
                Message = message,
                Data = data
            };
        }

        /// <summary>
        /// Creates a failed MCP tool response.
        /// </summary>
        public static AiMcpToolResponse<TData> Fail(string failureReason, string? message = null)
        {
            return new AiMcpToolResponse<TData>
            {
                Success = false,
                Message = message,
                FailureReason = failureReason
            };
        }
    }
}