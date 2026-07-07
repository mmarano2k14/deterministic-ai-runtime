using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides a Kubernetes runtime host creation strategy.
    /// </summary>
    /// <remarks>
    /// This strategy represents Kubernetes as a runtime host lifecycle provider.
    /// It creates Kubernetes-level runtime host resources through <see cref="IAiKubernetesRuntimeHostClient" />,
    /// waits for Kubernetes host readiness, publishes the Kubernetes-backed runtime instance into
    /// the runtime registry and capacity store, and then returns control to the provider-level provisioner.
    /// Runtime command dispatch and runtime-level readiness remain owned by the configured runtime provider,
    /// such as HTTP or gRPC.
    /// </remarks>
    public sealed class KubernetesAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly AiKubernetesRuntimePodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimeHostClient client;
        private readonly IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher;
        private readonly ILogger<KubernetesAiRuntimeHostCreationStrategy> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="podSpecBuilder">The Kubernetes runtime pod specification builder.</param>
        /// <param name="client">The Kubernetes runtime host client.</param>
        /// <param name="runtimeInstancePublisher">The Kubernetes runtime instance publisher.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter kept for constructor compatibility.</param>
        /// <param name="logger">The logger.</param>
        public KubernetesAiRuntimeHostCreationStrategy(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            AiKubernetesRuntimePodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimeHostClient client,
            IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            ILogger<KubernetesAiRuntimeHostCreationStrategy> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(readinessWaiter);
            this.options = options.Value ?? throw new ArgumentException("Kubernetes runtime host options are required.", nameof(options));
            this.podSpecBuilder = podSpecBuilder ?? throw new ArgumentNullException(nameof(podSpecBuilder));
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.runtimeInstancePublisher = runtimeInstancePublisher ?? throw new ArgumentNullException(nameof(runtimeInstancePublisher));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            this.logger.LogInformation(
                "KUBERNETES HOST START BEGIN RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} ClientMode={ClientMode} RequireRuntimeReadiness={RequireRuntimeReadiness} RuntimeImage={RuntimeImage} Namespace={Namespace}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                this.options.ClientMode,
                this.options.RequireRuntimeReadiness,
                this.options.RuntimeImage,
                this.options.Namespace);

            if (!this.options.Enabled)
            {
                return this.CreateRejectedWithLog(
                    request,
                    "kubernetes-runtime-host-creation-disabled",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.Namespace))
            {
                return this.CreateRejectedWithLog(
                    request,
                    "kubernetes-runtime-namespace-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeImage))
            {
                return this.CreateRejectedWithLog(
                    request,
                    "kubernetes-runtime-image-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.ContainerName))
            {
                return this.CreateRejectedWithLog(
                    request,
                    "kubernetes-runtime-container-name-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            AiKubernetesRuntimePodSpec podSpec;

            try
            {
                podSpec = this.podSpecBuilder.Build(request);
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES HOST POD SPEC BUILD FAILED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    exception.Message);

                return CreateRejectedResult(
                    request,
                    exception.Message,
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            this.logger.LogInformation(
                "KUBERNETES HOST POD SPEC BUILT RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} ContainerName={ContainerName} ContainerPort={ContainerPort} RuntimeImage={RuntimeImage}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                podSpec.ContainerName,
                podSpec.ContainerPort,
                podSpec.RuntimeImage);

            var createResult =
                await this.client
                    .CreateRuntimeHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST CREATED RuntimeInstanceId={RuntimeInstanceId} Success={Success} PodName={PodName} ServiceName={ServiceName} FailureReason={FailureReason} Retryable={Retryable}",
                request.RuntimeInstanceId,
                createResult.Success,
                createResult.PodName,
                createResult.ServiceName,
                createResult.FailureReason ?? "(none)",
                createResult.Retryable);

            var metadata =
                MergeMetadata(
                    podSpec.Annotations,
                    createResult.Metadata);

            if (!createResult.Success)
            {
                return CreateRejectedResult(
                    request,
                    createResult.FailureReason ?? "kubernetes-runtime-host-create-failed",
                    createResult.Retryable,
                    metadata);
            }

            this.logger.LogInformation(
                "KUBERNETES HOST READY WAIT BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} Timeout={Timeout} PollInterval={PollInterval}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                this.options.ReadinessTimeout,
                this.options.ReadinessPollInterval);

            var hostReadinessResult =
                await this.client
                    .WaitUntilHostReadyAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST READY RESULT RuntimeInstanceId={RuntimeInstanceId} Success={Success} PodName={PodName} TimedOut={TimedOut} FailureReason={FailureReason} Retryable={Retryable}",
                request.RuntimeInstanceId,
                hostReadinessResult.Success,
                hostReadinessResult.PodName,
                hostReadinessResult.TimedOut,
                hostReadinessResult.FailureReason ?? "(none)",
                hostReadinessResult.Retryable);

            metadata =
                MergeMetadata(
                    metadata,
                    hostReadinessResult.Metadata);

            if (!hostReadinessResult.Success)
            {
                await this.DeleteOnFailureAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

                return CreateRejectedResult(
                    request,
                    hostReadinessResult.FailureReason ?? "kubernetes-runtime-host-readiness-failed",
                    hostReadinessResult.Retryable,
                    metadata);
            }

            this.logger.LogInformation(
                "KUBERNETES HOST STARTED AFTER POD READINESS RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} RequireRuntimeReadiness={RequireRuntimeReadiness}",
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                this.options.RequireRuntimeReadiness);

            var startedResult =
                AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    metadata);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME INSTANCE PUBLICATION BEGIN RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName);

            await this.runtimeInstancePublisher
                .PublishAsync(
                    request,
                    startedResult,
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME INSTANCE PUBLICATION COMPLETED RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName);

            return startedResult;
        }

        /// <summary>
        /// Creates a rejected runtime host start result while logging the structured reason.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure is retryable.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The rejected runtime host start result.</returns>
        private AiRuntimeHostStartResult CreateRejectedWithLog(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string> metadata)
        {
            this.logger.LogWarning(
                "KUBERNETES HOST START REJECTED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                request.RuntimeInstanceId,
                failureReason);

            return CreateRejectedResult(
                request,
                failureReason,
                retryable,
                metadata);
        }

        /// <summary>
        /// Deletes Kubernetes resources after a failed host creation flow when configured to do so.
        /// </summary>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task DeleteOnFailureAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken)
        {
            if (!this.options.DeleteResourcesOnFailure)
            {
                return;
            }

            this.logger.LogInformation(
                "KUBERNETES HOST DELETE ON FAILURE BEGIN PodName={PodName} Namespace={Namespace}",
                podSpec.PodName,
                podSpec.Namespace);

            var deleteResult =
                await this.client
                    .DeleteRuntimeHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST DELETE ON FAILURE RESULT PodName={PodName} Namespace={Namespace} Success={Success} FailureReason={FailureReason}",
                podSpec.PodName,
                podSpec.Namespace,
                deleteResult.Success,
                deleteResult.FailureReason ?? "(none)");
        }

        /// <summary>
        /// Creates a rejected runtime host start result.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure is retryable.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The rejected runtime host start result.</returns>
        private static AiRuntimeHostStartResult CreateRejectedResult(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string> metadata)
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
        /// Creates base Kubernetes host lifecycle metadata.
        /// </summary>
        /// <returns>The base metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateBaseMetadata()
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(KubernetesAiRuntimeHostCreationStrategy)
            };
        }

        /// <summary>
        /// Merges metadata dictionaries using case-insensitive keys.
        /// </summary>
        /// <param name="first">The first metadata dictionary.</param>
        /// <param name="second">The second metadata dictionary.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in CreateBaseMetadata())
            {
                metadata[item.Key] = item.Value;
            }

            if (first is not null)
            {
                foreach (var item in first)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (second is not null)
            {
                foreach (var item in second)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            return metadata;
        }
    }
}