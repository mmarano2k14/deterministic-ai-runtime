using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Gateway
{
    /// <summary>
    /// Provides unit tests for <see cref="KubernetesSdkAiKubernetesRuntimeGatewayManager"/>.
    /// </summary>
    public sealed class KubernetesSdkAiKubernetesRuntimeGatewayManagerTests
    {
        /// <summary>
        /// Verifies that local port-forward mode does not treat Service creation as transport readiness
        /// before the controller-managed Gateway Service exposes a ready endpoint.
        /// </summary>
        [Fact]
        public async Task EnsureGatewayAsync_Should_Wait_For_Ready_Service_Endpoint_In_PortForward_Mode()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var options = CreateOptions(usePortForwardTransportEndpoint: true);
            var resourceFactory = new AiKubernetesGatewayResourceFactory(Options.Create(options));

            SeedAcceptedGatewayInfrastructure(
                sdkClient,
                resourceFactory,
                options);

            sdkClient.Endpoints =
                new V1Endpoints
                {
                    Metadata =
                        new V1ObjectMeta
                        {
                            Name = "envoy-ai-runtime-gateway",
                            NamespaceProperty = "envoy-gateway-system"
                        },
                    Subsets =
                        new List<V1EndpointSubset>
                        {
                            new()
                            {
                                Addresses = new List<V1EndpointAddress>()
                            }
                        }
                };

            sdkClient.ReadEndpointsCallback =
                () =>
                {
                    if (sdkClient.ReadEndpointsCallCount < 2)
                    {
                        return;
                    }

                    sdkClient.Endpoints!.Subsets![0].Addresses =
                        new List<V1EndpointAddress>
                        {
                            new()
                            {
                                Ip = "10.244.0.20"
                            }
                        };
                };

            var manager =
                new KubernetesSdkAiKubernetesRuntimeGatewayManager(
                    Options.Create(options),
                    new FakeKubernetesClientFactory(sdkClient),
                    resourceFactory,
                    NullLogger<KubernetesSdkAiKubernetesRuntimeGatewayManager>.Instance);

            var endpoint =
                await manager.EnsureGatewayAsync("control-plane-test");

            Assert.Equal("envoy-ai-runtime-gateway", endpoint.ServiceName);
            Assert.Equal("envoy-gateway-system", endpoint.ServiceNamespace);
            Assert.Equal(8080, endpoint.ServicePort);
            Assert.True(sdkClient.ReadEndpointsCallCount >= 2);
        }

        /// <summary>
        /// Verifies that non-port-forward Gateway transports retain the historical
        /// Service-presence readiness behavior and do not require Endpoints reads.
        /// </summary>
        [Fact]
        public async Task EnsureGatewayAsync_Should_Not_Require_Ready_Service_Endpoint_When_PortForward_Is_Disabled()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var options = CreateOptions(usePortForwardTransportEndpoint: false);
            var resourceFactory = new AiKubernetesGatewayResourceFactory(Options.Create(options));

            SeedAcceptedGatewayInfrastructure(
                sdkClient,
                resourceFactory,
                options);

            var manager =
                new KubernetesSdkAiKubernetesRuntimeGatewayManager(
                    Options.Create(options),
                    new FakeKubernetesClientFactory(sdkClient),
                    resourceFactory,
                    NullLogger<KubernetesSdkAiKubernetesRuntimeGatewayManager>.Instance);

            var endpoint =
                await manager.EnsureGatewayAsync("control-plane-test");

            Assert.Equal("envoy-ai-runtime-gateway", endpoint.ServiceName);
            Assert.Equal(0, sdkClient.ReadEndpointsCallCount);
        }

        /// <summary>
        /// Creates Gateway options for one unit-test scenario.
        /// </summary>
        private static AiKubernetesRuntimeHostOptions CreateOptions(
            bool usePortForwardTransportEndpoint)
        {
            return new AiKubernetesRuntimeHostOptions
            {
                Namespace = "ai-runtime",
                UseGatewayTransportEndpoint = true,
                GatewayName = "ai-runtime-gateway",
                GatewayClassName = "eg",
                GatewayControllerName = "gateway.envoyproxy.io/gatewayclass-controller",
                CreateGatewayClassWhenMissing = true,
                GatewayListenerName = "runtime",
                GatewayPort = 8080,
                CreateGatewayWhenMissing = true,
                RequireGatewayProgrammed = true,
                GatewayReadinessTimeout = TimeSpan.FromSeconds(1),
                GatewayReadinessPollInterval = TimeSpan.FromMilliseconds(1),
                UsePortForwardTransportEndpoint = usePortForwardTransportEndpoint
            };
        }

        /// <summary>
        /// Seeds an accepted GatewayClass, a listener-ready Gateway, and the controller-managed Service.
        /// </summary>
        private static void SeedAcceptedGatewayInfrastructure(
            FakeAiKubernetesSdkClient sdkClient,
            AiKubernetesGatewayResourceFactory resourceFactory,
            AiKubernetesRuntimeHostOptions options)
        {
            var gatewayClass =
                resourceFactory.CreateGatewayClass();

            gatewayClass.Status =
                new AiKubernetesGatewayClassStatus
                {
                    Conditions =
                        new List<AiKubernetesGatewayCondition>
                        {
                            CreateCondition(
                                AiKubernetesGatewayNames.AcceptedConditionType,
                                AiKubernetesGatewayNames.TrueConditionStatus)
                        }
                };

            sdkClient.SetClusterCustomObject(
                AiKubernetesGatewayNames.ApiGroup,
                AiKubernetesGatewayNames.ApiVersion,
                AiKubernetesGatewayNames.GatewayClassPlural,
                options.GatewayClassName,
                gatewayClass);

            var gateway =
                resourceFactory.CreateGateway("control-plane-test");

            gateway.Status =
                new AiKubernetesGatewayStatus
                {
                    Conditions =
                        new List<AiKubernetesGatewayCondition>
                        {
                            CreateCondition(
                                AiKubernetesGatewayNames.AcceptedConditionType,
                                AiKubernetesGatewayNames.TrueConditionStatus),
                            CreateCondition(
                                AiKubernetesGatewayNames.ProgrammedConditionType,
                                AiKubernetesGatewayNames.FalseConditionStatus,
                                AiKubernetesGatewayNames.AddressNotAssignedReason)
                        },
                    Listeners =
                        new List<AiKubernetesGatewayListenerStatus>
                        {
                            new()
                            {
                                Name = options.GatewayListenerName,
                                Conditions =
                                    new List<AiKubernetesGatewayCondition>
                                    {
                                        CreateCondition(
                                            AiKubernetesGatewayNames.AcceptedConditionType,
                                            AiKubernetesGatewayNames.TrueConditionStatus),
                                        CreateCondition(
                                            AiKubernetesGatewayNames.ProgrammedConditionType,
                                            AiKubernetesGatewayNames.TrueConditionStatus),
                                        CreateCondition(
                                            AiKubernetesGatewayNames.ResolvedRefsConditionType,
                                            AiKubernetesGatewayNames.TrueConditionStatus)
                                    }
                            }
                        }
                };

            sdkClient.SetNamespacedCustomObject(
                AiKubernetesGatewayNames.ApiGroup,
                AiKubernetesGatewayNames.ApiVersion,
                options.Namespace,
                AiKubernetesGatewayNames.GatewayPlural,
                options.GatewayName,
                gateway);

            sdkClient.Services.Add(
                new V1Service
                {
                    Metadata =
                        new V1ObjectMeta
                        {
                            Name = "envoy-ai-runtime-gateway",
                            NamespaceProperty = "envoy-gateway-system",
                            Labels =
                                new Dictionary<string, string>
                                {
                                    [AiKubernetesGatewayNames.EnvoyOwningGatewayNamespaceLabel] =
                                        options.Namespace,
                                    [AiKubernetesGatewayNames.EnvoyOwningGatewayNameLabel] =
                                        options.GatewayName
                                }
                        },
                    Spec =
                        new V1ServiceSpec
                        {
                            Ports =
                                new List<V1ServicePort>
                                {
                                    new()
                                    {
                                        Port = options.GatewayPort
                                    }
                                }
                        }
                });
        }

        /// <summary>
        /// Creates one Gateway API condition.
        /// </summary>
        private static AiKubernetesGatewayCondition CreateCondition(
            string type,
            string status,
            string? reason = null)
        {
            return new AiKubernetesGatewayCondition
            {
                Type = type,
                Status = status,
                Reason = reason
            };
        }
    }
}
