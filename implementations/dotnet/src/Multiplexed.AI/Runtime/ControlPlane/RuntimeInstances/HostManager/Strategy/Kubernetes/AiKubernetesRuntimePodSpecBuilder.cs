using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Builds runtime-owned Kubernetes pod specifications for runtime host creation.
    /// </summary>
    /// <remarks>
    /// The builder creates a provider-neutral pod description.
    /// It does not call Kubernetes, does not dispatch work, and does not mark scale-out requests as fulfilled.
    /// Kubernetes is only the host lifecycle provider; the runtime provider and transport remain owned by the request.
    /// </remarks>
    public sealed class AiKubernetesRuntimePodSpecBuilder
    {
        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly AiKubernetesRuntimePodMetadataBuilder metadataBuilder;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiKubernetesRuntimePodSpecBuilder"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="metadataBuilder">The Kubernetes pod metadata builder.</param>
        public AiKubernetesRuntimePodSpecBuilder(
            AiKubernetesRuntimeHostOptions options,
            AiKubernetesRuntimePodMetadataBuilder metadataBuilder)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        }

        /// <summary>
        /// Builds a Kubernetes runtime pod specification from a runtime host start request.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The generated Kubernetes runtime pod specification.</returns>
        public AiKubernetesRuntimePodSpec Build(
            AiRuntimeHostStartRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.RuntimeImage);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.ContainerName);

            if (this.options.ContainerPort <= 0)
            {
                throw new InvalidOperationException(
                    $"Kubernetes runtime host container port must be greater than zero. Actual value: {this.options.ContainerPort}.");
            }

            var metadata =
                this.metadataBuilder.Build(request);

            var transportName =
                string.IsNullOrWhiteSpace(request.TransportName)
                    ? request.ProviderName
                    : request.TransportName;

            var tenantId =
                request.ExecutionContextSnapshot?.TenantId;

            var tenantGroupId =
                request.ExecutionContextSnapshot?.TenantGroupId;

            var environmentVariables =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AiMcpHost__Mode"] = "RuntimeInstanceOnly",
                    ["AiRuntimeInstanceRegistration__RuntimeInstanceId"] = request.RuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration__ProviderName"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__TransportName"] = transportName,
                    ["AiRuntimeInstanceRegistration__Role"] = "Runtime",
                    ["AiRuntimeInstanceRegistration__Metadata__host.provider"] = AiRuntimeHostProviderNames.Kubernetes,
                    ["AiRuntimeInstanceRegistration__Metadata__host.creation.mode"] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    ["AiRuntimeInstanceRegistration__Metadata__provider.name"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__Metadata__provider"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__Metadata__transport.name"] = transportName,
                    ["AiRuntimeInstanceRegistration__ProviderMetadata__host.provider"] = AiRuntimeHostProviderNames.Kubernetes,
                    ["AiRuntimeInstanceRegistration__ProviderMetadata__host.creation.mode"] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    ["AiRuntimeInstanceRegistration__ProviderMetadata__provider.name"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__ProviderMetadata__provider"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__ProviderMetadata__transport.name"] = transportName,
                    ["AiLocalRuntimeInstancePool__Metadata__host.provider"] = AiRuntimeHostProviderNames.Kubernetes,
                    ["AiLocalRuntimeInstancePool__Metadata__host.creation.mode"] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    ["AiLocalRuntimeInstancePool__Metadata__provider.name"] = request.ProviderName,
                    ["AiLocalRuntimeInstancePool__Metadata__provider"] = request.ProviderName,
                    ["AiLocalRuntimeInstancePool__Metadata__transport.name"] = transportName
                };

            AddControlPlaneEnvironmentVariables(
                environmentVariables,
                request.ControlPlaneId);

            AddTransportEnvironmentVariables(
                environmentVariables,
                request.TransportEndpoint);

            AddTenantEnvironmentVariables(
                environmentVariables,
                tenantId,
                tenantGroupId);

            foreach (var environmentVariable in this.options.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(environmentVariable.Key))
                {
                    environmentVariables[environmentVariable.Key] = environmentVariable.Value;
                }
            }

            return new AiKubernetesRuntimePodSpec
            {
                Namespace = metadata.Namespace,
                PodName = metadata.PodName,
                RuntimeImage = this.options.RuntimeImage,
                ContainerName = this.options.ContainerName,
                ContainerPort = this.options.ContainerPort,
                ServiceAccountName = this.options.ServiceAccountName,
                Labels = metadata.Labels,
                Annotations = metadata.Annotations,
                EnvironmentVariables = environmentVariables,
                ImagePullPolicy = this.options.ImagePullPolicy
            };
        }

        /// <summary>
        /// Adds control-plane environment variables used by the runtime registration and local runtime pool.
        /// </summary>
        /// <param name="environmentVariables">The environment variables.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        private static void AddControlPlaneEnvironmentVariables(
            IDictionary<string, string> environmentVariables,
            string? controlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                return;
            }

            environmentVariables["AiMcpHost__ControlPlaneId"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__ControlPlaneId"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__Metadata__controlPlaneId"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__Metadata__control-plane.id"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__Metadata__controlplane.id"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__Metadata__runtime.controlPlaneId"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__controlPlaneId"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__control-plane.id"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__controlplane.id"] = controlPlaneId;
            environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__runtime.controlPlaneId"] = controlPlaneId;
            environmentVariables["AiLocalRuntimeInstancePool__Metadata__controlPlaneId"] = controlPlaneId;
            environmentVariables["AiLocalRuntimeInstancePool__Metadata__control-plane.id"] = controlPlaneId;
            environmentVariables["AiLocalRuntimeInstancePool__Metadata__controlplane.id"] = controlPlaneId;
            environmentVariables["AiLocalRuntimeInstancePool__Metadata__runtime.controlPlaneId"] = controlPlaneId;
        }

        /// <summary>
        /// Adds transport endpoint environment variables used by registration metadata.
        /// </summary>
        /// <param name="environmentVariables">The environment variables.</param>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        private static void AddTransportEnvironmentVariables(
            IDictionary<string, string> environmentVariables,
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return;
            }

            environmentVariables["AiRuntimeInstanceRegistration__Metadata__transport.endpoint"] = transportEndpoint;
            environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__transport.endpoint"] = transportEndpoint;
            environmentVariables["AiLocalRuntimeInstancePool__Metadata__transport.endpoint"] = transportEndpoint;
        }

        /// <summary>
        /// Adds tenant environment variables used by registration, provider metadata, and the local runtime pool.
        /// </summary>
        /// <param name="environmentVariables">The environment variables.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        private static void AddTenantEnvironmentVariables(
            IDictionary<string, string> environmentVariables,
            string? tenantId,
            string? tenantGroupId)
        {
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                environmentVariables["AiRuntimeInstanceRegistration__TenantId"] = tenantId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.id"] = tenantId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenantId"] = tenantId;
                environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__tenant.id"] = tenantId;
                environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__tenantId"] = tenantId;
                environmentVariables["AiLocalRuntimeInstancePool__TenantId"] = tenantId;
                environmentVariables["AiLocalRuntimeInstancePool__Metadata__tenant.id"] = tenantId;
                environmentVariables["AiLocalRuntimeInstancePool__Metadata__tenantId"] = tenantId;
            }

            if (!string.IsNullOrWhiteSpace(tenantGroupId))
            {
                environmentVariables["AiRuntimeInstanceRegistration__TenantGroupId"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.group.id"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.groupId"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenantGroupId"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__tenant.group.id"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__tenant.groupId"] = tenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__ProviderMetadata__tenantGroupId"] = tenantGroupId;
                environmentVariables["AiLocalRuntimeInstancePool__TenantGroupId"] = tenantGroupId;
                environmentVariables["AiLocalRuntimeInstancePool__Metadata__tenant.group.id"] = tenantGroupId;
                environmentVariables["AiLocalRuntimeInstancePool__Metadata__tenant.groupId"] = tenantGroupId;
                environmentVariables["AiLocalRuntimeInstancePool__Metadata__tenantGroupId"] = tenantGroupId;
            }
        }
    }
}