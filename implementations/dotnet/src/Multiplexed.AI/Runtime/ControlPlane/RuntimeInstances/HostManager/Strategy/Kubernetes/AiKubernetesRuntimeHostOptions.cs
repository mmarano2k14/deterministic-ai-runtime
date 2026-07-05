using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides options used by the Kubernetes runtime host creation strategy.
    /// </summary>
    /// <remarks>
    /// These options describe Kubernetes host lifecycle behavior only.
    /// Runtime command dispatch must remain owned by the selected transport provider, such as HTTP or gRPC.
    /// </remarks>
    public sealed class AiKubernetesRuntimeHostOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes runtime host creation is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the Kubernetes runtime host client mode.
        /// </summary>
        public AiKubernetesRuntimeHostClientMode ClientMode { get; set; } = AiKubernetesRuntimeHostClientMode.Fake;

        /// <summary>
        /// Gets or sets the Kubernetes namespace where runtime pods are created.
        /// </summary>
        public string Namespace { get; set; } = "ai-runtime";

        /// <summary>
        /// Gets or sets the runtime container image used for RuntimeInstanceOnly pods.
        /// </summary>
        public string RuntimeImage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime container name.
        /// </summary>
        public string ContainerName { get; set; } = "runtime-instance";

        /// <summary>
        /// Gets or sets the optional Kubernetes service account name used by runtime pods.
        /// </summary>
        public string? ServiceAccountName { get; set; }

        /// <summary>
        /// Gets or sets the prefix used when generating Kubernetes pod names.
        /// </summary>
        public string PodNamePrefix { get; set; } = "ai-runtime";

        /// <summary>
        /// Gets or sets the transport name exposed by the runtime pod.
        /// </summary>
        /// <remarks>
        /// This value should normally be <c>grpc</c> or <c>http</c>.
        /// Kubernetes owns host lifecycle, not command transport.
        /// </remarks>
        public string TransportName { get; set; } = "grpc";

        /// <summary>
        /// Gets or sets the container port exposed by the runtime transport.
        /// </summary>
        public int ContainerPort { get; set; } = 8080;

        /// <summary>
        /// Gets or sets a value indicating whether a dedicated Kubernetes service should be created per runtime instance.
        /// </summary>
        public bool UseServicePerRuntime { get; set; } = true;

        /// <summary>
        /// Gets or sets the timeout used while waiting for the Kubernetes host to start.
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the timeout used while waiting for the runtime instance to publish registry and capacity state.
        /// </summary>
        public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes resources should be deleted when host creation fails.
        /// </summary>
        public bool DeleteResourcesOnFailure { get; set; } = true;

        /// <summary>
        /// Gets or sets additional Kubernetes labels applied to runtime pods and services.
        /// </summary>
        public Dictionary<string, string> Labels { get; set; } = new();

        /// <summary>
        /// Gets or sets additional Kubernetes annotations applied to runtime pods and services.
        /// </summary>
        public Dictionary<string, string> Annotations { get; set; } = new();
    }
}