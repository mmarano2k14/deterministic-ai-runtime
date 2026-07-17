namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Defines Gateway API resource names, Kubernetes metadata keys, and routing constants
    /// used by the Kubernetes runtime gateway.
    /// </summary>
    public static class AiKubernetesGatewayNames
    {
        /// <summary>
        /// Gets the Kubernetes Gateway API group.
        /// </summary>
        public const string ApiGroup = "gateway.networking.k8s.io";

        /// <summary>
        /// Gets the Kubernetes Gateway API version.
        /// </summary>
        public const string ApiVersion = "v1";

        /// <summary>
        /// Gets the fully qualified Kubernetes Gateway API version.
        /// </summary>
        public const string QualifiedApiVersion = ApiGroup + "/" + ApiVersion;

        /// <summary>
        /// Gets the GatewayClass custom-resource plural name.
        /// </summary>
        public const string GatewayClassPlural = "gatewayclasses";

        /// <summary>
        /// Gets the Gateway custom-resource plural name.
        /// </summary>
        public const string GatewayPlural = "gateways";

        /// <summary>
        /// Gets the HTTPRoute custom-resource plural name.
        /// </summary>
        public const string HttpRoutePlural = "httproutes";

        /// <summary>
        /// Gets the GRPCRoute custom-resource plural name.
        /// </summary>
        public const string GrpcRoutePlural = "grpcroutes";

        /// <summary>
        /// Gets the GatewayClass resource kind.
        /// </summary>
        public const string GatewayClassKind = "GatewayClass";

        /// <summary>
        /// Gets the Gateway resource kind.
        /// </summary>
        public const string GatewayKind = "Gateway";

        /// <summary>
        /// Gets the HTTPRoute resource kind.
        /// </summary>
        public const string HttpRouteKind = "HTTPRoute";

        /// <summary>
        /// Gets the GRPCRoute resource kind.
        /// </summary>
        public const string GrpcRouteKind = "GRPCRoute";

        /// <summary>
        /// Gets the Kubernetes Service resource kind.
        /// </summary>
        public const string ServiceKind = "Service";

        /// <summary>
        /// Gets the HTTP Gateway listener protocol.
        /// </summary>
        public const string HttpListenerProtocol = "HTTP";

        /// <summary>
        /// Gets the exact header match type used by runtime routes.
        /// </summary>
        public const string ExactHeaderMatchType = "Exact";

        /// <summary>
        /// Gets the same-namespace route attachment policy.
        /// </summary>
        public const string SameNamespaceRoutePolicy = "Same";

        /// <summary>
        /// Gets the default runtime routing header name.
        /// </summary>
        public const string DefaultRoutingHeaderName = "x-ai-runtime-instance-id";

        /// <summary>
        /// Gets the label used by Gateway API implementations to associate infrastructure
        /// resources with a Gateway.
        /// </summary>
        public const string GatewayNameLabel = "gateway.networking.k8s.io/gateway-name";

        /// <summary>
        /// Gets the Envoy Gateway label containing the namespace of the owning Gateway.
        /// </summary>
        public const string EnvoyOwningGatewayNamespaceLabel =
            "gateway.envoyproxy.io/owning-gateway-namespace";

        /// <summary>
        /// Gets the Envoy Gateway label containing the name of the owning Gateway.
        /// </summary>
        public const string EnvoyOwningGatewayNameLabel =
            "gateway.envoyproxy.io/owning-gateway-name";

        /// <summary>
        /// Gets the label indicating that a resource is managed by the deterministic AI runtime.
        /// </summary>
        public const string ManagedByLabel = "multiplexed.ai/managed-by";

        /// <summary>
        /// Gets the label identifying the Kubernetes resource component.
        /// </summary>
        public const string ComponentLabel = "multiplexed.ai/component";

        /// <summary>
        /// Gets the label containing a Kubernetes-safe control-plane identity.
        /// </summary>
        public const string ControlPlaneIdLabel = "multiplexed.ai/control-plane-id";

        /// <summary>
        /// Gets the label containing a Kubernetes-safe runtime instance identity.
        /// </summary>
        public const string RuntimeInstanceIdLabel = "multiplexed.ai/runtime-instance-id";

        /// <summary>
        /// Gets the label identifying the runtime transport.
        /// </summary>
        public const string TransportLabel = "multiplexed.ai/transport";

        /// <summary>
        /// Gets the annotation preserving the full control-plane identity.
        /// </summary>
        public const string ControlPlaneIdAnnotation = "multiplexed.ai/control-plane-id";

        /// <summary>
        /// Gets the annotation preserving the full runtime instance identity.
        /// </summary>
        public const string RuntimeInstanceIdAnnotation = "multiplexed.ai/runtime-instance-id";

        /// <summary>
        /// Gets the Gateway API Accepted condition type.
        /// </summary>
        public const string AcceptedConditionType = "Accepted";

        /// <summary>
        /// Gets the Gateway API ResolvedRefs condition type.
        /// </summary>
        public const string ResolvedRefsConditionType = "ResolvedRefs";

        /// <summary>
        /// Gets the Gateway API Programmed condition type.
        /// </summary>
        public const string ProgrammedConditionType = "Programmed";

        /// <summary>
        /// Gets the Kubernetes condition status value representing true.
        /// </summary>
        public const string TrueConditionStatus = "True";

        /// <summary>
        /// Gets the Kubernetes condition status value representing false.
        /// </summary>
        public const string FalseConditionStatus = "False";

        /// <summary>
        /// Gets the Gateway API reason used when no external address has been assigned.
        /// </summary>
        public const string AddressNotAssignedReason = "AddressNotAssigned";

        /// <summary>
        /// Gets the managed-by label value.
        /// </summary>
        public const string ManagedByValue = "deterministic-ai-runtime";

        /// <summary>
        /// Gets the shared Gateway component label value.
        /// </summary>
        public const string GatewayComponentValue = "runtime-gateway";

        /// <summary>
        /// Gets the managed GatewayClass component label value.
        /// </summary>
        public const string GatewayClassComponentValue = "runtime-gateway-class";

        /// <summary>
        /// Gets the runtime route component label value.
        /// </summary>
        public const string RouteComponentValue = "runtime-route";
    }
}
