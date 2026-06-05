using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Local in-memory runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider preserves the current local runtime instance dispatch behavior.
    /// It resolves runtime instances from <see cref="IAiSharedRuntimeInstanceRegistry"/>
    /// and dispatches the request to the selected local runtime instance.
    /// </para>
    /// <para>
    /// It does not replace local queues. The target runtime instance still enqueues
    /// the run into its own local runtime queue.
    /// </para>
    /// </remarks>
    [AiRuntimeInstanceProvider("local")]
    public sealed class LocalAiRuntimeInstanceProvider : IAiRuntimeInstanceDispatchProvider
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

            var runtimeInstanceId =
                string.IsNullOrWhiteSpace(descriptor.RuntimeInstanceId)
                    ? request.RuntimeInstanceId
                    : descriptor.RuntimeInstanceId;

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return CreateFailedResult(
                    request,
                    string.Empty,
                    startedAtUtc,
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
                return CreateFailedResult(
                    request,
                    runtimeInstanceId,
                    startedAtUtc,
                    "runtime-instance-not-registered",
                    "Local runtime instance was not found in the shared runtime instance registry.");
            }

            return await instance
                .DispatchAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
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
        private static AiSharedRuntimeInstanceDispatchResult CreateFailedResult(
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
    }
}