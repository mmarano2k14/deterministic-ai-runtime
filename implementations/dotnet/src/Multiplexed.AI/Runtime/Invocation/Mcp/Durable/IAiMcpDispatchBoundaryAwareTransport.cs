using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable
{
    /// <summary>
    /// Optional physical-transport contract used only to classify a failed fenced attempt.
    /// The boundary is marked immediately before the business tools/call may be emitted.
    /// Transports that do not implement this contract remain conservatively possibly-sent.
    /// </summary>
    public interface IAiMcpDispatchBoundaryAwareTransport : IAiMcpToolTransport
    {
        Task<JsonElement> InvokeAsync(
            AiMcpToolRequest request,
            IAiMcpDispatchBoundary boundary,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// One-way observation boundary. Calling MarkPossiblySent never grants execution,
    /// retry or recovery authority; it only removes the ability to claim no emission.
    /// </summary>
    public interface IAiMcpDispatchBoundary
    {
        void MarkPossiblySent();
    }
}
