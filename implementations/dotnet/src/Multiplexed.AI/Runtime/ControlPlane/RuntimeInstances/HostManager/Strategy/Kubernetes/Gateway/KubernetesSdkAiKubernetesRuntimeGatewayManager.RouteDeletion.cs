using k8s.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Provides runtime-route deletion operations for the Kubernetes runtime Gateway manager.
    /// </summary>
    public sealed partial class KubernetesSdkAiKubernetesRuntimeGatewayManager
    {
        /// <inheritdoc />
        public async Task DeleteRuntimeRouteAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();
            this.ValidateOptions();

            var httpRouteName =
                this.resourceFactory.CreateRouteName(
                    runtimeInstanceId,
                    AiKubernetesRuntimeRouteKind.HttpRoute);

            var grpcRouteName =
                this.resourceFactory.CreateRouteName(
                    runtimeInstanceId,
                    AiKubernetesRuntimeRouteKind.GrpcRoute);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME ROUTE DELETE BEGIN RuntimeInstanceId={RuntimeInstanceId} HttpRouteName={HttpRouteName} GrpcRouteName={GrpcRouteName} Namespace={Namespace} GatewayName={GatewayName}",
                runtimeInstanceId,
                httpRouteName,
                grpcRouteName,
                this.options.Namespace,
                this.resourceFactory.CreateGatewayName());

            /*
             * Delete both deterministic route names. This remains safe if a runtime
             * transport changed between starts and avoids requiring stale metadata
             * simply to identify which route kind was previously created.
             */
            await this.DeleteHttpRouteIfExistsAsync(
                    runtimeInstanceId,
                    httpRouteName,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.DeleteGrpcRouteIfExistsAsync(
                    runtimeInstanceId,
                    grpcRouteName,
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME ROUTE DELETE COMPLETED RuntimeInstanceId={RuntimeInstanceId} HttpRouteName={HttpRouteName} GrpcRouteName={GrpcRouteName} Namespace={Namespace} GatewayPreserved=True",
                runtimeInstanceId,
                httpRouteName,
                grpcRouteName,
                this.options.Namespace);
        }

        /// <summary>
        /// Deletes an HTTPRoute when it exists.
        /// </summary>
        private async Task DeleteHttpRouteIfExistsAsync(
            string runtimeInstanceId,
            string routeName,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.client
                    .DeleteNamespacedCustomObjectAsync<V1Status>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        this.options.Namespace,
                        AiKubernetesGatewayNames.HttpRoutePlural,
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES HTTP ROUTE DELETED RuntimeInstanceId={RuntimeInstanceId} RouteName={RouteName} Namespace={Namespace}",
                    runtimeInstanceId,
                    routeName,
                    this.options.Namespace);
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                this.logger.LogDebug(
                    "KUBERNETES HTTP ROUTE DELETE CONVERGED RuntimeInstanceId={RuntimeInstanceId} RouteName={RouteName} Namespace={Namespace} Reason=not-found",
                    runtimeInstanceId,
                    routeName,
                    this.options.Namespace);
            }
        }

        /// <summary>
        /// Deletes a GRPCRoute when it exists.
        /// </summary>
        private async Task DeleteGrpcRouteIfExistsAsync(
            string runtimeInstanceId,
            string routeName,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.client
                    .DeleteNamespacedCustomObjectAsync<V1Status>(
                        AiKubernetesGatewayNames.ApiGroup,
                        AiKubernetesGatewayNames.ApiVersion,
                        this.options.Namespace,
                        AiKubernetesGatewayNames.GrpcRoutePlural,
                        routeName,
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES GRPC ROUTE DELETED RuntimeInstanceId={RuntimeInstanceId} RouteName={RouteName} Namespace={Namespace}",
                    runtimeInstanceId,
                    routeName,
                    this.options.Namespace);
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                this.logger.LogDebug(
                    "KUBERNETES GRPC ROUTE DELETE CONVERGED RuntimeInstanceId={RuntimeInstanceId} RouteName={RouteName} Namespace={Namespace} Reason=not-found",
                    runtimeInstanceId,
                    routeName,
                    this.options.Namespace);
            }
        }
    }
}
