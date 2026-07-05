using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
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

            var environmentVariables =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AiMcpHost__Mode"] = "RuntimeInstanceOnly",
                    ["AiRuntimeInstanceRegistration__RuntimeInstanceId"] = request.RuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration__ProviderName"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__TransportName"] = request.TransportName ?? request.ProviderName,
                    ["AiRuntimeInstanceRegistration__Metadata__host.provider"] = "kubernetes",
                    ["AiRuntimeInstanceRegistration__Metadata__host.creation.mode"] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    ["AiRuntimeInstanceRegistration__Metadata__provider.name"] = request.ProviderName,
                    ["AiRuntimeInstanceRegistration__Metadata__transport.name"] = request.TransportName ?? request.ProviderName
                };

            if (!string.IsNullOrWhiteSpace(request.ControlPlaneId))
            {
                environmentVariables["AiRuntimeInstanceRegistration__ControlPlaneId"] = request.ControlPlaneId;
            }

            if (!string.IsNullOrWhiteSpace(request.TransportEndpoint))
            {
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__transport.endpoint"] = request.TransportEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(request.ExecutionContextSnapshot.TenantId))
            {
                environmentVariables["AiRuntimeInstanceRegistration__TenantId"] = request.ExecutionContextSnapshot.TenantId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.id"] = request.ExecutionContextSnapshot.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.ExecutionContextSnapshot.TenantGroupId))
            {
                environmentVariables["AiRuntimeInstanceRegistration__TenantGroupId"] = request.ExecutionContextSnapshot.TenantGroupId;
                environmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.groupId"] = request.ExecutionContextSnapshot.TenantGroupId;
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
    }
}