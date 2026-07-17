using k8s.Models;
using k8s;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources
{
    /// <summary>
    /// Represents a typed Gateway API Gateway custom resource.
    /// </summary>
    public sealed class AiKubernetesGatewayResource : IKubernetesObject<V1ObjectMeta>
    {
        /// <inheritdoc />
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = AiKubernetesGatewayNames.QualifiedApiVersion;

        /// <inheritdoc />
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = AiKubernetesGatewayNames.GatewayKind;

        /// <inheritdoc />
        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the Gateway specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public AiKubernetesGatewaySpec Spec { get; set; } = new();

        /// <summary>
        /// Gets or sets the Gateway status reported by the Gateway controller.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiKubernetesGatewayStatus? Status { get; set; }
    }

    /// <summary>
    /// Represents the shared runtime Gateway specification.
    /// </summary>
    public sealed class AiKubernetesGatewaySpec
    {
        /// <summary>
        /// Gets or sets the GatewayClass name.
        /// </summary>
        [JsonPropertyName("gatewayClassName")]
        public string GatewayClassName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Gateway listeners.
        /// </summary>
        [JsonPropertyName("listeners")]
        public IList<AiKubernetesGatewayListener> Listeners { get; set; } =
            new List<AiKubernetesGatewayListener>();
    }

    /// <summary>
    /// Represents one Gateway listener shared by HTTPRoute and GRPCRoute resources.
    /// </summary>
    public sealed class AiKubernetesGatewayListener
    {
        /// <summary>
        /// Gets or sets the listener name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the listener protocol.
        /// </summary>
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = AiKubernetesGatewayNames.HttpListenerProtocol;

        /// <summary>
        /// Gets or sets the listener port.
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets the route attachment policy.
        /// </summary>
        [JsonPropertyName("allowedRoutes")]
        public AiKubernetesGatewayAllowedRoutes AllowedRoutes { get; set; } = new();
    }

    /// <summary>
    /// Represents the Route kinds and namespaces allowed to attach to a listener.
    /// </summary>
    public sealed class AiKubernetesGatewayAllowedRoutes
    {
        /// <summary>
        /// Gets or sets the allowed route namespaces.
        /// </summary>
        [JsonPropertyName("namespaces")]
        public AiKubernetesGatewayAllowedRouteNamespaces Namespaces { get; set; } = new();

        /// <summary>
        /// Gets or sets the allowed route kinds.
        /// </summary>
        [JsonPropertyName("kinds")]
        public IList<AiKubernetesGatewayRouteGroupKind> Kinds { get; set; } =
            new List<AiKubernetesGatewayRouteGroupKind>();
    }

    /// <summary>
    /// Represents the Gateway listener namespace policy for attached routes.
    /// </summary>
    public sealed class AiKubernetesGatewayAllowedRouteNamespaces
    {
        /// <summary>
        /// Gets or sets the namespace selection policy.
        /// </summary>
        [JsonPropertyName("from")]
        public string From { get; set; } = AiKubernetesGatewayNames.SameNamespaceRoutePolicy;
    }

    /// <summary>
    /// Represents one allowed Route API group and kind.
    /// </summary>
    public sealed class AiKubernetesGatewayRouteGroupKind
    {
        /// <summary>
        /// Gets or sets the Route API group.
        /// </summary>
        [JsonPropertyName("group")]
        public string Group { get; set; } = AiKubernetesGatewayNames.ApiGroup;

        /// <summary>
        /// Gets or sets the Route kind.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the Gateway status required by readiness and endpoint discovery.
    /// </summary>
    public sealed class AiKubernetesGatewayStatus
    {
        /// <summary>
        /// Gets or sets the Gateway addresses reported by the controller.
        /// </summary>
        [JsonPropertyName("addresses")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayStatusAddress>? Addresses { get; set; }

        /// <summary>
        /// Gets or sets the Gateway conditions.
        /// </summary>
        [JsonPropertyName("conditions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayCondition>? Conditions { get; set; }

        /// <summary>
        /// Gets or sets listener-specific status entries.
        /// </summary>
        [JsonPropertyName("listeners")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayListenerStatus>? Listeners { get; set; }
    }

    /// <summary>
    /// Represents one Gateway status address.
    /// </summary>
    public sealed class AiKubernetesGatewayStatusAddress
    {
        /// <summary>
        /// Gets or sets the address type.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets the address value.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the status of one Gateway listener.
    /// </summary>
    public sealed class AiKubernetesGatewayListenerStatus
    {
        /// <summary>
        /// Gets or sets the listener name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of attached routes.
        /// </summary>
        [JsonPropertyName("attachedRoutes")]
        public int AttachedRoutes { get; set; }

        /// <summary>
        /// Gets or sets the listener conditions.
        /// </summary>
        [JsonPropertyName("conditions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayCondition>? Conditions { get; set; }
    }
}
