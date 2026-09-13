using System.Text.Json;

namespace Multiplexed.Abstractions.AI.Invocation.Mcp
{
    /// <summary>
    /// Trusted server transport boundary. Production transports are installed explicitly by trusted host configuration.
    /// An implementation must enforce tenant-scoped routing, use server-owned credentials,
    /// honor cancellation, return promptly with a Task, and never retry an effect silently.
    /// It must throw for protocol/transport errors, not return an empty success result.
    /// </summary>
    public interface IAiMcpToolTransport
    {
        /// <summary>
        /// Returns a detached internal envelope: schemaVersion=1, matching requestId,
        /// explicit isError, content array, and optional structuredContent object.
        /// A real MCP client must normalize CallToolResult into this envelope, including
        /// an explicit isError value, while preserving protocol/tool failures explicitly.
        /// No worker-provided Park, retry, payload-store reference or claim is accepted.
        /// </summary>
        Task<JsonElement> InvokeAsync(
            AiMcpToolRequest request,
            CancellationToken cancellationToken = default);
    }
}
