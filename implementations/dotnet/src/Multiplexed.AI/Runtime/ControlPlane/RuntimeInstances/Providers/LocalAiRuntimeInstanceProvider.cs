using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Local in-memory runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider preserves the current local runtime instance behavior.
    /// It resolves runtime instances from <see cref="IAiSharedRuntimeInstanceRegistry"/>.
    /// </para>
    ///
    /// <para>
    /// Dispatch operations enqueue shared runs into the selected local runtime instance queue.
    /// Status operations read run or queue visibility from the selected local runtime instance
    /// queue control-plane.
    /// Control operations pause, resume, cancel, or cancel queued runs through the same
    /// existing queue control-plane.
    /// </para>
    ///
    /// <para>
    /// This provider does not replace local queues. The target runtime instance remains
    /// responsible for its own local queue, worker pool, and DAG execution engine.
    /// </para>
    /// </remarks>
    [AiRuntimeInstanceProvider("local")]
    public sealed class LocalAiRuntimeInstanceProvider :
        IAiRuntimeInstanceDispatchProvider,
        IAiRuntimeInstanceStatusProvider,
        IAiRuntimeInstanceControlProvider
    {
        /// <summary>
        /// The provider name used by this local runtime instance provider.
        /// </summary>
        private const string ProviderName = "local";

        /// <summary>
        /// The shared runtime instance registry used to resolve local runtime instances.
        /// </summary>
        private readonly IAiSharedRuntimeInstanceRegistry registry;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiRuntimeInstanceProvider"/> class.
        /// </summary>
        /// <param name="registry">
        /// The shared runtime instance registry used to resolve local runtime instances.
        /// </param>
        public LocalAiRuntimeInstanceProvider(
            IAiSharedRuntimeInstanceRegistry registry)
        {
            this.registry =
                registry
                ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc />
        public bool CanHandle(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.Metadata is not null &&
                descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                return string.Equals(
                    providerName.Trim(),
                    ProviderName,
                    StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        /// <inheritdoc />
        public async Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedDispatchResult(
                    request,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .DispatchAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .GetRunStatusAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .GetQueueStatusAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.PauseQueue,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .PauseQueueAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .ResumeQueueAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.CancelRun,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .CancelRunAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var resolution =
                await ResolveSharedRuntimeInstanceAsync(
                        descriptor,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resolution.Instance is null)
            {
                return CreateFailedQueueResult(
                    request,
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                    resolution.RuntimeInstanceId,
                    startedAtUtc,
                    resolution.FailureReason ?? "runtime-instance-not-available",
                    resolution.Message ?? "Runtime instance is not available.");
            }

            return await resolution.Instance
                .QueueControlPlane
                .CancelQueuedRunAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves a local shared runtime instance from a capacity descriptor and fallback runtime id.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="fallbackRuntimeInstanceId">The fallback runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime instance resolution result.</returns>
        private async Task<SharedRuntimeInstanceResolution> ResolveSharedRuntimeInstanceAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            string? fallbackRuntimeInstanceId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    descriptor,
                    fallbackRuntimeInstanceId);

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return SharedRuntimeInstanceResolution.Failed(
                    string.Empty,
                    "runtime-instance-id-missing",
                    "Runtime instance id is missing.");
            }

            var instance =
                await registry
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (instance is null)
            {
                return SharedRuntimeInstanceResolution.Failed(
                    runtimeInstanceId,
                    "runtime-instance-not-registered",
                    "Local runtime instance was not found in the shared runtime instance registry.");
            }

            return SharedRuntimeInstanceResolution.Succeeded(
                runtimeInstanceId,
                instance);
        }

        /// <summary>
        /// Resolves the runtime instance identifier from the descriptor and fallback value.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="fallbackRuntimeInstanceId">The fallback runtime instance identifier.</param>
        /// <returns>The resolved runtime instance identifier.</returns>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            string? fallbackRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            return string.IsNullOrWhiteSpace(descriptor.RuntimeInstanceId)
                ? fallbackRuntimeInstanceId ?? string.Empty
                : descriptor.RuntimeInstanceId;
        }

        /// <summary>
        /// Creates a failed shared runtime instance dispatch result.
        /// </summary>
        /// <param name="request">The original shared runtime instance dispatch request.</param>
        /// <param name="runtimeInstanceId">The target runtime instance identifier.</param>
        /// <param name="startedAtUtc">The UTC timestamp when dispatch started.</param>
        /// <param name="failureReason">The failure reason code.</param>
        /// <param name="message">The human-readable failure message.</param>
        /// <returns>The failed dispatch result.</returns>
        private static AiSharedRuntimeInstanceDispatchResult CreateFailedDispatchResult(
            AiSharedRuntimeInstanceDispatchRequest request,
            string runtimeInstanceId,
            DateTimeOffset startedAtUtc,
            string failureReason,
            string message)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiSharedRuntimeInstanceDispatchResult
            {
                Success = false,
                RuntimeInstanceId = runtimeInstanceId,
                SharedRunId = request.SharedRun.SharedRunId,
                ClaimToken = request.ClaimToken,
                Message = message,
                FailureReason = failureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = Math.Max(
                    0,
                    (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName
                }
            };
        }

        /// <summary>
        /// Creates a failed runtime queue control-plane result.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="operation">The runtime queue operation.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="startedAtUtc">The UTC timestamp when status or control resolution started.</param>
        /// <param name="failureReason">The failure reason code.</param>
        /// <param name="message">The human-readable failure message.</param>
        /// <returns>The failed runtime queue control-plane result.</returns>
        private static AiRuntimeQueueControlPlaneResult CreateFailedQueueResult(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            string runtimeInstanceId,
            DateTimeOffset startedAtUtc,
            string failureReason,
            string message)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = operation,
                Success = false,
                Message = message,
                RunId = request.RunId,
                CorrelationId = request.CorrelationId,
                RuntimeInstanceId = runtimeInstanceId,
                RequestedBy = request.RequestedBy,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = Math.Max(
                    0,
                    (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                FailureReason = failureReason,
                Diagnostics = request.IncludeDiagnostics
                    ? new[]
                    {
                        message
                    }
                    : Array.Empty<string>()
            };
        }

        /// <summary>
        /// Represents the result of resolving a local shared runtime instance.
        /// </summary>
        private sealed class SharedRuntimeInstanceResolution
        {
            /// <summary>
            /// Gets the resolved runtime instance identifier.
            /// </summary>
            public string RuntimeInstanceId { get; private init; } = string.Empty;

            /// <summary>
            /// Gets the resolved shared runtime instance.
            /// </summary>
            public IAiSharedRuntimeInstance? Instance { get; private init; }

            /// <summary>
            /// Gets the failure reason code when resolution failed.
            /// </summary>
            public string? FailureReason { get; private init; }

            /// <summary>
            /// Gets the human-readable failure message when resolution failed.
            /// </summary>
            public string? Message { get; private init; }

            /// <summary>
            /// Creates a successful shared runtime instance resolution.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <param name="instance">The shared runtime instance.</param>
            /// <returns>The successful resolution.</returns>
            public static SharedRuntimeInstanceResolution Succeeded(
                string runtimeInstanceId,
                IAiSharedRuntimeInstance instance)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                ArgumentNullException.ThrowIfNull(instance);

                return new SharedRuntimeInstanceResolution
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Instance = instance
                };
            }

            /// <summary>
            /// Creates a failed shared runtime instance resolution.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <param name="failureReason">The failure reason code.</param>
            /// <param name="message">The human-readable failure message.</param>
            /// <returns>The failed resolution.</returns>
            public static SharedRuntimeInstanceResolution Failed(
                string runtimeInstanceId,
                string failureReason,
                string message)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
                ArgumentException.ThrowIfNullOrWhiteSpace(message);

                return new SharedRuntimeInstanceResolution
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    FailureReason = failureReason,
                    Message = message
                };
            }
        }
    }
}