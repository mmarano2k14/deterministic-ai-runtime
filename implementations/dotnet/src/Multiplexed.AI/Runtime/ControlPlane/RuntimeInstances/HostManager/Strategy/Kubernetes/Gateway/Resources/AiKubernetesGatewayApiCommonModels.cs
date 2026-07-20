using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources
{

    /// <summary>
    /// Represents a Kubernetes condition reported on Gateway API resources.
    /// </summary>
    public sealed class AiKubernetesGatewayCondition
    {
        /// <summary>
        /// Gets or sets the condition type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the condition status.
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the observed resource generation.
        /// </summary>
        [JsonPropertyName("observedGeneration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ObservedGeneration { get; set; }

        /// <summary>
        /// Gets or sets the last transition time as reported by Kubernetes.
        /// </summary>
        [JsonPropertyName("lastTransitionTime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastTransitionTime { get; set; }

        /// <summary>
        /// Gets or sets the machine-readable condition reason.
        /// </summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        /// <summary>
        /// Gets or sets the human-readable condition message.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Represents a Gateway API parent reference.
    /// </summary>
    public sealed class AiKubernetesGatewayParentReference
    {
        /// <summary>
        /// Gets or sets the parent API group.
        /// </summary>
        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Group { get; set; }

        /// <summary>
        /// Gets or sets the parent resource kind.
        /// </summary>
        [JsonPropertyName("kind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; set; }

        /// <summary>
        /// Gets or sets the parent namespace.
        /// </summary>
        [JsonPropertyName("namespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets the parent resource name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parent section name, normally a Gateway listener name.
        /// </summary>
        [JsonPropertyName("sectionName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SectionName { get; set; }
    }

    /// <summary>
    /// Represents a Gateway API Service backend reference.
    /// </summary>
    public sealed class AiKubernetesGatewayBackendReference
    {
        /// <summary>
        /// Gets or sets the backend API group.
        /// </summary>
        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Group { get; set; }

        /// <summary>
        /// Gets or sets the backend resource kind.
        /// </summary>
        [JsonPropertyName("kind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; set; }

        /// <summary>
        /// Gets or sets the backend resource name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the backend namespace.
        /// </summary>
        [JsonPropertyName("namespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets the backend Service port.
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets the backend weight.
        /// </summary>
        [JsonPropertyName("weight")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Weight { get; set; }
    }

    /// <summary>
    /// Represents an exact Gateway API header match.
    /// </summary>
    public sealed class AiKubernetesGatewayHeaderMatch
    {
        /// <summary>
        /// Gets or sets the header match type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = AiKubernetesGatewayNames.ExactHeaderMatchType;

        /// <summary>
        /// Gets or sets the header name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the header value.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the Gateway API status shared by HTTPRoute and GRPCRoute resources.
    /// </summary>
    public sealed class AiKubernetesGatewayRouteStatus
    {
        /// <summary>
        /// Gets or sets the parent status entries reported by Gateway controllers.
        /// </summary>
        [JsonPropertyName("parents")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayRouteParentStatus>? Parents { get; set; }
    }

    /// <summary>
    /// Represents the attachment status of a Route to one parent Gateway.
    /// </summary>
    public sealed class AiKubernetesGatewayRouteParentStatus
    {
        /// <summary>
        /// Gets or sets the parent reference.
        /// </summary>
        [JsonPropertyName("parentRef")]
        public AiKubernetesGatewayParentReference ParentRef { get; set; } = new();

        /// <summary>
        /// Gets or sets the controller name reporting the status.
        /// </summary>
        [JsonPropertyName("controllerName")]
        public string ControllerName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the route conditions reported by the controller.
        /// </summary>
        [JsonPropertyName("conditions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IList<AiKubernetesGatewayCondition>? Conditions { get; set; }
    }
}
