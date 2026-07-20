using k8s.Models;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Creates typed Kubernetes Gateway API resources for the shared runtime gateway
    /// and one header-routed backend route per runtime instance.
    /// </summary>
    /// <remarks>
    /// This factory only builds resource models. It does not create Kubernetes resources,
    /// wait for controller readiness, or alter the existing runtime transport lifecycle.
    /// </remarks>
    public sealed class AiKubernetesGatewayResourceFactory
    {
        private const int KubernetesDnsLabelMaxLength = 63;
        private const int KubernetesLabelValueMaxLength = 63;
        private readonly AiKubernetesRuntimeHostOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiKubernetesGatewayResourceFactory"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        public AiKubernetesGatewayResourceFactory(
            IOptions<AiKubernetesRuntimeHostOptions> options)
        {
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Creates the configured cluster-scoped GatewayClass resource.
        /// </summary>
        /// <returns>The typed GatewayClass resource.</returns>
        public AiKubernetesGatewayClassResource CreateGatewayClass()
        {
            this.ValidateGatewayOptions();

            return new AiKubernetesGatewayClassResource
            {
                Metadata =
                    new V1ObjectMeta
                    {
                        Name = this.options.GatewayClassName,
                        Labels =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                [AiKubernetesGatewayNames.ManagedByLabel] =
                                    AiKubernetesGatewayNames.ManagedByValue,
                                [AiKubernetesGatewayNames.ComponentLabel] =
                                    AiKubernetesGatewayNames.GatewayClassComponentValue
                            }
                    },
                Spec =
                    new AiKubernetesGatewayClassSpec
                    {
                        ControllerName = this.options.GatewayControllerName
                    }
            };
        }

        /// <summary>
        /// Creates the configured shared Gateway resource.
        /// </summary>
        /// <param name="controlPlaneId">The owning control-plane id.</param>
        /// <returns>The typed Gateway resource.</returns>
        public AiKubernetesGatewayResource CreateGateway(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            this.ValidateGatewayOptions();

            var gatewayName =
                this.CreateGatewayName();

            var listenerName =
                this.CreateListenerName();

            return new AiKubernetesGatewayResource
            {
                Metadata =
                    new V1ObjectMeta
                    {
                        Name = gatewayName,
                        NamespaceProperty = this.options.Namespace,
                        Labels = CreateGatewayLabels(controlPlaneId),
                        Annotations = CreateGatewayAnnotations(controlPlaneId)
                    },
                Spec =
                    new AiKubernetesGatewaySpec
                    {
                        GatewayClassName = this.options.GatewayClassName,
                        Listeners =
                            new List<AiKubernetesGatewayListener>
                            {
                                new()
                                {
                                    Name = listenerName,
                                    Protocol = AiKubernetesGatewayNames.HttpListenerProtocol,
                                    Port = this.options.GatewayPort,
                                    AllowedRoutes =
                                        new AiKubernetesGatewayAllowedRoutes
                                        {
                                            Namespaces =
                                                new AiKubernetesGatewayAllowedRouteNamespaces
                                                {
                                                    From = AiKubernetesGatewayNames.SameNamespaceRoutePolicy
                                                },
                                            Kinds =
                                                new List<AiKubernetesGatewayRouteGroupKind>
                                                {
                                                    new()
                                                    {
                                                        Group = AiKubernetesGatewayNames.ApiGroup,
                                                        Kind = AiKubernetesGatewayNames.HttpRouteKind
                                                    },
                                                    new()
                                                    {
                                                        Group = AiKubernetesGatewayNames.ApiGroup,
                                                        Kind = AiKubernetesGatewayNames.GrpcRouteKind
                                                    }
                                                }
                                        }
                                }
                            }
                    }
            };
        }

        /// <summary>
        /// Creates an HTTPRoute that selects one runtime instance by routing header.
        /// </summary>
        /// <param name="controlPlaneId">The owning control-plane id.</param>
        /// <param name="runtimeInstanceId">The target runtime instance id.</param>
        /// <param name="runtimeServiceName">The Kubernetes Service backing the runtime.</param>
        /// <param name="backendPort">The runtime Service port.</param>
        /// <returns>The typed HTTPRoute resource.</returns>
        public AiKubernetesHttpRouteResource CreateHttpRoute(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort)
        {
            ValidateRuntimeRouteArguments(
                controlPlaneId,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort);

            this.ValidateGatewayOptions();

            return new AiKubernetesHttpRouteResource
            {
                Metadata =
                    this.CreateRouteMetadata(
                        controlPlaneId,
                        runtimeInstanceId,
                        transportName: "http",
                        routeKind: AiKubernetesRuntimeRouteKind.HttpRoute),
                Spec =
                    new AiKubernetesHttpRouteSpec
                    {
                        ParentRefs =
                            new List<AiKubernetesGatewayParentReference>
                            {
                                this.CreateParentReference()
                            },
                        Rules =
                            new List<AiKubernetesHttpRouteRule>
                            {
                                new()
                                {
                                    Matches =
                                        new List<AiKubernetesHttpRouteMatch>
                                        {
                                            new()
                                            {
                                                Headers =
                                                    new List<AiKubernetesGatewayHeaderMatch>
                                                    {
                                                        this.CreateRuntimeHeaderMatch(runtimeInstanceId)
                                                    }
                                            }
                                        },
                                    BackendRefs =
                                        new List<AiKubernetesGatewayBackendReference>
                                        {
                                            CreateRuntimeBackendReference(
                                                runtimeServiceName,
                                                backendPort)
                                        }
                                }
                            }
                    }
            };
        }

        /// <summary>
        /// Creates a GRPCRoute that selects one runtime instance by gRPC metadata header.
        /// </summary>
        /// <param name="controlPlaneId">The owning control-plane id.</param>
        /// <param name="runtimeInstanceId">The target runtime instance id.</param>
        /// <param name="runtimeServiceName">The Kubernetes Service backing the runtime.</param>
        /// <param name="backendPort">The runtime Service port.</param>
        /// <returns>The typed GRPCRoute resource.</returns>
        public AiKubernetesGrpcRouteResource CreateGrpcRoute(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort)
        {
            ValidateRuntimeRouteArguments(
                controlPlaneId,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort);

            this.ValidateGatewayOptions();

            return new AiKubernetesGrpcRouteResource
            {
                Metadata =
                    this.CreateRouteMetadata(
                        controlPlaneId,
                        runtimeInstanceId,
                        transportName: "grpc",
                        routeKind: AiKubernetesRuntimeRouteKind.GrpcRoute),
                Spec =
                    new AiKubernetesGrpcRouteSpec
                    {
                        ParentRefs =
                            new List<AiKubernetesGatewayParentReference>
                            {
                                this.CreateParentReference()
                            },
                        Rules =
                            new List<AiKubernetesGrpcRouteRule>
                            {
                                new()
                                {
                                    Matches =
                                        new List<AiKubernetesGrpcRouteMatch>
                                        {
                                            new()
                                            {
                                                Headers =
                                                    new List<AiKubernetesGatewayHeaderMatch>
                                                    {
                                                        this.CreateRuntimeHeaderMatch(runtimeInstanceId)
                                                    }
                                            }
                                        },
                                    BackendRefs =
                                        new List<AiKubernetesGatewayBackendReference>
                                        {
                                            CreateRuntimeBackendReference(
                                                runtimeServiceName,
                                                backendPort)
                                        }
                                }
                            }
                    }
            };
        }

        /// <summary>
        /// Creates the normalized shared Gateway resource name.
        /// </summary>
        /// <returns>The Gateway resource name.</returns>
        public string CreateGatewayName()
        {
            return CreateKubernetesDnsLabel(
                this.options.GatewayName);
        }

        /// <summary>
        /// Creates the normalized shared Gateway listener name.
        /// </summary>
        /// <returns>The listener name.</returns>
        public string CreateListenerName()
        {
            return CreateKubernetesDnsLabel(
                this.options.GatewayListenerName);
        }

        /// <summary>
        /// Creates the deterministic route resource name for one runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="routeKind">The route kind.</param>
        /// <returns>The route resource name.</returns>
        public string CreateRouteName(
            string runtimeInstanceId,
            AiKubernetesRuntimeRouteKind routeKind)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var routeToken =
                routeKind == AiKubernetesRuntimeRouteKind.GrpcRoute
                    ? "grpc"
                    : "http";

            return CreateKubernetesDnsLabel(
                $"{this.CreateGatewayName()}-{routeToken}-{runtimeInstanceId}");
        }

        /// <summary>
        /// Creates the parent Gateway reference shared by runtime routes.
        /// </summary>
        /// <returns>The parent reference.</returns>
        private AiKubernetesGatewayParentReference CreateParentReference()
        {
            return new AiKubernetesGatewayParentReference
            {
                Group = AiKubernetesGatewayNames.ApiGroup,
                Kind = AiKubernetesGatewayNames.GatewayKind,
                Name = this.CreateGatewayName(),
                SectionName = this.CreateListenerName()
            };
        }

        /// <summary>
        /// Creates the exact runtime routing header match.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The header match.</returns>
        private AiKubernetesGatewayHeaderMatch CreateRuntimeHeaderMatch(
            string runtimeInstanceId)
        {
            return new AiKubernetesGatewayHeaderMatch
            {
                Type = AiKubernetesGatewayNames.ExactHeaderMatchType,
                Name = this.ResolveRoutingHeaderName(),
                Value = runtimeInstanceId
            };
        }

        /// <summary>
        /// Creates a backend reference to a runtime Service in the same namespace.
        /// </summary>
        /// <param name="runtimeServiceName">The runtime Service name.</param>
        /// <param name="backendPort">The runtime Service port.</param>
        /// <returns>The backend reference.</returns>
        private static AiKubernetesGatewayBackendReference CreateRuntimeBackendReference(
            string runtimeServiceName,
            int backendPort)
        {
            return new AiKubernetesGatewayBackendReference
            {
                Name = runtimeServiceName,
                Port = backendPort,
                Weight = 1
            };
        }

        /// <summary>
        /// Creates Kubernetes metadata for one runtime Route.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="transportName">The route transport name.</param>
        /// <param name="routeKind">The route kind.</param>
        /// <returns>The route metadata.</returns>
        private V1ObjectMeta CreateRouteMetadata(
            string controlPlaneId,
            string runtimeInstanceId,
            string transportName,
            AiKubernetesRuntimeRouteKind routeKind)
        {
            return new V1ObjectMeta
            {
                Name = this.CreateRouteName(runtimeInstanceId, routeKind),
                NamespaceProperty = this.options.Namespace,
                Labels =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiKubernetesGatewayNames.ManagedByLabel] = AiKubernetesGatewayNames.ManagedByValue,
                        [AiKubernetesGatewayNames.ComponentLabel] = AiKubernetesGatewayNames.RouteComponentValue,
                        [AiKubernetesGatewayNames.ControlPlaneIdLabel] = CreateKubernetesLabelValue(controlPlaneId),
                        [AiKubernetesGatewayNames.RuntimeInstanceIdLabel] = CreateKubernetesLabelValue(runtimeInstanceId),
                        [AiKubernetesGatewayNames.TransportLabel] = transportName
                    },
                Annotations =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiKubernetesGatewayNames.ControlPlaneIdAnnotation] = controlPlaneId,
                        [AiKubernetesGatewayNames.RuntimeInstanceIdAnnotation] = runtimeInstanceId
                    }
            };
        }

        /// <summary>
        /// Creates labels for the shared Gateway.
        /// </summary>
        /// <param name="controlPlaneId">The owning control-plane id.</param>
        /// <returns>The Gateway labels.</returns>
        private static IDictionary<string, string> CreateGatewayLabels(
            string controlPlaneId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiKubernetesGatewayNames.ManagedByLabel] = AiKubernetesGatewayNames.ManagedByValue,
                [AiKubernetesGatewayNames.ComponentLabel] = AiKubernetesGatewayNames.GatewayComponentValue,
                [AiKubernetesGatewayNames.ControlPlaneIdLabel] = CreateKubernetesLabelValue(controlPlaneId)
            };
        }

        /// <summary>
        /// Creates annotations for the shared Gateway.
        /// </summary>
        /// <param name="controlPlaneId">The owning control-plane id.</param>
        /// <returns>The Gateway annotations.</returns>
        private static IDictionary<string, string> CreateGatewayAnnotations(
            string controlPlaneId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiKubernetesGatewayNames.ControlPlaneIdAnnotation] = controlPlaneId
            };
        }

        /// <summary>
        /// Resolves the configured runtime routing header name.
        /// </summary>
        /// <returns>The routing header name.</returns>
        private string ResolveRoutingHeaderName()
        {
            if (!string.IsNullOrWhiteSpace(this.options.GatewayRouteHeaderName))
            {
                return this.options.GatewayRouteHeaderName.Trim().ToLowerInvariant();
            }

            return AiKubernetesGatewayNames.DefaultRoutingHeaderName;
        }

        /// <summary>
        /// Validates the Gateway options required to build resources.
        /// </summary>
        private void ValidateGatewayOptions()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayClassName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayControllerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.GatewayListenerName);

            if (this.options.GatewayPort <= 0 || this.options.GatewayPort > 65535)
            {
                throw new InvalidOperationException(
                    $"Kubernetes Gateway port must be between 1 and 65535. ConfiguredPort='{this.options.GatewayPort}'.");
            }
        }

        /// <summary>
        /// Validates runtime Route factory arguments.
        /// </summary>
        private static void ValidateRuntimeRouteArguments(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeServiceName);

            if (backendPort <= 0 || backendPort > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(backendPort),
                    backendPort,
                    "The runtime Service backend port must be between 1 and 65535.");
            }
        }

        /// <summary>
        /// Creates a Kubernetes DNS label compatible resource name.
        /// </summary>
        /// <param name="rawName">The raw resource name.</param>
        /// <returns>The normalized Kubernetes DNS label.</returns>
        private static string CreateKubernetesDnsLabel(
            string rawName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawName);

            var normalized =
                new string(
                    rawName
                        .ToLowerInvariant()
                        .Select(character =>
                            char.IsLetterOrDigit(character) || character == '-'
                                ? character
                                : '-')
                        .ToArray());

            normalized =
                string.Join(
                    "-",
                    normalized.Split(
                        '-',
                        StringSplitOptions.RemoveEmptyEntries));

            normalized =
                normalized.Trim('-');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "runtime";
            }

            if (normalized.Length <= KubernetesDnsLabelMaxLength)
            {
                return normalized;
            }

            var hash =
                CreateStableHash(normalized, length: 10);

            var prefixLength =
                KubernetesDnsLabelMaxLength - hash.Length - 1;

            var prefix =
                normalized[..prefixLength].TrimEnd('-');

            if (string.IsNullOrWhiteSpace(prefix))
            {
                return hash;
            }

            return $"{prefix}-{hash}";
        }

        /// <summary>
        /// Creates a Kubernetes label-safe value while preserving deterministic identity.
        /// </summary>
        /// <param name="rawValue">The raw value.</param>
        /// <returns>The Kubernetes-safe label value.</returns>
        private static string CreateKubernetesLabelValue(
            string rawValue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawValue);

            var normalized =
                new string(
                    rawValue
                        .ToLowerInvariant()
                        .Select(character =>
                            char.IsLetterOrDigit(character) ||
                            character == '-' ||
                            character == '_' ||
                            character == '.'
                                ? character
                                : '-')
                        .ToArray())
                    .Trim('-', '_', '.');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return CreateStableHash(rawValue, length: 12);
            }

            if (normalized.Length <= KubernetesLabelValueMaxLength)
            {
                return normalized;
            }

            var hash =
                CreateStableHash(rawValue, length: 12);

            var prefixLength =
                KubernetesLabelValueMaxLength - hash.Length - 1;

            var prefix =
                normalized[..prefixLength]
                    .TrimEnd('-', '_', '.');

            return string.IsNullOrWhiteSpace(prefix)
                ? hash
                : $"{prefix}-{hash}";
        }

        /// <summary>
        /// Creates a stable lowercase hexadecimal hash.
        /// </summary>
        /// <param name="value">The value to hash.</param>
        /// <param name="length">The requested hexadecimal length.</param>
        /// <returns>The stable hash.</returns>
        private static string CreateStableHash(
            string value,
            int length)
        {
            return Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant()[..length];
        }
    }
}
