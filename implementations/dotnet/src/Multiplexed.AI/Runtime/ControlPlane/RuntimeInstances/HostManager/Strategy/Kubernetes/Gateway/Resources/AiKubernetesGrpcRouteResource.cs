using k8s.Models;
using k8s;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources
{
    /// <summary>
    /// Represents a typed Gateway API GRPCRoute custom resource.
    /// </summary>
    public sealed class AiKubernetesGrpcRouteResource : IKubernetesObject<V1ObjectMeta>
    {
        /// <inheritdoc />
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = AiKubernetesGatewayNames.QualifiedApiVersion;

        /// <inheritdoc />
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = AiKubernetesGatewayNames.GrpcRouteKind;

        /// <inheritdoc />
        [JsonPropertyName("metadata")]
        public V1ObjectMeta Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the GRPCRoute specification.
        /// </summary>
        [JsonPropertyName("spec")]
        public AiKubernetesGrpcRouteSpec Spec { get; set; } = new();

        /// <summary>
        /// Gets or sets the route status reported by the Gateway controller.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiKubernetesGatewayRouteStatus? Status { get; set; }
    }

    /// <summary>
    /// Represents a GRPCRoute specification for one runtime instance.
    /// </summary>
    public sealed class AiKubernetesGrpcRouteSpec
    {
        /// <summary>
        /// Gets or sets the parent Gateway references.
        /// </summary>
        [JsonPropertyName("parentRefs")]
        public IList<AiKubernetesGatewayParentReference> ParentRefs { get; set; } =
            new List<AiKubernetesGatewayParentReference>();

        /// <summary>
        /// Gets or sets the gRPC routing rules.
        /// </summary>
        [JsonPropertyName("rules")]
        public IList<AiKubernetesGrpcRouteRule> Rules { get; set; } =
            new List<AiKubernetesGrpcRouteRule>();
    }

    /// <summary>
    /// Represents one GRPCRoute rule.
    /// </summary>
    public sealed class AiKubernetesGrpcRouteRule
    {
        /// <summary>
        /// Gets or sets the gRPC request matches.
        /// </summary>
        [JsonPropertyName("matches")]
        public IList<AiKubernetesGrpcRouteMatch> Matches { get; set; } =
            new List<AiKubernetesGrpcRouteMatch>();

        /// <summary>
        /// Gets or sets the backend Service references.
        /// </summary>
        [JsonPropertyName("backendRefs")]
        public IList<AiKubernetesGatewayBackendReference> BackendRefs { get; set; } =
            new List<AiKubernetesGatewayBackendReference>();
    }

    /// <summary>
    /// Represents a GRPCRoute metadata header match.
    /// </summary>
    public sealed class AiKubernetesGrpcRouteMatch
    {
        /// <summary>
        /// Gets or sets the gRPC metadata header matches.
        /// </summary>
        [JsonPropertyName("headers")]
        public IList<AiKubernetesGatewayHeaderMatch> Headers { get; set; } =
            new List<AiKubernetesGatewayHeaderMatch>();
    }
}
