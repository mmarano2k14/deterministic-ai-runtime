using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.Abstractions.AI.Invocation.Workers
{
    /// <summary>Portable bytes, not a payload-store key or a local filesystem path.</summary>
    public sealed record AiWorkerFile(string Path, string Sha256, long SizeBytes, string Base64Url);
    public sealed record AiWorkerDependency(string Name, string Version, IReadOnlyList<AiWorkerFile> Files);

    /// <summary>Only the selected, verified function and its pinned dependency closure leave the server.</summary>
    public sealed record AiWorkerCodeBundle(
        AiDurableInvocationTarget Target, AiPublicationEnvironment Runtime,
        string EntryPointPath, string EntryPointSymbol, IReadOnlyList<AiWorkerFile> Sources,
        IReadOnlyList<AiWorkerDependency> Dependencies)
    {
        // Server-only admission metadata, restored from the verified immutable environment.
        // Never forward it automatically to the closed Python/Node/.NET wire readers.
        [JsonIgnore]
        public AiPublicationExecutionDescriptor? ExecutionDescriptor { get; init; }
    }

    /// <summary>
    /// Server-owned wire projection. A worker reads JSON; it never references this assembly.
    /// Journal lease tokens, RBAC snapshots, store keys and service credentials are excluded.
    /// </summary>
    public sealed record AiWorkerInvocationRequest(
        int ProtocolVersion, string Type, string RequestId, string OperationId,
        string EffectIdempotencyKey, string WorkerId, long Epoch, string TenantId,
        string ExecutionId, string StepName, int Generation, DateTimeOffset DeadlineUtc,
        string? TraceParent, JsonElement Inputs, AiWorkerCodeBundle Code);

    /// <summary>The server validates correlation before exposing a frame to the supervisor.</summary>
    public sealed record AiWorkerInvocationFrame(string Type, AiDurableInvocationResult? Result = null);

    /// <summary>
    /// One process/assignment exchange. The callback is called only on a valid ready/heartbeat
    /// frame. Implementations must stop work on cancellation and must never retry the operation.
    /// </summary>
    public interface IAiWorkerInvocationTransport
    {
        Task<AiDurableInvocationResult> InvokeAsync(AiWorkerInvocationRequest request,
            Func<CancellationToken, Task> heartbeat, CancellationToken cancellationToken = default);
    }

    /// <summary>Preparation is authorized through the restored original execution context.</summary>
    public interface IAiWorkerInvocationPreparer
    {
        Task<AiWorkerCodeBundle> PrepareAsync(AiDurableInvocationRecord invocation,
            CancellationToken cancellationToken = default);
    }
}
