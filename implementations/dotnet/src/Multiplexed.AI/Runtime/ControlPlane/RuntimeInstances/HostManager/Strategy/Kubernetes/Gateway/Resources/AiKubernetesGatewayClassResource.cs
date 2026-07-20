using k8s.Models;
using k8s;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources
{
    /// <summary>
    /// Represents a typed Gateway API GatewayClass custom resource.
    /// </summary>
    /// <remarks>
    /// The runtime host provider can create this cluster-scoped resource when it is
    /// missing, then waits until the matching Gateway controller reports
    /// <c>Accepted=True</c>.
    /// </remarks>
    public sealed class AiKubernetesGatewayClassResource : IKubernetesObject<V1ObjectMeta>
    {
        /// <inheritdoc />
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = AiKubernetesGatewayNames.QualifiedApiVersion;

        /// <inheritdoc />
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = AiKubernetesGatewayNames.GatewayClassKind;

        /// <inheritdoc />
        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the GatewayClass specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public AiKubernetesGatewayClassSpec Spec { get; set; } = new();

        /// <summary>
        /// Gets or sets the GatewayClass status.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiKubernetesGatewayClassStatus? Status { get; set; }
    }

    /// <summary>
    /// Represents the GatewayClass specification fields required by the runtime host provider.
    /// </summary>
    public sealed class AiKubernetesGatewayClassSpec
    {
        /// <summary>
        /// Gets or sets the controller name responsible for the GatewayClass.
        /// </summary>
        [JsonPropertyName("controllerName")]
        public string ControllerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the GatewayClass status fields required by readiness validation.
    /// </summary>
    public sealed class AiKubernetesGatewayClassStatus
    {
        /// <summary>
        /// Gets or sets the GatewayClass conditions.
        /// </summary>
        [JsonPropertyName("conditions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayCondition>? Conditions { get; set; }
    }
}
