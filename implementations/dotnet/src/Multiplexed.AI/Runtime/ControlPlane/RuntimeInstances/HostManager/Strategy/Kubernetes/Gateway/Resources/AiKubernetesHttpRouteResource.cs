using k8s.Models;
using k8s;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources
{
    /// <summary>
    /// Represents a typed Gateway API HTTPRoute custom resource.
    /// </summary>
    public sealed class AiKubernetesHttpRouteResource : IKubernetesObject<V1ObjectMeta>
    {
        /// <inheritdoc />
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = AiKubernetesGatewayNames.QualifiedApiVersion;

        /// <inheritdoc />
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = AiKubernetesGatewayNames.HttpRouteKind;

        /// <inheritdoc />
        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the HTTPRoute specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public AiKubernetesHttpRouteSpec Spec { get; set; } = new();

        /// <summary>
        /// Gets or sets the route status reported by the Gateway controller.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiKubernetesGatewayRouteStatus? Status { get; set; }
    }

    /// <summary>
    /// Represents an HTTPRoute specification for one runtime instance.
    /// </summary>
    public sealed class AiKubernetesHttpRouteSpec
    {
        /// <summary>
        /// Gets or sets the parent Gateway references.
        /// </summary>
        [JsonPropertyName("parentRefs")]
        public IList<AiKubernetesGatewayParentReference> ParentRefs { get; set; } =
            new List<AiKubernetesGatewayParentReference>();

        /// <summary>
        /// Gets or sets the HTTP routing rules.
        /// </summary>
        [JsonPropertyName("rules")]
        public IList<AiKubernetesHttpRouteRule> Rules { get; set; } =
            new List<AiKubernetesHttpRouteRule>();
    }

    /// <summary>
    /// Represents one HTTPRoute rule.
    /// </summary>
    public sealed class AiKubernetesHttpRouteRule
    {
        /// <summary>
        /// Gets or sets the request matches.
        /// </summary>
        [JsonPropertyName("matches")]
        public IList<AiKubernetesHttpRouteMatch> Matches { get; set; } =
            new List<AiKubernetesHttpRouteMatch>();

        /// <summary>
        /// Gets or sets the backend Service references.
        /// </summary>
        [JsonPropertyName("backendRefs")]
        public IList<AiKubernetesGatewayBackendReference> BackendRefs { get; set; } =
            new List<AiKubernetesGatewayBackendReference>();
    }

    /// <summary>
    /// Represents an HTTPRoute header match.
    /// </summary>
    public sealed class AiKubernetesHttpRouteMatch
    {
        /// <summary>
        /// Gets or sets the HTTP header matches.
        /// </summary>
        [JsonPropertyName("headers")]
        public IList<AiKubernetesGatewayHeaderMatch> Headers { get; set; } =
            new List<AiKubernetesGatewayHeaderMatch>();
    }
}
