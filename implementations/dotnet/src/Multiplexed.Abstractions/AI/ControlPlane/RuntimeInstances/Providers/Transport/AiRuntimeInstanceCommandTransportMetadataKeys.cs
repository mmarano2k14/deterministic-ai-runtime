using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Defines metadata keys used by runtime instance command transports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These keys are stored on runtime instance capacity descriptors and command
    /// metadata dictionaries.
    /// </para>
    ///
    /// <para>
    /// The provider metadata decides which provider class handles the runtime instance.
    /// Transport metadata tells the selected provider or transport how to reach the
    /// target runtime instance.
    /// </para>
    ///
    /// <para>
    /// Example:
    /// </para>
    ///
    /// <code>
    /// provider.name = remote-command
    /// transport.name = redis
    /// transport.command.queue.key = ai:runtime:mcp-runtime-1:commands
    /// </code>
    ///
    /// <para>
    /// IMPORTANT:
    /// These keys must not replace local runtime queues. They only describe how a
    /// remote command provider can communicate with the runtime instance that owns
    /// its own local queue.
    /// </para>
    /// </remarks>
    public static class AiRuntimeInstanceCommandTransportMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key that identifies the command transport name.
        /// </summary>
        public const string TransportName = "transport.name";

        /// <summary>
        /// Gets the metadata key that identifies the command transport kind.
        /// </summary>
        public const string TransportKind = "transport.kind";

        /// <summary>
        /// Gets the metadata key that identifies the command transport endpoint.
        /// </summary>
        public const string TransportEndpoint = "transport.endpoint";

        /// <summary>
        /// Gets the metadata key that identifies the internal transport endpoint.
        /// </summary>
        public const string InternalTransportEndpoint = "transport.endpoint.internal";

        /// <summary>
        /// Gets the legacy metadata key that identifies the runtime command endpoint.
        /// </summary>
        public const string RuntimeCommandEndpoint = "runtime.command.endpoint";

        /// <summary>
        /// Gets the legacy metadata key that identifies the gRPC endpoint.
        /// </summary>
        public const string GrpcEndpoint = "grpc.endpoint";

        /// <summary>
        /// Gets the metadata key that identifies how the transport endpoint was resolved.
        /// </summary>
        public const string TransportEndpointSource = "transport.endpoint.source";

        /// <summary>
        /// Gets the metadata key that identifies the visibility scope of the transport endpoint.
        /// </summary>
        public const string TransportEndpointScope = "transport.endpoint.scope";

        /// <summary>
        /// Gets the metadata key that identifies the command queue key.
        /// </summary>
        public const string CommandQueueKey = "transport.command.queue.key";

        /// <summary>
        /// Gets the metadata key that identifies the reply queue key.
        /// </summary>
        public const string ReplyQueueKey = "transport.reply.queue.key";

        /// <summary>
        /// Gets the metadata key that identifies the command timeout in milliseconds.
        /// </summary>
        public const string CommandTimeoutMs = "transport.command.timeout.ms";

        /// <summary>
        /// Gets the metadata key that identifies the transport namespace.
        /// </summary>
        public const string Namespace = "transport.namespace";

        /// <summary>
        /// Gets the metadata key that identifies the transport zone.
        /// </summary>
        public const string Zone = "transport.zone";

        /// <summary>
        /// Gets the metadata key that identifies the target runtime instance id used by command transports.
        /// </summary>
        public const string RuntimeInstanceId = AiRuntimeInstanceMetadataKeys.RuntimeInstanceId;

        /// <summary>
        /// Gets the metadata key that identifies the explicit target runtime instance id for a command.
        /// </summary>
        public const string TargetRuntimeInstanceId = "target.runtime.instance.id";

        /// <summary>
        /// Gets the metadata key that identifies the gateway routing header name.
        /// </summary>
        public const string GatewayRoutingHeader = "gateway.routing.header";

        /// <summary>
        /// Gets the metadata key that identifies the gateway routing header value.
        /// </summary>
        public const string GatewayRoutingValue = "gateway.routing.value";

        /// <summary>
        /// Gets the metadata key that indicates the command is routed through a remote command provider.
        /// </summary>
        public const string RemoteCommand = "runtime.command.remote";

        /// <summary>
        /// Gets the metadata value that identifies Redis as the command transport.
        /// </summary>
        public const string RedisTransportName = "redis";

        /// <summary>
        /// Gets the metadata value that identifies HTTP as the command transport.
        /// </summary>
        public const string HttpTransportName = "http";

        /// <summary>
        /// Gets the metadata value that identifies gRPC as the command transport.
        /// </summary>
        public const string GrpcTransportName = "grpc";

        /// <summary>
        /// Gets the metadata value that identifies Kubernetes as the command transport.
        /// </summary>
        public const string KubernetesTransportName = "kubernetes";

        /// <summary>
        /// Gets the camel-case transport endpoint metadata key used by compatibility payloads.
        /// </summary>
        public const string CamelCaseTransportEndpoint = "transportEndpoint";

        /// <summary>
        /// Gets the camel-case transport name metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseTransportName = "transportName";
    }
}