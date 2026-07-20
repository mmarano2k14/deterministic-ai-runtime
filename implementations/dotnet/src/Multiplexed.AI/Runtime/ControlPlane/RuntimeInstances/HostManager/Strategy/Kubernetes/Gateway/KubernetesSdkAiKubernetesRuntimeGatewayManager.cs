using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Ensures the shared Kubernetes runtime Gateway through the Kubernetes SDK.
    /// </summary>
    /// <remarks>
    /// The operation is idempotent and safe under concurrent callers:
    /// it reads the Gateway first, creates it when missing, and converges on the existing
    /// resource when another caller wins the Kubernetes create race.
    ///
    /// The Gateway API CRDs and controller deployment remain cluster prerequisites.
    /// The manager can create the configured GatewayClass dynamically and waits for
    /// the controller to accept it before creating the shared Gateway.
    /// </remarks>
    public sealed partial class KubernetesSdkAiKubernetesRuntimeGatewayManager : IAiKubernetesRuntimeGatewayManager
    {
        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly Lazy<IAiKubernetesSdkClient> lazyClient;

        /// <summary>
        /// Gets the Kubernetes SDK operation boundary, created only when Gateway
        /// lifecycle work is actually requested.
        /// </summary>
        private IAiKubernetesSdkClient client => this.lazyClient.Value;
        private readonly AiKubernetesGatewayResourceFactory resourceFactory;
        private readonly ILogger<KubernetesSdkAiKubernetesRuntimeGatewayManager> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesSdkAiKubernetesRuntimeGatewayManager"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="clientFactory">The Kubernetes SDK client factory.</param>
        /// <param name="resourceFactory">The Gateway API resource factory.</param>
        /// <param name="logger">The logger.</param>
        public KubernetesSdkAiKubernetesRuntimeGatewayManager(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            IKubernetesClientFactory clientFactory,
            AiKubernetesGatewayResourceFactory resourceFactory,
            ILogger<KubernetesSdkAiKubernetesRuntimeGatewayManager> logger)
        {
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            ArgumentNullException.ThrowIfNull(clientFactory);

            this.lazyClient =
                new Lazy<IAiKubernetesSdkClient>(
                    clientFactory.CreateClient,
                    LazyThreadSafetyMode.ExecutionAndPublication);
            this.resourceFactory = resourceFactory ?? throw new ArgumentNullException(nameof(resourceFactory));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesGatewayEndpoint> EnsureGatewayAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            cancellationToken.ThrowIfCancellationRequested();

            this.ValidateOptions();

            var gatewayName =
                this.resourceFactory.CreateGatewayName();

            var deadline =
                DateTimeOffset.UtcNow.Add(this.options.GatewayReadinessTimeout);

            this.logger.LogInformation(
                "KUBERNETES GATEWAY ENSURE BEGIN GatewayName={GatewayName} GatewayClassName={GatewayClassName} GatewayControllerName={GatewayControllerName} Namespace={Namespace} ListenerName={ListenerName} ListenerPort={ListenerPort} CreateGatewayClassWhenMissing={CreateGatewayClassWhenMissing} CreateGatewayWhenMissing={CreateGatewayWhenMissing} RequireProgrammed={RequireProgrammed} ControlPlaneId={ControlPlaneId}",
                gatewayName,
                this.options.GatewayClassName,
                this.options.GatewayControllerName,
                this.options.Namespace,
                this.resourceFactory.CreateListenerName(),
                this.options.GatewayPort,
                this.options.CreateGatewayClassWhenMissing,
                this.options.CreateGatewayWhenMissing,
                this.options.RequireGatewayProgrammed,
                controlPlaneId);

            _ =
                await this.EnsureGatewayClassAsync(
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

            var gateway =
                await this.TryReadGatewayAsync(
                        gatewayName,
                        cancellationToken)
                    .ConfigureAwait(false);

            var created = false;

            if (gateway is null)
            {
                if (!this.options.CreateGatewayWhenMissing)
                {
                    throw new InvalidOperationException(
                        $"kubernetes-gateway-missing: Gateway '{this.options.Namespace}/{gatewayName}' does not exist and CreateGatewayWhenMissing is disabled.");
                }

                var desiredGateway =
                    this.resourceFactory.CreateGateway(controlPlaneId);

                try
                {
                    gateway =
                        await this.client
                            .CreateNamespacedCustomObjectAsync(
                                desiredGateway,
                                AiKubernetesGatewayNames.ApiGroup,
                                AiKubernetesGatewayNames.ApiVersion,
                                this.options.Namespace,
                                AiKubernetesGatewayNames.GatewayPlural,
                                cancellationToken)
                            .ConfigureAwait(false);

                    created = true;

                    this.logger.LogInformation(
                        "KUBERNETES GATEWAY CREATED GatewayName={GatewayName} GatewayClassName={GatewayClassName} Namespace={Namespace} ControlPlaneId={ControlPlaneId}",
                        gatewayName,
                        this.options.GatewayClassName,
                        this.options.Namespace,
                        controlPlaneId);
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    this.logger.LogInformation(
                        "KUBERNETES GATEWAY CREATE CONVERGED GatewayName={GatewayName} Namespace={Namespace} Reason=already-exists",
                        gatewayName,
                        this.options.Namespace);

                    gateway =
                        await this.ReadGatewayRequiredAsync(
                                gatewayName,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            else
            {
                this.logger.LogInformation(
                    "KUBERNETES GATEWAY REUSED GatewayName={GatewayName} GatewayClassName={GatewayClassName} Namespace={Namespace}",
                    gatewayName,
                    gateway.Spec?.GatewayClassName,
                    this.options.Namespace);
            }

            ValidateGatewayIdentity(gateway, this.options, this.resourceFactory);

            gateway =
                await this.WaitUntilGatewayReadyAsync(
                        gatewayName,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

            var gatewayService =
                await this.WaitUntilGatewayServiceAvailableAsync(
                        gatewayName,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

            var servicePort =
                ResolveGatewayServicePort(
                    gatewayService,
                    this.options.GatewayPort);

            var serviceName =
                gatewayService.Metadata?.Name ??
                throw new InvalidOperationException(
                    $"kubernetes-gateway-service-name-missing: Gateway Service for '{this.options.Namespace}/{gatewayName}' does not expose metadata.name.");

            var serviceNamespace =
                gatewayService.Metadata?.NamespaceProperty;

            if (string.IsNullOrWhiteSpace(serviceNamespace))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-service-namespace-missing: Gateway Service '{serviceName}' for Gateway '{this.options.Namespace}/{gatewayName}' does not expose metadata.namespace.");
            }

            var internalEndpoint =
                $"http://{serviceName}.{serviceNamespace}.svc.cluster.local:{servicePort}";

            this.logger.LogInformation(
                "KUBERNETES GATEWAY ENSURE COMPLETED GatewayName={GatewayName} GatewayClassName={GatewayClassName} GatewayNamespace={GatewayNamespace} ServiceName={ServiceName} ServiceNamespace={ServiceNamespace} ServicePort={ServicePort} InternalEndpoint={InternalEndpoint} Created={Created}",
                gatewayName,
                gateway.Spec.GatewayClassName,
                this.options.Namespace,
                serviceName,
                serviceNamespace,
                servicePort,
                internalEndpoint,
                created);

            return new AiKubernetesGatewayEndpoint
            {
                Namespace = this.options.Namespace,
                GatewayName = gatewayName,
                GatewayClassName = gateway.Spec.GatewayClassName,
                ListenerName = this.resourceFactory.CreateListenerName(),
                ListenerPort = this.options.GatewayPort,
                ServiceName = serviceName,
                ServiceNamespace = serviceNamespace,
                ServicePort = servicePort,
                InternalEndpoint = internalEndpoint
            };
        }

        /// <summary>
        /// Ensures the configured GatewayClass exists and is accepted by its controller.
        /// </summary>
        private async Task<AiKubernetesGatewayClassResource> EnsureGatewayClassAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            var gatewayClass =
                await this.TryReadGatewayClassAsync(cancellationToken)
                    .ConfigureAwait(false);

            var created = false;

            if (gatewayClass is null)
            {
                if (!this.options.CreateGatewayClassWhenMissing)
                {
                    throw new InvalidOperationException(
                        $"kubernetes-gateway-class-missing: GatewayClass '{this.options.GatewayClassName}' does not exist and CreateGatewayClassWhenMissing is disabled.");
                }

                var desiredGatewayClass =
                    this.resourceFactory.CreateGatewayClass();

                try
                {
                    gatewayClass =
                        await this.client
                            .CreateClusterCustomObjectAsync(
                                desiredGatewayClass,
                                AiKubernetesGatewayNames.ApiGroup,
                                AiKubernetesGatewayNames.ApiVersion,
                                AiKubernetesGatewayNames.GatewayClassPlural,
                                cancellationToken)
                            .ConfigureAwait(false);

                    created = true;

                    this.logger.LogInformation(
                        "KUBERNETES GATEWAY CLASS CREATED GatewayClassName={GatewayClassName} GatewayControllerName={GatewayControllerName}",
                        this.options.GatewayClassName,
                        this.options.GatewayControllerName);
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    this.logger.LogInformation(
                        "KUBERNETES GATEWAY CLASS CREATE CONVERGED GatewayClassName={GatewayClassName} Reason=already-exists",
                        this.options.GatewayClassName);

                    gatewayClass =
                        await this.ReadGatewayClassRequiredAsync(cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            else
            {
                this.logger.LogInformation(
                    "KUBERNETES GATEWAY CLASS REUSED GatewayClassName={GatewayClassName} GatewayControllerName={GatewayControllerName}",
                    gatewayClass.Metadata?.Name,
                    gatewayClass.Spec?.ControllerName);
            }

            ValidateGatewayClassIdentity(
                gatewayClass,
                this.options.GatewayClassName,
                this.options.GatewayControllerName);

            gatewayClass =
                await this.WaitUntilGatewayClassAcceptedAsync(
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES GATEWAY CLASS ENSURE COMPLETED GatewayClassName={GatewayClassName} GatewayControllerName={GatewayControllerName} Created={Created}",
                gatewayClass.Metadata?.Name,
                gatewayClass.Spec?.ControllerName,
                created);

            return gatewayClass;
        }

        /// <summary>
        /// Reads the configured GatewayClass when it exists.
        /// </summary>
        private async Task<AiKubernetesGatewayClassResource?> TryReadGatewayClassAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client
                    .ReadClusterCustomObjectAsync<AiKubernetesGatewayClassResource>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        AiKubernetesGatewayNames.GatewayClassPlural,
                        this.options.GatewayClassName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the configured GatewayClass and fails when it is unexpectedly missing.
        /// </summary>
        private async Task<AiKubernetesGatewayClassResource> ReadGatewayClassRequiredAsync(
            CancellationToken cancellationToken)
        {
            var gatewayClass =
                await this.TryReadGatewayClassAsync(cancellationToken)
                    .ConfigureAwait(false);

            return gatewayClass ??
                   throw new InvalidOperationException(
                       $"kubernetes-gateway-class-convergence-read-missing: GatewayClass '{this.options.GatewayClassName}' was reported as existing but could not be read.");
        }

        /// <summary>
        /// Waits until the Gateway controller accepts the configured GatewayClass.
        /// </summary>
        private async Task<AiKubernetesGatewayClassResource> WaitUntilGatewayClassAcceptedAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            string? lastConditionSummary = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gatewayClass =
                    await this.ReadGatewayClassRequiredAsync(cancellationToken)
                        .ConfigureAwait(false);

                ValidateGatewayClassIdentity(
                    gatewayClass,
                    this.options.GatewayClassName,
                    this.options.GatewayControllerName);

                if (HasTrueCondition(
                        gatewayClass.Status?.Conditions,
                        AiKubernetesGatewayNames.AcceptedConditionType))
                {
                    return gatewayClass;
                }

                lastConditionSummary =
                    CreateConditionSummary(gatewayClass.Status?.Conditions);

                await DelayUntilNextPollAsync(
                        deadline,
                        this.options.GatewayReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"kubernetes-gateway-class-readiness-timeout: GatewayClass '{this.options.GatewayClassName}' was not Accepted=True within '{this.options.GatewayReadinessTimeout}'. " +
                $"ControllerName='{this.options.GatewayControllerName}', Conditions='{lastConditionSummary ?? "none"}'.");
        }

        /// <summary>
        /// Reads the shared Gateway when it exists.
        /// </summary>
        private async Task<AiKubernetesGatewayResource?> TryReadGatewayAsync(
            string gatewayName,
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client
                    .ReadNamespacedCustomObjectAsync<AiKubernetesGatewayResource>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        this.options.Namespace,
                        AiKubernetesGatewayNames.GatewayPlural,
                        gatewayName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the shared Gateway and fails when it is unexpectedly missing.
        /// </summary>
        private async Task<AiKubernetesGatewayResource> ReadGatewayRequiredAsync(
            string gatewayName,
            CancellationToken cancellationToken)
        {
            var gateway =
                await this.TryReadGatewayAsync(
                        gatewayName,
                        cancellationToken)
                    .ConfigureAwait(false);

            return gateway ??
                   throw new InvalidOperationException(
                       $"kubernetes-gateway-convergence-read-missing: Gateway '{this.options.Namespace}/{gatewayName}' was reported as existing but could not be read.");
        }

        /// <summary>
        /// Waits until the shared Gateway is usable for runtime routing.
        /// </summary>
        /// <remarks>
        /// A production Gateway normally requires the top-level
        /// <c>Programmed=True</c> condition.
        ///
        /// In local port-forward mode, a controller-managed LoadBalancer Service can
        /// remain without an external address and therefore report
        /// <c>Programmed=False/AddressNotAssigned</c>, even though the listener,
        /// Envoy data plane, and backing Service are ready. In that precise case the
        /// Service is the transport boundary and its presence is sufficient because
        /// kubectl port-forward does not use the external LoadBalancer address.
        /// </remarks>
        private async Task<AiKubernetesGatewayResource> WaitUntilGatewayReadyAsync(
            string gatewayName,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            string? lastConditionSummary = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gateway =
                    await this.ReadGatewayRequiredAsync(
                            gatewayName,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateGatewayIdentity(gateway, this.options, this.resourceFactory);

                var accepted =
                    HasTrueCondition(
                        gateway.Status?.Conditions,
                        AiKubernetesGatewayNames.AcceptedConditionType);

                var programmed =
                    HasTrueCondition(
                        gateway.Status?.Conditions,
                        AiKubernetesGatewayNames.ProgrammedConditionType);

                var listenerReady =
                    IsListenerReady(
                        gateway,
                        this.resourceFactory.CreateListenerName());

                var addressNotAssigned =
                    HasCondition(
                        gateway.Status?.Conditions,
                        AiKubernetesGatewayNames.ProgrammedConditionType,
                        AiKubernetesGatewayNames.FalseConditionStatus,
                        AiKubernetesGatewayNames.AddressNotAssignedReason);

                var localPortForwardServiceReady = false;

                if (accepted &&
                    listenerReady &&
                    this.options.RequireGatewayProgrammed &&
                    !programmed &&
                    this.options.UsePortForwardTransportEndpoint &&
                    addressNotAssigned)
                {
                    var gatewayService =
                        await this.TryResolveGatewayServiceAsync(
                                gatewayName,
                                cancellationToken)
                            .ConfigureAwait(false);

                    localPortForwardServiceReady =
                        gatewayService is not null &&
                        HasGatewayServicePort(
                            gatewayService,
                            this.options.GatewayPort);
                }

                if (accepted &&
                    listenerReady &&
                    (!this.options.RequireGatewayProgrammed ||
                     programmed ||
                     localPortForwardServiceReady))
                {
                    if (localPortForwardServiceReady)
                    {
                        this.logger.LogInformation(
                            "KUBERNETES GATEWAY READY THROUGH LOCAL SERVICE GatewayName={GatewayName} Namespace={Namespace} Reason=AddressNotAssigned PortForwardEnabled=True ListenerReady=True",
                            gatewayName,
                            this.options.Namespace);
                    }

                    return gateway;
                }

                lastConditionSummary =
                    CreateGatewayConditionSummary(gateway);

                await DelayUntilNextPollAsync(
                        deadline,
                        this.options.GatewayReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"kubernetes-gateway-readiness-timeout: Gateway '{this.options.Namespace}/{gatewayName}' did not become ready within '{this.options.GatewayReadinessTimeout}'. " +
                $"RequireProgrammed='{this.options.RequireGatewayProgrammed}', UsePortForward='{this.options.UsePortForwardTransportEndpoint}', Conditions='{lastConditionSummary ?? "none"}'.");
        }

        /// <summary>
        /// Waits until the Gateway controller exposes a Service for the shared Gateway.
        /// </summary>
        private async Task<V1Service> WaitUntilGatewayServiceAvailableAsync(
            string gatewayName,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gatewayService =
                    await this.TryResolveGatewayServiceAsync(
                            gatewayName,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (gatewayService is not null &&
                    HasGatewayServicePort(gatewayService, this.options.GatewayPort))
                {
                    return gatewayService;
                }

                await DelayUntilNextPollAsync(
                        deadline,
                        this.options.GatewayReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"kubernetes-gateway-service-timeout: No Kubernetes Service exposing listener port '{this.options.GatewayPort}' was resolved for Gateway '{this.options.Namespace}/{gatewayName}' within '{this.options.GatewayReadinessTimeout}'.");
        }

        /// <summary>
        /// Resolves the configured or controller-created Gateway Service.
        /// </summary>
        private async Task<V1Service?> TryResolveGatewayServiceAsync(
            string gatewayName,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(this.options.GatewayServiceName))
            {
                var configuredServiceNamespace =
                    string.IsNullOrWhiteSpace(this.options.GatewayServiceNamespace)
                        ? this.options.Namespace
                        : this.options.GatewayServiceNamespace.Trim();

                try
                {
                    return await this.client
                        .ReadServiceAsync(
                            this.options.GatewayServiceName,
                            configuredServiceNamespace,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsNotFound(exception))
                {
                    return null;
                }
            }

            var discoveredServices =
                new List<V1Service>();

            var standardLabelSelector =
                $"{AiKubernetesGatewayNames.GatewayNameLabel}={gatewayName}";

            /*
             * Prefer the portable Gateway ownership label. Search the Gateway
             * namespace first, then the full cluster because a controller can own
             * its data-plane Service in another namespace.
             */
            discoveredServices.AddRange(
                await this.client
                    .ListServicesAsync(
                        this.options.Namespace,
                        standardLabelSelector,
                        cancellationToken)
                    .ConfigureAwait(false));

            discoveredServices.AddRange(
                await this.client
                    .ListServicesForAllNamespacesAsync(
                        standardLabelSelector,
                        cancellationToken)
                    .ConfigureAwait(false));

            /*
             * Envoy Gateway labels controller-owned Services with the owning Gateway
             * namespace and name.
             */
            var envoyLabelSelector =
                $"{AiKubernetesGatewayNames.EnvoyOwningGatewayNamespaceLabel}={this.options.Namespace}," +
                $"{AiKubernetesGatewayNames.EnvoyOwningGatewayNameLabel}={gatewayName}";

            discoveredServices.AddRange(
                await this.client
                    .ListServicesForAllNamespacesAsync(
                        envoyLabelSelector,
                        cancellationToken)
                    .ConfigureAwait(false));

            return discoveredServices
                .Where(service => HasGatewayServicePort(service, this.options.GatewayPort))
                .GroupBy(
                    service =>
                        $"{service.Metadata?.NamespaceProperty}/{service.Metadata?.Name}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(service =>
                    string.Equals(
                        service.Metadata?.NamespaceProperty,
                        this.options.Namespace,
                        StringComparison.OrdinalIgnoreCase))
                .ThenBy(service => service.Metadata?.CreationTimestamp)
                .ThenBy(service => service.Metadata?.NamespaceProperty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(service => service.Metadata?.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>
        /// Validates options required by Gateway mode.
        /// </summary>
        private void ValidateOptions()
        {
            if (!this.options.UseGatewayTransportEndpoint)
            {
                throw new InvalidOperationException(
                    "kubernetes-gateway-disabled: UseGatewayTransportEndpoint must be enabled before ensuring the shared runtime Gateway.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayClassName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayControllerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayListenerName);

            if (this.options.GatewayPort <= 0 || this.options.GatewayPort > 65535)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-port-invalid: GatewayPort must be between 1 and 65535. ConfiguredPort='{this.options.GatewayPort}'.");
            }

            if (this.options.GatewayReadinessTimeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-readiness-timeout-invalid: GatewayReadinessTimeout must be greater than zero. ConfiguredTimeout='{this.options.GatewayReadinessTimeout}'.");
            }

            if (this.options.GatewayReadinessPollInterval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-readiness-poll-invalid: GatewayReadinessPollInterval must be greater than zero. ConfiguredPollInterval='{this.options.GatewayReadinessPollInterval}'.");
            }
        }

        /// <summary>
        /// Validates the immutable identity of a GatewayClass.
        /// </summary>
        private static void ValidateGatewayClassIdentity(
            AiKubernetesGatewayClassResource gatewayClass,
            string expectedGatewayClassName,
            string expectedGatewayControllerName)
        {
            ArgumentNullException.ThrowIfNull(gatewayClass);

            if (!string.Equals(
                    gatewayClass.Metadata?.Name,
                    expectedGatewayClassName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-class-identity-mismatch: Expected GatewayClass '{expectedGatewayClassName}', actual '{gatewayClass.Metadata?.Name ?? "(null)"}'.");
            }

            if (!string.Equals(
                    gatewayClass.Spec?.ControllerName,
                    expectedGatewayControllerName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-class-controller-mismatch: GatewayClass '{expectedGatewayClassName}' uses controller '{gatewayClass.Spec?.ControllerName ?? "(null)"}', expected '{expectedGatewayControllerName}'.");
            }
        }

        /// <summary>
        /// Validates that an existing Gateway matches the configured shared infrastructure identity.
        /// </summary>
        private static void ValidateGatewayIdentity(
            AiKubernetesGatewayResource gateway,
            AiKubernetesRuntimeHostOptions options,
            AiKubernetesGatewayResourceFactory resourceFactory)
        {
            ArgumentNullException.ThrowIfNull(gateway);

            var expectedGatewayName =
                resourceFactory.CreateGatewayName();

            if (!string.Equals(
                    gateway.Metadata?.Name,
                    expectedGatewayName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-name-mismatch: Expected '{expectedGatewayName}', actual '{gateway.Metadata?.Name ?? "(null)"}'.");
            }

            if (!string.Equals(
                    gateway.Metadata?.NamespaceProperty,
                    options.Namespace,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-namespace-mismatch: Expected '{options.Namespace}', actual '{gateway.Metadata?.NamespaceProperty ?? "(null)"}'.");
            }

            if (!string.Equals(
                    gateway.Spec?.GatewayClassName,
                    options.GatewayClassName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-class-mismatch: Gateway '{options.Namespace}/{expectedGatewayName}' uses GatewayClass '{gateway.Spec?.GatewayClassName ?? "(null)"}', expected '{options.GatewayClassName}'.");
            }

            ValidateManagedGatewayLabels(gateway.Metadata?.Labels, options.Namespace, expectedGatewayName);

            var expectedListenerName =
                resourceFactory.CreateListenerName();

            var listener =
                gateway.Spec?.Listeners?.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.Name,
                            expectedListenerName,
                            StringComparison.OrdinalIgnoreCase));

            if (listener is null)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-listener-missing: Gateway '{options.Namespace}/{expectedGatewayName}' does not contain listener '{expectedListenerName}'.");
            }

            if (!string.Equals(
                    listener.Protocol,
                    AiKubernetesGatewayNames.HttpListenerProtocol,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-listener-protocol-mismatch: Gateway '{options.Namespace}/{expectedGatewayName}' listener '{expectedListenerName}' uses protocol '{listener.Protocol}', expected '{AiKubernetesGatewayNames.HttpListenerProtocol}'.");
            }

            if (listener.Port != options.GatewayPort)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-listener-port-mismatch: Gateway '{options.Namespace}/{expectedGatewayName}' listener '{expectedListenerName}' exposes port '{listener.Port}', expected '{options.GatewayPort}'.");
            }

            var allowedKinds =
                listener.AllowedRoutes?.Kinds ??
                Array.Empty<AiKubernetesGatewayRouteGroupKind>();

            if (!ContainsAllowedRouteKind(allowedKinds, AiKubernetesGatewayNames.HttpRouteKind) ||
                !ContainsAllowedRouteKind(allowedKinds, AiKubernetesGatewayNames.GrpcRouteKind))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-route-kinds-mismatch: Gateway '{options.Namespace}/{expectedGatewayName}' listener '{expectedListenerName}' must allow both HTTPRoute and GRPCRoute.");
            }
        }

        /// <summary>
        /// Validates labels only when the existing Gateway declares itself managed.
        /// </summary>
        private static void ValidateManagedGatewayLabels(
            IDictionary<string, string>? labels,
            string namespaceName,
            string gatewayName)
        {
            if (labels is null ||
                !labels.TryGetValue(AiKubernetesGatewayNames.ManagedByLabel, out var managedBy))
            {
                return;
            }

            if (!string.Equals(
                    managedBy,
                    AiKubernetesGatewayNames.ManagedByValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-owner-collision: Gateway '{namespaceName}/{gatewayName}' is managed by '{managedBy}', not '{AiKubernetesGatewayNames.ManagedByValue}'.");
            }

            if (labels.TryGetValue(AiKubernetesGatewayNames.ComponentLabel, out var component) &&
                !string.Equals(
                    component,
                    AiKubernetesGatewayNames.GatewayComponentValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-component-collision: Gateway '{namespaceName}/{gatewayName}' has component '{component}', not '{AiKubernetesGatewayNames.GatewayComponentValue}'.");
            }
        }

        /// <summary>
        /// Determines whether an allowed-route list contains a Gateway API kind.
        /// </summary>
        private static bool ContainsAllowedRouteKind(
            IEnumerable<AiKubernetesGatewayRouteGroupKind> allowedKinds,
            string expectedKind)
        {
            return allowedKinds.Any(item =>
                string.Equals(
                    item.Group,
                    AiKubernetesGatewayNames.ApiGroup,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.Kind,
                    expectedKind,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Determines whether the configured listener has been accepted, programmed,
        /// and has resolved all referenced resources.
        /// </summary>
        private static bool IsListenerReady(
            AiKubernetesGatewayResource gateway,
            string listenerName)
        {
            var listenerStatus =
                gateway.Status?.Listeners?.FirstOrDefault(item =>
                        string.Equals(
                            item.Name,
                            listenerName,
                            StringComparison.OrdinalIgnoreCase));

            return listenerStatus is not null &&
                   HasTrueCondition(
                       listenerStatus.Conditions,
                       AiKubernetesGatewayNames.AcceptedConditionType) &&
                   HasTrueCondition(
                       listenerStatus.Conditions,
                       AiKubernetesGatewayNames.ProgrammedConditionType) &&
                   HasTrueCondition(
                       listenerStatus.Conditions,
                       AiKubernetesGatewayNames.ResolvedRefsConditionType);
        }

        /// <summary>
        /// Determines whether a condition collection contains the requested True condition.
        /// </summary>
        private static bool HasTrueCondition(
            IEnumerable<AiKubernetesGatewayCondition>? conditions,
            string conditionType)
        {
            return HasCondition(
                conditions,
                conditionType,
                AiKubernetesGatewayNames.TrueConditionStatus);
        }

        /// <summary>
        /// Determines whether a condition collection contains an exact type, status,
        /// and optional reason combination.
        /// </summary>
        private static bool HasCondition(
            IEnumerable<AiKubernetesGatewayCondition>? conditions,
            string conditionType,
            string conditionStatus,
            string? conditionReason = null)
        {
            return conditions?.Any(condition =>
                       string.Equals(
                           condition.Type,
                           conditionType,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           condition.Status,
                           conditionStatus,
                           StringComparison.OrdinalIgnoreCase) &&
                       (conditionReason is null ||
                        string.Equals(
                            condition.Reason,
                            conditionReason,
                            StringComparison.OrdinalIgnoreCase))) == true;
        }

        /// <summary>
        /// Creates a compact Gateway condition summary for timeout diagnostics.
        /// </summary>
        private static string CreateGatewayConditionSummary(
            AiKubernetesGatewayResource gateway)
        {
            var gatewayConditions =
                CreateConditionSummary(gateway.Status?.Conditions);

            var listenerConditions =
                gateway.Status?.Listeners is null
                    ? "none"
                    : string.Join(
                        ";",
                        gateway.Status.Listeners.Select(listener =>
                            $"listener={listener.Name}[{CreateConditionSummary(listener.Conditions)}]"));

            return $"gateway=[{gatewayConditions}],listeners=[{listenerConditions}]";
        }

        /// <summary>
        /// Creates a compact condition summary.
        /// </summary>
        private static string CreateConditionSummary(
            IEnumerable<AiKubernetesGatewayCondition>? conditions)
        {
            if (conditions is null)
            {
                return "none";
            }

            var values =
                conditions.Select(condition =>
                    $"{condition.Type}:{condition.Status}:{condition.Reason ?? "(none)"}")
                .ToArray();

            return values.Length == 0
                ? "none"
                : string.Join("|", values);
        }

        /// <summary>
        /// Determines whether a Kubernetes Service exposes the configured Gateway listener port.
        /// </summary>
        private static bool HasGatewayServicePort(
            V1Service service,
            int gatewayPort)
        {
            return service.Spec?.Ports?.Any(port => port.Port == gatewayPort) == true;
        }

        /// <summary>
        /// Resolves the Service port matching the configured Gateway listener port.
        /// </summary>
        private static int ResolveGatewayServicePort(
            V1Service service,
            int gatewayPort)
        {
            var servicePort =
                service.Spec?.Ports?.FirstOrDefault(port => port.Port == gatewayPort);

            return servicePort?.Port ??
                   throw new InvalidOperationException(
                       $"kubernetes-gateway-service-port-missing: Service '{service.Metadata?.NamespaceProperty}/{service.Metadata?.Name}' does not expose Gateway listener port '{gatewayPort}'.");
        }

        /// <summary>
        /// Delays until the next readiness poll without passing the shared deadline.
        /// </summary>
        private static async Task DelayUntilNextPollAsync(
            DateTimeOffset deadline,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            var remaining =
                deadline - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var delay =
                remaining < pollInterval
                    ? remaining
                    : pollInterval;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Determines whether an exception represents a Kubernetes resource not found response.
        /// </summary>
        private static bool IsNotFound(
            Exception exception)
        {
            return exception is KeyNotFoundException ||
                   exception is HttpOperationException httpOperationException &&
                   httpOperationException.Response?.StatusCode == HttpStatusCode.NotFound;
        }
    }
}
