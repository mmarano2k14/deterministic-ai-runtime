using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

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

            AddWhenPresent(
                childEnvironment,
                "ConnectionStrings__Redis",
                options.RedisConnectionString);
            AddWhenPresent(
                childEnvironment,
                "ConnectionStrings__Mongo",
                options.MongoConnectionString);
            AddWhenPresent(
                childEnvironment,
                "Mongo__DatabaseName",
                options.MongoDatabaseName);
            AddWhenPresent(
                childEnvironment,
                "OpenAI__ApiKey",
                options.OpenAiApiKey);

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
                    RedirectOutput = true,
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
        /// Adds a non-empty child configuration value.
        /// </summary>
        private static void AddWhenPresent(
            IDictionary<string, string> destination,
            string key,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                destination[key] = value;
            }
        }
    }
}
