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
    /// Provides HTTPRoute lifecycle operations for the Kubernetes runtime Gateway manager.
    /// </summary>
    public sealed partial class KubernetesSdkAiKubernetesRuntimeGatewayManager
    {
        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeRouteResult> EnsureHttpRouteAsync(
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
                this.resourceFactory.CreateHttpRoute(
                    controlPlaneId,
                    runtimeInstanceId,
                    runtimeServiceName,
                    backendPort);

            var routeName =
                desiredRoute.Metadata?.Name ??
                throw new InvalidOperationException(
                    $"kubernetes-http-route-name-missing: The desired HTTPRoute for runtime '{runtimeInstanceId}' does not expose metadata.name.");

            var deadline =
                DateTimeOffset.UtcNow.Add(
                    this.options.GatewayReadinessTimeout);

            this.logger.LogInformation(
                "KUBERNETES HTTP ROUTE ENSURE BEGIN RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} ListenerName={ListenerName} Namespace={Namespace} RoutingHeaderName={RoutingHeaderName}",
                routeName,
                runtimeInstanceId,
                runtimeServiceName,
                backendPort,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ListenerName,
                this.options.Namespace,
                ResolveRoutingHeaderName(this.options));

            var route =
                await this.TryReadHttpRouteAsync(
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
                                AiKubernetesGatewayNames.HttpRoutePlural,
                                cancellationToken)
                            .ConfigureAwait(false);

                    created = true;

                    this.logger.LogInformation(
                        "KUBERNETES HTTP ROUTE CREATED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} Namespace={Namespace}",
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
                        "KUBERNETES HTTP ROUTE CREATE CONVERGED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} Namespace={Namespace} Reason=already-exists",
                        routeName,
                        runtimeInstanceId,
                        this.options.Namespace);

                    route =
                        await this.ReadHttpRouteRequiredAsync(
                                routeName,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            else
            {
                this.logger.LogInformation(
                    "KUBERNETES HTTP ROUTE REUSED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} Namespace={Namespace}",
                    routeName,
                    runtimeInstanceId,
                    runtimeServiceName,
                    backendPort,
                    gatewayEndpoint.GatewayName,
                    this.options.Namespace);
            }

            ValidateHttpRouteIdentity(
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
                await this.WaitUntilHttpRouteReadyAsync(
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
                "KUBERNETES HTTP ROUTE ENSURE COMPLETED RouteName={RouteName} RuntimeInstanceId={RuntimeInstanceId} RuntimeServiceName={RuntimeServiceName} BackendPort={BackendPort} GatewayName={GatewayName} ListenerName={ListenerName} Namespace={Namespace} Created={Created}",
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
                RouteKind = AiKubernetesRuntimeRouteKind.HttpRoute,
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeServiceName = runtimeServiceName,
                BackendPort = backendPort,
                RoutingHeaderName = ResolveRoutingHeaderName(this.options),
                RoutingHeaderValue = runtimeInstanceId
            };
        }

        /// <summary>
        /// Reads an HTTPRoute when it exists.
        /// </summary>
        private async Task<AiKubernetesHttpRouteResource?> TryReadHttpRouteAsync(
            string routeName,
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client
                    .ReadNamespacedCustomObjectAsync<AiKubernetesHttpRouteResource>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        this.options.Namespace,
                        AiKubernetesGatewayNames.HttpRoutePlural,
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
        /// Reads an HTTPRoute and fails when it is unexpectedly missing.
        /// </summary>
        private async Task<AiKubernetesHttpRouteResource> ReadHttpRouteRequiredAsync(
            string routeName,
            CancellationToken cancellationToken)
        {
            var route =
                await this.TryReadHttpRouteAsync(
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);

            return route ??
                throw new InvalidOperationException(
                    $"kubernetes-http-route-missing-after-create-conflict: HTTPRoute '{this.options.Namespace}/{routeName}' was not found after Kubernetes returned AlreadyExists.");
        }

        /// <summary>
        /// Waits until the HTTPRoute is accepted and all backend references are resolved.
        /// </summary>
        private async Task<AiKubernetesHttpRouteResource> WaitUntilHttpRouteReadyAsync(
            string routeName,
            AiKubernetesHttpRouteResource desiredRoute,
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
                    await this.ReadHttpRouteRequiredAsync(
                            routeName,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateHttpRouteIdentity(
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
                    CreateHttpRouteConditionSummary(
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
                $"kubernetes-http-route-readiness-timeout: HTTPRoute '{this.options.Namespace}/{routeName}' for runtime '{runtimeInstanceId}' did not become Accepted=True and ResolvedRefs=True within '{this.options.GatewayReadinessTimeout}'. " +
                $"Gateway='{gatewayName}', Listener='{listenerName}', BackendService='{runtimeServiceName}', BackendPort='{backendPort}', Conditions='{lastConditionSummary ?? "none"}'.");
        }

        /// <summary>
        /// Validates that an existing HTTPRoute still represents the requested runtime route.
        /// </summary>
        private static void ValidateHttpRouteIdentity(
            AiKubernetesHttpRouteResource route,
            AiKubernetesHttpRouteResource desiredRoute,
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
                    "kubernetes-http-route-desired-name-missing: Desired HTTPRoute metadata.name is missing.");

            if (!string.Equals(
                    route.Metadata?.Name,
                    expectedRouteName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-http-route-name-mismatch: Expected '{expectedRouteName}', actual '{route.Metadata?.Name ?? "(null)"}'.");
            }

            if (!string.Equals(
                    route.Metadata?.NamespaceProperty,
                    namespaceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"kubernetes-http-route-namespace-mismatch: HTTPRoute '{expectedRouteName}' is in namespace '{route.Metadata?.NamespaceProperty ?? "(null)"}', expected '{namespaceName}'.");
            }

            ValidateMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.ManagedByLabel,
                AiKubernetesGatewayNames.ManagedByValue,
                expectedRouteName,
                "label");

            ValidateMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.ComponentLabel,
                AiKubernetesGatewayNames.RouteComponentValue,
                expectedRouteName,
                "label");

            ValidateMetadataValue(
                route.Metadata?.Labels,
                AiKubernetesGatewayNames.TransportLabel,
                "http",
                expectedRouteName,
                "label");

            ValidateMetadataValue(
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
                    $"kubernetes-http-route-parent-mismatch: HTTPRoute '{namespaceName}/{expectedRouteName}' does not reference Gateway '{gatewayName}' listener '{listenerName}'.");
            }

            var matchingRule =
                route.Spec?
                    .Rules?
                    .FirstOrDefault(rule =>
                        ContainsRuntimeHeaderMatch(
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
                    $"kubernetes-http-route-routing-mismatch: HTTPRoute '{namespaceName}/{expectedRouteName}' does not route header '{routingHeaderName}: {runtimeInstanceId}' to Service '{runtimeServiceName}:{backendPort}'.");
            }
        }

        /// <summary>
        /// Determines whether a Route parent reference targets the expected Gateway listener.
        /// </summary>
        private static bool IsExpectedRouteParent(
            AiKubernetesGatewayParentReference? parentReference,
            string namespaceName,
            string gatewayName,
            string listenerName)
        {
            if (parentReference is null ||
                !string.Equals(parentReference.Name, gatewayName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parentReference.Group) &&
                !string.Equals(parentReference.Group, AiKubernetesGatewayNames.ApiGroup, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parentReference.Kind) &&
                !string.Equals(parentReference.Kind, AiKubernetesGatewayNames.GatewayKind, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parentReference.Namespace) &&
                !string.Equals(parentReference.Namespace, namespaceName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(parentReference.SectionName) ||
                   string.Equals(parentReference.SectionName, listenerName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether HTTP matches contain the exact runtime routing header.
        /// </summary>
        private static bool ContainsRuntimeHeaderMatch(
            IEnumerable<AiKubernetesHttpRouteMatch>? matches,
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
        /// Determines whether backend references contain the expected runtime Service.
        /// </summary>
        private static bool ContainsRuntimeBackendReference(
            IEnumerable<AiKubernetesGatewayBackendReference>? backendReferences,
            string namespaceName,
            string runtimeServiceName,
            int backendPort)
        {
            return backendReferences?.Any(backend =>
                (string.IsNullOrWhiteSpace(backend.Group) || string.Equals(backend.Group, string.Empty, StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(backend.Kind) || string.Equals(backend.Kind, AiKubernetesGatewayNames.ServiceKind, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(backend.Namespace) || string.Equals(backend.Namespace, namespaceName, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(backend.Name, runtimeServiceName, StringComparison.OrdinalIgnoreCase) &&
                backend.Port == backendPort) == true;
        }

        /// <summary>
        /// Validates one required Kubernetes metadata value.
        /// </summary>
        private static void ValidateMetadataValue(
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
                    $"kubernetes-http-route-{metadataKind}-mismatch: HTTPRoute '{routeName}' {metadataKind} '{key}' is '{actualValue ?? "(null)"}', expected '{expectedValue}'.");
            }
        }

        /// <summary>
        /// Determines whether a current Route condition is true.
        /// </summary>
        private static bool HasCurrentTrueRouteCondition(
            IEnumerable<AiKubernetesGatewayCondition>? conditions,
            string conditionType,
            long? resourceGeneration)
        {
            return conditions?.Any(condition =>
                string.Equals(condition.Type, conditionType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(condition.Status, AiKubernetesGatewayNames.TrueConditionStatus, StringComparison.OrdinalIgnoreCase) &&
                (!resourceGeneration.HasValue ||
                 !condition.ObservedGeneration.HasValue ||
                 condition.ObservedGeneration.Value >= resourceGeneration.Value)) == true;
        }

        /// <summary>
        /// Creates a concise HTTPRoute status diagnostic.
        /// </summary>
        private static string CreateHttpRouteConditionSummary(
            AiKubernetesHttpRouteResource route,
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

        /// <summary>
        /// Resolves the configured routing header name.
        /// </summary>
        private static string ResolveRoutingHeaderName(
            AiKubernetesRuntimeHostOptions options)
        {
            return string.IsNullOrWhiteSpace(options.GatewayRouteHeaderName)
                ? AiKubernetesGatewayNames.DefaultRoutingHeaderName
                : options.GatewayRouteHeaderName.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Validates runtime route arguments.
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
    }
}
