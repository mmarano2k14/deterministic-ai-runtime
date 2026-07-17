using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DiagnosticsProcess = System.Diagnostics.Process;

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
    public sealed class KubernetesAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy, IAiRuntimeHostProcessControl, IDisposable
    {
        /// <summary>
        /// Serializes dynamic local port allocation and kubectl binding across all
        /// Kubernetes host strategy instances in the current control-plane process.
        /// </summary>
        private static readonly SemaphoreSlim PortForwardStartupGate =
            new(initialCount: 1, maxCount: 1);

        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly AiKubernetesRuntimePodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimeHostClient client;
        private readonly IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly ILogger<KubernetesAiRuntimeHostCreationStrategy> logger;
        private readonly ConcurrentDictionary<string, DiagnosticsProcess> portForwardProcesses;

        private readonly ConcurrentDictionary<string, AiKubernetesRuntimePodSpec> podSpecsByRuntimeInstanceId;

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
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.portForwardProcesses = new ConcurrentDictionary<string, DiagnosticsProcess>(StringComparer.OrdinalIgnoreCase);
            this.podSpecsByRuntimeInstanceId = new ConcurrentDictionary<string, AiKubernetesRuntimePodSpec>(StringComparer.OrdinalIgnoreCase);
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
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-host-creation-disabled", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.Namespace))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-namespace-missing", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeImage))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-image-missing", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.ContainerName))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-container-name-missing", false, CreateBaseMetadata());
            }

            AiKubernetesRuntimePodSpec podSpec;

            try
            {
                podSpec = this.podSpecBuilder.Build(request);
                this.podSpecsByRuntimeInstanceId[request.RuntimeInstanceId] = podSpec;
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES HOST POD SPEC BUILD FAILED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    exception.Message);

                return CreateRejectedResult(request, exception.Message, false, CreateBaseMetadata());
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
                    .CreateRuntimeHostAsync(podSpec, cancellationToken)
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
                    .WaitUntilHostReadyAsync(podSpec, cancellationToken)
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
                await this.DeleteOnFailureAsync(podSpec, cancellationToken).ConfigureAwait(false);

                return CreateRejectedResult(
                    request,
                    hostReadinessResult.FailureReason ?? "kubernetes-runtime-host-readiness-failed",
                    hostReadinessResult.Retryable,
                    metadata);
            }

            if (this.ShouldUsePortForwardTransportEndpoint(request))
            {
                try
                {
                    var portForwardEndpoint =
                        await this.StartPortForwardAsync(
                                request,
                                podSpec,
                                createResult.ServiceName,
                                metadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                    metadata =
                        MergeMetadata(
                            metadata,
                            CreatePortForwardTransportEndpointMetadata(
                                portForwardEndpoint.Endpoint,
                                portForwardEndpoint.LocalPort,
                                portForwardEndpoint.ServiceName));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    this.logger.LogWarning(
                        exception,
                        "KUBERNETES PORT-FORWARD START FAILED RuntimeInstanceId={RuntimeInstanceId} ServiceName={ServiceName} Reason={Reason}",
                        request.RuntimeInstanceId,
                        createResult.ServiceName,
                        exception.Message);

                    await this.DeleteOnFailureAsync(podSpec, cancellationToken).ConfigureAwait(false);

                    return CreateRejectedResult(
                        request,
                        $"kubernetes-port-forward-start-failed:{exception.Message}",
                        true,
                        metadata);
                }
            }

            var transportEndpoint =
                ResolveKubernetesTransportEndpoint(
                    request,
                    metadata);

            ValidateResolvedKubernetesTransportEndpoint(
                request,
                transportEndpoint);

            metadata =
                MergeMetadata(
                    metadata,
                    CreateTransportEndpointMetadata(transportEndpoint));

            Console.WriteLine(
                $"[KUBERNETES TRANSPORT ENDPOINT RESOLVED] RuntimeInstanceId='{request.RuntimeInstanceId}', RequestTransportEndpoint='{request.TransportEndpoint}', ResolvedTransportEndpoint='{transportEndpoint}', Metadata='{string.Join(";", metadata.Select(item => $"{item.Key}={item.Value}"))}'.");

            this.logger.LogInformation(
                "KUBERNETES HOST STARTED AFTER POD READINESS RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} TransportName={TransportName} RequestTransportEndpoint={RequestTransportEndpoint} ResolvedTransportEndpoint={ResolvedTransportEndpoint} RequireRuntimeReadiness={RequireRuntimeReadiness}",
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                transportEndpoint,
                this.options.RequireRuntimeReadiness);

            var startedResult =
                AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    transportEndpoint,
                    metadata);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME INSTANCE PUBLICATION BEGIN RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName,
                transportEndpoint);

            var runtimeReadinessFailure =
                await this.WaitForRuntimeReadinessBeforePublicationAsync(
                        request,
                        podSpec,
                        transportEndpoint,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(runtimeReadinessFailure))
            {
                this.StopPortForward(request.RuntimeInstanceId);

                await this.DeleteOnFailureAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

                return CreateRejectedResult(
                    request,
                    runtimeReadinessFailure,
                    true,
                    metadata);
            }

            await this.runtimeInstancePublisher
                .PublishAsync(request, startedResult, cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME INSTANCE PUBLICATION COMPLETED RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName,
                transportEndpoint);

            return startedResult;
        }

        /// <inheritdoc />
        public async Task<bool> KillAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            this.StopPortForward(runtimeInstanceId);

            if (!this.podSpecsByRuntimeInstanceId.TryRemove(runtimeInstanceId, out var podSpec))
            {
                this.logger.LogWarning(
                    "Kubernetes runtime host kill requested but no pod spec was registered. RuntimeInstanceId={RuntimeInstanceId}.",
                    runtimeInstanceId);

                return false;
            }

            this.logger.LogWarning(
                "Deleting Kubernetes runtime host on demand. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}.",
                runtimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace);

            var result =
                await this.client
                    .DeleteRuntimeHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogWarning(
                "Kubernetes runtime host delete completed. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}, Success={Success}, FailureReason={FailureReason}.",
                runtimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                result.Success,
                result.FailureReason ?? "(none)");

            if (result.Success)
            {
                await this.runtimeInstancePublisher
                    .UnpublishAsync(
                        runtimeInstanceId,
                        "kubernetes-runtime-host-killed",
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogWarning(
                    "Kubernetes runtime instance unpublished after runtime host kill. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}.",
                    runtimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace);
            }

            return result.Success;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var item in this.portForwardProcesses.ToArray())
            {
                this.StopPortForward(item.Key);
            }

            this.podSpecsByRuntimeInstanceId.Clear();
        }


        /// <summary>
        /// Determines whether the Kubernetes runtime host must expose a local port-forward endpoint.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns><see langword="true" /> when a local port-forward endpoint must be used.</returns>
        private bool ShouldUsePortForwardTransportEndpoint(
            AiRuntimeHostStartRequest request)
        {
            if (this.options.UsePortForwardTransportEndpoint)
            {
                return true;
            }

            if (IsGrpcRuntimeTransport(request))
            {
                return true;
            }

            return IsUnsafeLocalhostKubernetesEndpoint(
                request.TransportEndpoint);
        }

        /// <summary>
        /// Resolves the local TCP port used by kubectl port-forward.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The local TCP port.</returns>
        private int ResolvePortForwardLocalPort(
            AiRuntimeHostStartRequest request)
        {
            if (IsGrpcRuntimeTransport(request))
            {
                return GetFreeTcpPort();
            }

            if (this.options.PortForwardLocalPort > 0 &&
                this.portForwardProcesses.IsEmpty)
            {
                return this.options.PortForwardLocalPort;
            }

            return GetFreeTcpPort();
        }

        /// <summary>
        /// Determines whether the runtime transport is gRPC.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns><see langword="true" /> when the provider or transport is gRPC.</returns>
        private static bool IsGrpcRuntimeTransport(
            AiRuntimeHostStartRequest request)
        {
            return string.Equals(
                       request.ProviderName,
                       "grpc",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       request.TransportName,
                       "grpc",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a Kubernetes transport endpoint is unsafe for multi-pod dispatch.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns><see langword="true" /> when the endpoint is unsafe.</returns>
        private static bool IsUnsafeLocalhostKubernetesEndpoint(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return false;
            }

            return transportEndpoint.Contains(
                       "127.0.0.1:8080",
                       StringComparison.OrdinalIgnoreCase) ||
                   transportEndpoint.Contains(
                       "localhost:8080",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that the resolved Kubernetes transport endpoint is safe for dispatch.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        private static void ValidateResolvedKubernetesTransportEndpoint(
            AiRuntimeHostStartRequest request,
            string? transportEndpoint)
        {
            if (!IsGrpcRuntimeTransport(request))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC runtime endpoint was not resolved. RuntimeInstanceId='{request.RuntimeInstanceId}'.");
            }

            if (IsUnsafeLocalhostKubernetesEndpoint(transportEndpoint))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC runtime endpoint is not unique for multi-pod dispatch. RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{transportEndpoint}'. Enable port-forward or publish a unique NodePort/service endpoint.");
            }
        }

        /// <summary>
        /// Waits for the runtime command endpoint to become ready before the Kubernetes-backed runtime instance is published.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A failure reason when readiness fails; otherwise, <see langword="null"/>.</returns>
        private async Task<string?> WaitForRuntimeReadinessBeforePublicationAsync(
            AiRuntimeHostStartRequest request,
            AiKubernetesRuntimePodSpec podSpec,
            string? transportEndpoint,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (!this.options.RequireRuntimeReadiness)
            {
                return null;
            }

            var requireTransportEndpoint =
                IsGrpcRuntimeTransport(request) ||
                ShouldRequireTransportEndpoint(metadata);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME READINESS WAIT BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} RequireTransportEndpoint={RequireTransportEndpoint} Timeout={Timeout} PollInterval={PollInterval}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                request.ProviderName,
                request.TransportName,
                transportEndpoint,
                requireTransportEndpoint,
                this.options.ReadinessTimeout,
                this.options.ReadinessPollInterval);

            var readinessResult =
                await this.readinessWaiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ProviderName = request.ProviderName,
                            TransportName = request.TransportName,
                            TransportEndpoint = transportEndpoint,
                            RequireTransportEndpoint = requireTransportEndpoint,
                            Timeout = this.options.ReadinessTimeout,
                            PollInterval = this.options.ReadinessPollInterval
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME READINESS WAIT RESULT RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} Success={Success} TimedOut={TimedOut} FailureReason={FailureReason} TransportEndpoint={TransportEndpoint}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                readinessResult.Success,
                readinessResult.TimedOut,
                readinessResult.FailureReason ?? "(none)",
                readinessResult.TransportEndpoint ?? "(null)");

            if (readinessResult.Success)
            {
                return null;
            }

            return readinessResult.FailureReason ?? "kubernetes-runtime-command-readiness-failed";
        }

        /// <summary>
        /// Determines whether runtime readiness must require a transport endpoint.
        /// </summary>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns><see langword="true"/> when a transport endpoint is required.</returns>
        private static bool ShouldRequireTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata)
        {
            if (TryGetMetadataValue(metadata, "transport.name", out var transportName) &&
                string.Equals(transportName, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryGetMetadataValue(metadata, "provider.name", out var providerName) &&
                string.Equals(providerName, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryGetMetadataValue(metadata, "provider", out var providerAlias) &&
                string.Equals(providerAlias, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Starts a local kubectl port-forward process for a Kubernetes pod.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="serviceName">The Kubernetes service name, when available.</param>
        /// <param name="metadata">The Kubernetes metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The local port-forward endpoint.</returns>
        private async Task<KubernetesPortForwardEndpoint> StartPortForwardAsync(
            AiRuntimeHostStartRequest request,
            AiKubernetesRuntimePodSpec podSpec,
            string? serviceName,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(podSpec);
            ArgumentNullException.ThrowIfNull(metadata);

            serviceName =
                ResolveServiceName(
                    serviceName,
                    metadata) ?? string.Empty;

            await PortForwardStartupGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                /*
                 * Keep local port selection and the actual kubectl bind in one
                 * process-wide critical section. GetFreeTcpPort releases its
                 * temporary listener before kubectl binds, so independent test
                 * hosts could otherwise select the same port under parallel scale-out.
                 */
                this.StopPortForward(request.RuntimeInstanceId);

                var localPort =
                    this.ResolvePortForwardLocalPort(request);

                var kubectlPath =
                    string.IsNullOrWhiteSpace(this.options.KubectlPath)
                        ? "kubectl"
                        : this.options.KubectlPath;

                var processStartInfo =
                    new ProcessStartInfo
                    {
                        FileName = kubectlPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                processStartInfo.ArgumentList.Add("port-forward");
                processStartInfo.ArgumentList.Add("-n");
                processStartInfo.ArgumentList.Add(podSpec.Namespace);
                processStartInfo.ArgumentList.Add($"pod/{podSpec.PodName}");
                processStartInfo.ArgumentList.Add($"{localPort}:{podSpec.ContainerPort}");

                var process =
                    new DiagnosticsProcess
                    {
                        StartInfo = processStartInfo,
                        EnableRaisingEvents = true
                    };

                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    {
                        return;
                    }

                    this.logger.LogDebug(
                        "KUBERNETES PORT-FORWARD STDOUT RuntimeInstanceId={RuntimeInstanceId} LocalPort={LocalPort} Message={Message}",
                        request.RuntimeInstanceId,
                        localPort,
                        eventArgs.Data);
                };

                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    {
                        return;
                    }

                    this.logger.LogDebug(
                        "KUBERNETES PORT-FORWARD STDERR RuntimeInstanceId={RuntimeInstanceId} LocalPort={LocalPort} Message={Message}",
                        request.RuntimeInstanceId,
                        localPort,
                        eventArgs.Data);
                };

                this.logger.LogInformation(
                    "KUBERNETES PORT-FORWARD START BEGIN RuntimeInstanceId={RuntimeInstanceId} ServiceName={ServiceName} PodName={PodName} Namespace={Namespace} LocalPort={LocalPort} RemotePort={RemotePort} KubectlPath={KubectlPath}",
                    request.RuntimeInstanceId,
                    serviceName,
                    podSpec.PodName,
                    podSpec.Namespace,
                    localPort,
                    podSpec.ContainerPort,
                    kubectlPath);

                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException(
                        "kubectl port-forward process did not start.");
                }

                if (!this.portForwardProcesses.TryAdd(
                        request.RuntimeInstanceId,
                        process))
                {
                    KillProcess(process);
                    process.Dispose();

                    throw new InvalidOperationException(
                        $"A port-forward process is already registered for runtime instance '{request.RuntimeInstanceId}'.");
                }

                try
                {
                    /*
                     * Redirected stdout and stderr must be consumed continuously.
                     * kubectl writes one line for startup and for forwarded
                     * connections; an undrained pipe can fill and block the tunnel.
                     */
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await WaitForLocalPortOpenAsync(
                            process,
                            localPort,
                            this.options.PortForwardStartupTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    this.StopPortForward(request.RuntimeInstanceId);
                    throw;
                }

                var endpoint =
                    $"http://127.0.0.1:{localPort}";

                this.logger.LogInformation(
                    "KUBERNETES PORT-FORWARD STARTED RuntimeInstanceId={RuntimeInstanceId} ServiceName={ServiceName} PodName={PodName} Namespace={Namespace} LocalPort={LocalPort} RemotePort={RemotePort} ProcessId={ProcessId} Endpoint={Endpoint}",
                    request.RuntimeInstanceId,
                    serviceName,
                    podSpec.PodName,
                    podSpec.Namespace,
                    localPort,
                    podSpec.ContainerPort,
                    process.Id,
                    endpoint);

                Console.WriteLine(
                    $"[KUBERNETES PORT-FORWARD STARTED] RuntimeInstanceId='{request.RuntimeInstanceId}', ServiceName='{serviceName}', PodName='{podSpec.PodName}', Namespace='{podSpec.Namespace}', LocalPort='{localPort}', RemotePort='{podSpec.ContainerPort}', ProcessId='{process.Id}', Endpoint='{endpoint}'.");

                return new KubernetesPortForwardEndpoint(
                    serviceName,
                    localPort,
                    endpoint);
            }
            finally
            {
                PortForwardStartupGate.Release();
            }
        }

        /// <summary>
        /// Stops a local kubectl port-forward process for a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        private void StopPortForward(
            string runtimeInstanceId)
        {
            if (!this.portForwardProcesses.TryRemove(runtimeInstanceId, out var process))
            {
                return;
            }

            try
            {
                KillProcess(process);
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>
        /// Kills a process if it is still running.
        /// </summary>
        /// <param name="process">The process.</param>
        private static void KillProcess(
            DiagnosticsProcess process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Waits until the local kubectl port-forward process accepts TCP connections.
        /// </summary>
        /// <param name="process">The kubectl port-forward process.</param>
        /// <param name="localPort">The local TCP port.</param>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task WaitForLocalPortOpenAsync(
            DiagnosticsProcess process,
            int localPort,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(process);

            if (localPort <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPort),
                    localPort,
                    "The local port-forward port must be greater than zero.");
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The port-forward startup timeout must be greater than zero.");
            }

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"kubectl port-forward exited before its local endpoint became reachable. " +
                        $"ProcessId='{process.Id}', ExitCode='{process.ExitCode}', " +
                        $"LocalEndpoint='127.0.0.1:{localPort}'.");
                }

                try
                {
                    using var client =
                        new TcpClient();

                    using var probeCancellationTokenSource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);

                    probeCancellationTokenSource.CancelAfter(
                        TimeSpan.FromMilliseconds(500));

                    await client
                        .ConnectAsync(
                            IPAddress.Loopback,
                            localPort,
                            probeCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                    if (client.Connected)
                    {
                        if (process.HasExited)
                        {
                            throw new InvalidOperationException(
                                $"The local port became reachable, but kubectl port-forward exited. " +
                                $"ProcessId='{process.Id}', ExitCode='{process.ExitCode}', " +
                                $"LocalEndpoint='127.0.0.1:{localPort}'.");
                        }

                        return;
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                await Task
                    .Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"kubectl port-forward exited before startup completed. " +
                    $"ProcessId='{process.Id}', ExitCode='{process.ExitCode}', " +
                    $"LocalEndpoint='127.0.0.1:{localPort}'.");
            }

            throw new TimeoutException(
                $"Local kubectl port-forward endpoint did not become reachable on " +
                $"'127.0.0.1:{localPort}' within '{timeout}'. " +
                $"ProcessId='{process.Id}', HasExited='{process.HasExited}'.");
        }

        /// <summary>
        /// Gets a free local TCP port.
        /// </summary>
        /// <returns>The free TCP port.</returns>
        private static int GetFreeTcpPort()
        {
            var listener =
                new TcpListener(
                    IPAddress.Loopback,
                    0);

            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Resolves the Kubernetes service name.
        /// </summary>
        /// <param name="serviceName">The service name returned by the Kubernetes client.</param>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The Kubernetes service name, when available.</returns>
        private static string? ResolveServiceName(
            string? serviceName,
            IReadOnlyDictionary<string, string> metadata)
        {
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                return serviceName;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.name", out var metadataServiceName))
            {
                return metadataServiceName;
            }

            return null;
        }

        /// <summary>
        /// Creates local port-forward transport endpoint metadata.
        /// </summary>
        /// <param name="transportEndpoint">The local transport endpoint.</param>
        /// <param name="localPort">The local port.</param>
        /// <param name="serviceName">The service name.</param>
        /// <returns>The transport endpoint metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreatePortForwardTransportEndpointMetadata(
            string transportEndpoint,
            int localPort,
            string serviceName)
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint,
                ["transport.endpoint"] = transportEndpoint,
                ["transportEndpoint"] = transportEndpoint,
                ["kubernetes.portForward.endpoint"] = transportEndpoint,
                ["kubernetes.portForward.localPort"] = localPort.ToString(),
                ["kubernetes.portForward.serviceName"] = serviceName,
                ["kubernetes.transport.endpoint.source"] = "port-forward"
            };
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
                    .DeleteRuntimeHostAsync(podSpec, cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST DELETE ON FAILURE RESULT PodName={PodName} Namespace={Namespace} Success={Success} FailureReason={FailureReason}",
                podSpec.PodName,
                podSpec.Namespace,
                deleteResult.Success,
                deleteResult.FailureReason ?? "(none)");
        }

        /// <summary>
        /// Resolves the transport endpoint that should be published for a Kubernetes-backed runtime.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="metadata">The Kubernetes host metadata.</param>
        /// <returns>The resolved transport endpoint.</returns>
        private static string? ResolveKubernetesTransportEndpoint(
            AiRuntimeHostStartRequest request,
            IReadOnlyDictionary<string, string> metadata)
        {
            if (TryGetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint, out var transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "transport.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "transportEndpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.portForward.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.nodePort.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.url", out transportEndpoint))
            {
                return transportEndpoint;
            }

            return request.TransportEndpoint;
        }

        /// <summary>
        /// Creates transport endpoint metadata using all known aliases.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns>The transport endpoint metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateTransportEndpointMetadata(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint,
                ["transport.endpoint"] = transportEndpoint,
                ["transportEndpoint"] = transportEndpoint
            };
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><see langword="true"/> when the value exists.</returns>
        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out string value)
        {
            if (metadata.TryGetValue(key, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Value))
                {
                    value = item.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
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

        /// <summary>
        /// Represents a local Kubernetes port-forward endpoint.
        /// </summary>
        private sealed class KubernetesPortForwardEndpoint
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="KubernetesPortForwardEndpoint"/> class.
            /// </summary>
            /// <param name="serviceName">The Kubernetes service name, when available.</param>
            /// <param name="localPort">The local port.</param>
            /// <param name="endpoint">The local transport endpoint.</param>
            public KubernetesPortForwardEndpoint(
                string serviceName,
                int localPort,
                string endpoint)
            {
                ServiceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
                LocalPort = localPort;
                Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            }

            /// <summary>
            /// Gets the Kubernetes service name.
            /// </summary>
            public string ServiceName { get; }

            /// <summary>
            /// Gets the local port.
            /// </summary>
            public int LocalPort { get; }

            /// <summary>
            /// Gets the local endpoint.
            /// </summary>
            public string Endpoint { get; }
        }
    }
}