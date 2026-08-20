using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Composes the existing Process Pool child lifecycle as the data plane inside one
    /// Kubernetes Runtime Pool Pod.
    /// </summary>
    public static class AiKubernetesRuntimePoolInPodServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the in-Pod Process Pool Manager from strongly typed bootstrap options.
        /// </summary>
        public static IServiceCollection AddAiKubernetesRuntimePoolInPod(
            this IServiceCollection services,
            AiKubernetesRuntimePoolInPodOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options);

            var hostId =
                AiKubernetesRuntimePoolInPodOptionsValidator
                    .ReadHostId(options);

            var orderedRuntimeInstances =
                options.RuntimeInstances
                    .OrderBy(item => item.Ordinal)
                    .ToArray();

            var basePort =
                orderedRuntimeInstances
                    .Min(item => item.TransportPort);

            var maxPort =
                orderedRuntimeInstances
                    .Max(item => item.TransportPort);

            var processPoolOptions =
                new AiRuntimeProcessPoolOptions
                {
                    Enabled = true,
                    PoolId = options.PoolId,
                    HostIdPrefix = "kubernetes-pod",
                    RuntimeInstanceIdPrefix =
                        options.RuntimeInstanceIdPrefix,
                    InitialProcessCount =
                        options.InitialProcessCount,
                    MinimumProcessCount =
                        options.MinimumProcessCount,
                    MaximumProcessCount =
                        options.MaximumProcessCount,
                    StartupParallelism =
                        options.StartupParallelism,
                    ShutdownTimeoutSeconds =
                        options.ShutdownTimeoutSeconds
                };

            var executionContextSnapshot =
                new ExecutionContextSnapshot
                {
                    ContextKey = options.ContextKey,
                    Project = options.Project,
                    UserId = options.UserId,
                    TenantId = options.TenantId,
                    TenantGroupId = options.TenantGroupId,
                    CurrentNamespace =
                        options.CurrentNamespace,
                    Namespaces = new List<NamespaceEntry>(),
                    InFlightCount = 0,
                    TtlSeconds = options.SnapshotTtlSeconds,
                    CreatedAtUtc = DateTime.UtcNow
                };

            var childEnvironment =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            /*
             * Project inherited feature and tenant configuration first. Kubernetes-specific
             * durable connections, physical identity, and host metadata are authoritative and
             * are deliberately applied afterwards so a ProcessHost-oriented inherited value
             * such as localhost Redis/Mongo cannot override the in-Pod data-plane contract.
             */
            foreach (var pair in options.ChildEnvironmentVariables)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
                childEnvironment[pair.Key] = pair.Value ?? string.Empty;
            }

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddDurableRuntimeEnvironment(
                    childEnvironment,
                    options.RedisConnectionString,
                    options.MongoConnectionString,
                    options.MongoDatabaseName,
                    options.OpenAiApiKey);

            AddKubernetesIdentityEnvironment(
                childEnvironment,
                options);

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddHostMetadata(
                    childEnvironment,
                    hostProvider: "kubernetes",
                    hostCreationMode: AiRuntimeHostCreationModeNames.KubernetesPool,
                    hostType: AiRuntimeHostTypeNames.KubernetesPool,
                    deployment: AiRuntimeHostDeploymentNames.KubernetesPool,
                    transportEndpointScope: "pod-internal");

            var runtimeOptions =
                new AiRuntimeProcessPoolRuntimeInstanceOptions
                {
                    DotnetExecutablePath =
                        options.DotnetExecutablePath,
                    RuntimeHostAssemblyPath =
                        options.RuntimeHostAssemblyPath,
                    WorkingDirectory =
                        options.WorkingDirectory,
                    BasePort = basePort,
                    MaxPort = maxPort,
                    EndpointHost =
                        options.EndpointHost,
                    ControlPlaneId =
                        options.ControlPlaneId,
                    EnableControlPlaneDiscovery = true,
                    RequireControlPlaneDiscovery = true,
                    ExecutionContextSnapshot =
                        executionContextSnapshot,
                    ProviderName = options.ProviderName,
                    TransportName = options.TransportName,
                    WorkerCountPerInstance =
                        options.WorkerCountPerInstance,
                    MaxConcurrentRunsPerInstance =
                        options.MaxConcurrentRunsPerInstance,
                    LocalQueueCapacity =
                        options.LocalQueueCapacity,
                    IsolationMode = options.IsolationMode,
                    PreferDedicatedCapacity =
                        options.PreferDedicatedCapacity,
                    AllowSharedFallback =
                        options.AllowSharedFallback,
                    StartupTimeout =
                        options.ChildStartupTimeout,
                    ReadinessPollInterval =
                        options.ChildReadinessPollInterval,
                    HeartbeatInterval =
                        options.HeartbeatInterval,
                    RedirectOutput = false,
                    CreateNoWindow = true,
                    KillEntireProcessTreeOnStop = true,
                    StopTimeoutSeconds =
                        options.ShutdownTimeoutSeconds,
                    EnvironmentVariables =
                        childEnvironment
                };

            services.AddAiRuntimeProcessPool(
                processPoolOptions,
                runtimeOptions);

            services.RemoveAll<IAiRuntimeProcessPoolManager>();

            services.AddSingleton<IAiRuntimeProcessPoolManager>(
                serviceProvider =>
                    new AiKubernetesRuntimePoolInPodManager(
                        options,
                        hostId,
                        serviceProvider.GetRequiredService<
                            IAiRuntimeProcessPoolChildFactory>()));

            return services;
        }

        /// <summary>
        /// Projects the exact Pod identity supplied by the Kubernetes Downward API into every
        /// RuntimeInstanceOnly child registration.
        /// </summary>
        private static void AddKubernetesIdentityEnvironment(
            IDictionary<string, string> destination,
            AiKubernetesRuntimePoolInPodOptions options)
        {
            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddWhenPresent(
                    destination,
                    string.Concat(
                        "AiRuntimeInstanceRegistration__ProviderMetadata__",
                        AiKubernetesRuntimeHostMetadataKeys.Namespace),
                    options.KubernetesNamespace);

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddWhenPresent(
                    destination,
                    string.Concat(
                        "AiRuntimeInstanceRegistration__ProviderMetadata__",
                        AiKubernetesRuntimeHostMetadataKeys.PodName),
                    options.KubernetesPodName);

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddWhenPresent(
                    destination,
                    string.Concat(
                        "AiRuntimeInstanceRegistration__ProviderMetadata__",
                        AiKubernetesRuntimeHostMetadataKeys.NodeName),
                    options.KubernetesNodeName);
        }
    }
}
