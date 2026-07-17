using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Provides GRPCRoute lifecycle operations for the Kubernetes runtime Gateway manager.
    /// </summary>
    public sealed partial class KubernetesSdkAiKubernetesRuntimeGatewayManager
    {
        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeRouteResult> EnsureGrpcRouteAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort,
            CancellationToken cancellationToken = default)
        {
            ValidateRuntimeRouteArguments(
                controlPlaneId,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort);

            cancellationToken.ThrowIfCancellationRequested();
            this.ValidateOptions();

            /*
             * Ensure the shared parent infrastructure first. This remains idempotent and
             * also guarantees that the configured GatewayClass and listener are valid.
             */
            var gatewayEndpoint =
                await this.EnsureGatewayAsync(
                        controlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var desiredRoute =
                this.resourceFactory.CreateGrpcRoute(
                    controlPlaneId,
                    runtimeInstanceId,
                    runtimeServiceName,
                    backendPort);

            var routeName =
                desiredRoute.Metadata?.Name ??
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-name-missing: The desired GRPCRoute for runtime '{runtimeInstanceId}' does not expose metadata.name.");

            var deadline =
                DateTimeOffset.UtcNow.Add(
                    this.options.GatewayReadinessTimeout);

            this.logger.LogInformation(
                "KUBERNETES GRPC ROUTE ENSURE BEGIN RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} ListenerName={ListenerName} Namespace={Namespace} RoutingHeaderName={RoutingHeaderName}",
                routeName,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ListenerName,
                this.options.Namespace,
                ResolveRoutingHeaderName(this.options));

            var route =
                await this.TryReadGrpcRouteAsync(
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);

            var created = false;

            if (route is null)
            {
                try
                {
                    route =
                        await this.client
                            .CreateNamespacedCustomObjectAsync(
                                desiredRoute,
                                AiKubernetesGatewayNames.ApiGroup,
                                AiKubernetesGatewayNames.ApiVersion,
                                this.options.Namespace,
                                AiKubernetesGatewayNames.GrpcRoutePlural,
                                cancellationToken)
                            .ConfigureAwait(false);

                    created = true;

                    this.logger.LogInformation(
                        "KUBERNETES GRPC ROUTE CREATED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} Namespace={Namespace}",
                        routeName,
                        runtimeInstanceId,
                        runtimeServiceName,
                        backendPort,
                        gatewayEndpoint.GatewayName,
                        this.options.Namespace);
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    this.logger.LogInformation(
                        "KUBERNETES GRPC ROUTE CREATE CONVERGED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} Namespace={Namespace} Reason=already-exists",
                        routeName,
                        runtimeInstanceId,
                        this.options.Namespace);

                    route =
                        await this.ReadGrpcRouteRequiredAsync(
                                routeName,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            else
            {
                this.logger.LogInformation(
                    "KUBERNETES GRPC ROUTE REUSED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} Namespace={Namespace}",
                    routeName,
                    runtimeInstanceId,
                    runtimeServiceName,
                    backendPort,
                    gatewayEndpoint.GatewayName,
                    this.options.Namespace);
            }

            ValidateGrpcRouteIdentity(
                route,
                desiredRoute,
                this.options.Namespace,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ListenerName,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort,
                ResolveRoutingHeaderName(this.options));

            route =
                await this.WaitUntilGrpcRouteReadyAsync(
                        routeName,
                        desiredRoute,
                        gatewayEndpoint.GatewayName,
                        gatewayEndpoint.ListenerName,
                        runtimeInstanceId,
                        runtimeServiceName,
                        backendPort,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES GRPC ROUTE ENSURE COMPLETED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} ListenerName={ListenerName} Namespace={Namespace} Created={Created}",
                route.Metadata?.Name,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ListenerName,
                this.options.Namespace,
                created);

            return new AiKubernetesRuntimeRouteResult
            {
                Namespace = this.options.Namespace,
                GatewayName = gatewayEndpoint.GatewayName,
                ListenerName = gatewayEndpoint.ListenerName,
                RouteName = routeName,
                RouteKind = AiKubernetesRuntimeRouteKind.GrpcRoute,
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeServiceName = runtimeServiceName,
                BackendPort = backendPort,
                RoutingHeaderName = ResolveRoutingHeaderName(this.options),
                RoutingHeaderValue = runtimeInstanceId
            };
        }

        /// <summary>
        /// Reads a GRPCRoute when it exists.
        /// </summary>
        private async Task<AiKubernetesGrpcRouteResource?> TryReadGrpcRouteAsync(
            string routeName,
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client
                    .ReadNamespacedCustomObjectAsync<AiKubernetesGrpcRouteResource>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        this.options.Namespace,
                        AiKubernetesGatewayNames.GrpcRoutePlural,
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a GRPCRoute and fails when it is unexpectedly missing.
        /// </summary>
        private async Task<AiKubernetesGrpcRouteResource> ReadGrpcRouteRequiredAsync(
            string routeName,
            CancellationToken cancellationToken)
        {
            var route =
                await this.TryReadGrpcRouteAsync(
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);

            return route ??
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-missing-after-create-conflict: GRPCRoute '{this.options.Namespace}/{routeName}' was not found after Kubernetes returned AlreadyExists.");
        }

        /// <summary>
        /// Waits until the GRPCRoute is accepted and all backend references are resolved.
        /// </summary>
        private async Task<AiKubernetesGrpcRouteResource> WaitUntilGrpcRouteReadyAsync(
            string routeName,
            AiKubernetesGrpcRouteResource desiredRoute,
            string gatewayName,
            string listenerName,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            string? lastConditionSummary = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var route =
                    await this.ReadGrpcRouteRequiredAsync(
                            routeName,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateGrpcRouteIdentity(
                    route,
                    desiredRoute,
                    this.options.Namespace,
                    gatewayName,
                    listenerName,
                    runtimeInstanceId,
                    runtimeServiceName,
                    backendPort,
                    ResolveRoutingHeaderName(this.options));

                var parentStatus =
                    route.Status?
                        .Parents?
                        .FirstOrDefault(parent =>
                            IsExpectedRouteParent(
                                parent.ParentRef,
                                this.options.Namespace,
                                gatewayName,
                                listenerName));

                var generation =
                    route.Metadata?.Generation;

                var accepted =
                    HasCurrentTrueRouteCondition(
                        parentStatus?.Conditions,
                        AiKubernetesGatewayNames.AcceptedConditionType,
                        generation);

                var resolvedRefs =
                    HasCurrentTrueRouteCondition(
                        parentStatus?.Conditions,
                        AiKubernetesGatewayNames.ResolvedRefsConditionType,
                        generation);

                if (accepted && resolvedRefs)
                {
                    return route;
                }

                lastConditionSummary =
                    CreateGrpcRouteConditionSummary(
                        route,
                        gatewayName,
                        listenerName);

                await DelayUntilNextPollAsync(
                        deadline,
                        this.options.GatewayReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"kubernetes-grpc-route-readiness-timeout: GRPCRoute '{this.options.Namespace}/{routeName}' for runtime '{runtimeInstanceId}' did not become Accepted=True and ResolvedRefs=True within '{this.options.GatewayReadinessTimeout}'. " +
                $"Gateway='{gatewayName}', Listener='{listenerName}', BackendService='{runtimeServiceName}', BackendPort='{backendPort}', Conditions='{lastConditionSummary ?? "none"}'.");
        }

        /// <summary>
        /// Validates that an existing GRPCRoute still represents the requested runtime route.
        /// </summary>
        private static void ValidateGrpcRouteIdentity(
            AiKubernetesGrpcRouteResource route,
            AiKubernetesGrpcRouteResource desiredRoute,
            string namespaceName,
            string gatewayName,
            string listenerName,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort,
            string routingHeaderName)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(desiredRoute);

            var expectedRouteName =
                desiredRoute.Metadata?.Name ??
                throw new InvalidOperationException(
                    "kubernetes-grpc-route-desired-name-missing: Desired GRPCRoute metadata.name is missing.");

            if (!string.Equals(
                    route.Metadata?.Name,
                    expectedRouteName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-name-mismatch: Expected '{expectedRouteName}', actual '{route.Metadata?.Name ?? "(null)"}'.");
            }

            if (!string.Equals(
                    route.Metadata?.NamespaceProperty,
                    namespaceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-namespace-mismatch: GRPCRoute '{expectedRouteName}' is in namespace '{route.Metadata?.NamespaceProperty ?? "(null)"}', expected '{namespaceName}'.");
            }

            ValidateGrpcRouteMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.ManagedByLabel,
                AiKubernetesGatewayNames.ManagedByValue,
                expectedRouteName,
                "label");

            ValidateGrpcRouteMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.ComponentLabel,
                AiKubernetesGatewayNames.RouteComponentValue,
                expectedRouteName,
                "label");

            ValidateGrpcRouteMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.TransportLabel,
                "grpc",
                expectedRouteName,
                "label");

            ValidateGrpcRouteMetadataValue(
                route.Metadata?.Annotations,
                AiKubernetesGatewayNames.RuntimeInstanceIdAnnotation,
                runtimeInstanceId,
                expectedRouteName,
                "annotation");

            var parentReference =
                route.Spec?
                    .ParentRefs?
                    .FirstOrDefault(parent =>
                        IsExpectedRouteParent(
                            parent,
                            namespaceName,
                            gatewayName,
                            listenerName));

            if (parentReference is null)
            {
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-parent-mismatch: GRPCRoute '{namespaceName}/{expectedRouteName}' does not reference Gateway '{gatewayName}' listener '{listenerName}'.");
            }

            var matchingRule =
                route.Spec?
                    .Rules?
                    .FirstOrDefault(rule =>
                        ContainsGrpcRuntimeHeaderMatch(
                            rule.Matches,
                            routingHeaderName,
                            runtimeInstanceId) &&
                        ContainsRuntimeBackendReference(
                            rule.BackendRefs,
                            namespaceName,
                            runtimeServiceName,
                            backendPort));

            if (matchingRule is null)
            {
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-routing-mismatch: GRPCRoute '{namespaceName}/{expectedRouteName}' does not route metadata header '{routingHeaderName}: {runtimeInstanceId}' to Service '{runtimeServiceName}:{backendPort}'.");
            }
        }

        /// <summary>
        /// Determines whether gRPC matches contain the exact runtime routing metadata header.
        /// </summary>
        private static bool ContainsGrpcRuntimeHeaderMatch(
            IEnumerable<AiKubernetesGrpcRouteMatch>? matches,
            string routingHeaderName,
            string runtimeInstanceId)
        {
            return matches?
                .SelectMany(match => match.Headers ?? Array.Empty<AiKubernetesGatewayHeaderMatch>())
                .Any(header =>
                    string.Equals(header.Type, AiKubernetesGatewayNames.ExactHeaderMatchType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header.Name, routingHeaderName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(header.Value, runtimeInstanceId, StringComparison.Ordinal)) == true;
        }

        /// <summary>
        /// Validates one required GRPCRoute metadata value.
        /// </summary>
        private static void ValidateGrpcRouteMetadataValue(
            IDictionary<string, string>? metadata,
            string key,
            string expectedValue,
            string routeName,
            string metadataKind)
        {
            var actualValue =
                metadata?
                    .FirstOrDefault(item =>
                        string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                    .Value;

            if (!string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-grpc-route-{metadataKind}-mismatch: GRPCRoute '{routeName}' {metadataKind} '{key}' is '{actualValue ?? "(null)"}', expected '{expectedValue}'.");
            }
        }

        /// <summary>
        /// Creates a concise GRPCRoute status diagnostic.
        /// </summary>
        private static string CreateGrpcRouteConditionSummary(
            AiKubernetesGrpcRouteResource route,
            string gatewayName,
            string listenerName)
        {
            var parent =
                route.Status?
                    .Parents?
                    .FirstOrDefault(item =>
                        IsExpectedRouteParent(
                            item.ParentRef,
                            route.Metadata?.NamespaceProperty ?? string.Empty,
                            gatewayName,
                            listenerName));

            if (parent is null)
            {
                return "parent-status-missing";
            }

            var conditions =
                parent.Conditions;

            if (conditions is null || conditions.Count == 0)
            {
                return "parent-conditions-missing";
            }

            return string.Join(
                ";",
                conditions.Select(condition =>
                    $"{condition.Type}={condition.Status},Reason={condition.Reason ?? "(none)"},ObservedGeneration={condition.ObservedGeneration?.ToString() ?? "(null)"},Message={condition.Message ?? "(none)"}"));
        }
    }
}
