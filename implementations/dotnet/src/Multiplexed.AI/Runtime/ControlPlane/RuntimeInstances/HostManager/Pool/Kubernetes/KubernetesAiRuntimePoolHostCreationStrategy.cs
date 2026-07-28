using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Creates one opt-in Kubernetes Runtime Pool Pod for a provider scale-out request.
    /// </summary>
    /// <remarks>
    /// This strategy owns Kubernetes resource lifecycle only. The in-Pod Process Pool Manager,
    /// child registration, stable HTTP/gRPC routing, and runtime-level readiness are completed
    /// by the following milestone packages.
    /// </remarks>
    public sealed class KubernetesAiRuntimePoolHostCreationStrategy :
        IAiRuntimeHostCreationStrategy
    {
        private readonly AiKubernetesRuntimePoolOptions poolOptions;
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;
        private readonly AiKubernetesRuntimePoolPodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimePoolHostClient client;
        private readonly AiKubernetesRuntimePoolInPodCommandLineFactory commandLineFactory;
        private readonly ILogger<KubernetesAiRuntimePoolHostCreationStrategy> logger;

        /// <summary>
        /// Initializes a new Kubernetes Runtime Pool host creation strategy.
        /// </summary>
        public KubernetesAiRuntimePoolHostCreationStrategy(
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions,
            AiKubernetesRuntimePoolPodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimePoolHostClient client,
            AiKubernetesRuntimePoolInPodCommandLineFactory commandLineFactory,
            ILogger<KubernetesAiRuntimePoolHostCreationStrategy> logger)
        {
            this.poolOptions =
                poolOptions?.Value
                ?? throw new ArgumentNullException(nameof(poolOptions));

            this.hostOptions =
                hostOptions?.Value
                ?? throw new ArgumentNullException(nameof(hostOptions));

            this.podSpecBuilder =
                podSpecBuilder
                ?? throw new ArgumentNullException(nameof(podSpecBuilder));

            this.client =
                client
                ?? throw new ArgumentNullException(nameof(client));

            this.commandLineFactory =
                commandLineFactory
                ?? throw new ArgumentNullException(nameof(commandLineFactory));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode =>
            AiRuntimeHostCreationMode.KubernetesPool;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var validationFailure = this.ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(validationFailure))
            {
                return CreateRejected(
                    request,
                    validationFailure,
                    retryable: false);
            }

            AiKubernetesRuntimePoolPodSpec podSpec;
            try
            {
                var podRequestId =
                    CreatePodRequestId(request);

                var plan =
                    AiKubernetesRuntimePoolPodPlanFactory.Create(
                        this.poolOptions,
                        podRequestId,
                        request.RuntimeInstanceId);

                var basePodSpec =
                    this.podSpecBuilder.Build(plan);

                podSpec =
                    basePodSpec with
                    {
                        ContainerArguments =
                            this.commandLineFactory.Create(
                                basePodSpec,
                                request)
                    };
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES RUNTIME POOL SPEC BUILD FAILED RuntimeInstanceId={RuntimeInstanceId} PoolId={PoolId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.PoolId,
                    exception.Message);

                return CreateRejected(
                    request,
                    string.Concat(
                        "kubernetes-runtime-pool-spec-build-failed:",
                        exception.Message),
                    retryable: false);
            }

            var createResult =
                await this.client
                    .CreateRuntimePoolHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!createResult.Success)
            {
                return CreateRejected(
                    request,
                    createResult.FailureReason
                    ?? "kubernetes-runtime-pool-create-failed",
                    createResult.Retryable,
                    createResult.Metadata);
            }

            var readinessResult =
                await this.client
                    .WaitUntilHostReadyAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            var metadata =
                MergeMetadata(
                    createResult.Metadata,
                    readinessResult.Metadata);

            if (!readinessResult.Success)
            {
                if (this.hostOptions.DeleteResourcesOnFailure)
                {
                    await this.client
                        .DeleteRuntimePoolHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return CreateRejected(
                    request,
                    readinessResult.FailureReason
                    ?? "kubernetes-runtime-pool-readiness-failed",
                    readinessResult.Retryable,
                    metadata);
            }

            if (!metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostId,
                    out var hostId)
                || string.IsNullOrWhiteSpace(hostId))
            {
                if (this.hostOptions.DeleteResourcesOnFailure)
                {
                    await this.client
                        .DeleteRuntimePoolHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return CreateRejected(
                    request,
                    "kubernetes-runtime-pool-pod-uid-missing",
                    retryable: true,
                    metadata);
            }

            metadata =
                MergeMetadata(
                    metadata,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeHostMetadataKeys.HostProvider] =
                            "kubernetes",
                        [AiRuntimeHostMetadataKeys.HostCreationMode] =
                            AiRuntimeHostCreationMode
                                .KubernetesPool
                                .ToString(),
                        [AiRuntimeHostMetadataKeys.HostCreationStrategy] =
                            nameof(
                                KubernetesAiRuntimePoolHostCreationStrategy),
                        [AiRuntimeHostMetadataKeys.HostId] = hostId,
                        [AiRuntimeHostMetadataKeys.HostName] =
                            podSpec.PodName,
                        ["runtime.pool.id"] = podSpec.PoolId,
                        ["runtime.pool.primaryRuntimeInstanceId"] =
                            request.RuntimeInstanceId
                    });

            metadata.TryGetValue(
                "transport.endpoint",
                out var transportEndpoint);

            return AiRuntimeHostStartResult.Started(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                transportEndpoint,
                metadata);
        }

        /// <summary>
        /// Validates first-class request authority before resource creation.
        /// </summary>
        private string? ValidateRequest(
            AiRuntimeHostStartRequest request)
        {
            if (!this.poolOptions.Enabled)
            {
                return "kubernetes-runtime-pool-disabled";
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return "kubernetes-runtime-pool-request-id-missing";
            }

            if (string.IsNullOrWhiteSpace(request.PoolId))
            {
                return "kubernetes-runtime-pool-id-missing";
            }

            if (!string.Equals(
                    request.PoolId,
                    this.poolOptions.PoolId,
                    StringComparison.Ordinal))
            {
                return "kubernetes-runtime-pool-id-mismatch";
            }

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return "kubernetes-runtime-pool-primary-runtime-id-missing";
            }

            if (!string.Equals(
                    request.ProviderName,
                    this.poolOptions.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "kubernetes-runtime-pool-provider-mismatch";
            }

            if (!string.Equals(
                    request.TransportName,
                    this.poolOptions.TransportName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "kubernetes-runtime-pool-transport-mismatch";
            }

            return null;
        }

        /// <summary>
        /// Creates a stable DNS-safe Pod request identity from first-class request fields.
        /// </summary>
        private static string CreatePodRequestId(
            AiRuntimeHostStartRequest request)
        {
            var source =
                string.Concat(
                    request.RequestId,
                    "|",
                    request.PoolId,
                    "|",
                    request.RuntimeInstanceId);

            return Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(source)))
                .ToLowerInvariant()[..24];
        }

        /// <summary>
        /// Creates a rejected strategy result.
        /// </summary>
        private static AiRuntimeHostStartResult CreateRejected(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return AiRuntimeHostStartResult.Rejected(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                failureReason,
                retryable,
                metadata);
        }

        /// <summary>
        /// Merges diagnostic metadata without changing first-class request fields.
        /// </summary>
        private static Dictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (first is not null)
            {
                foreach (var pair in first)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            if (second is not null)
            {
                foreach (var pair in second)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }
    }
}
