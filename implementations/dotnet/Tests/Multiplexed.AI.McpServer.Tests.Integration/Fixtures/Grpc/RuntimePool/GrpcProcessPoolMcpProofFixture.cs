using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using AspNetCoreServer = Microsoft.AspNetCore.Hosting.Server.IServer;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Grpc.RuntimePool
{
    /// <summary>
    /// Provides one real three-child gRPC Process Pool behind a stable HTTP/2 Kestrel endpoint.
    /// </summary>
    /// <remarks>
    /// All settings are strongly typed and local to the test fixture. The fixture does not
    /// read environment variables or dedicated JSON test settings.
    /// </remarks>
    public sealed class GrpcProcessPoolMcpProofFixture :
        IAsyncLifetime
    {
        private const int BasePort = 6400;
        private const int PortRangeSize = 10;
        private IConnectionMultiplexer? redisConnection;
        private WebApplication? application;
        private GrpcChannel? channel;
        private AiRuntimeInstanceCommandGrpc
            .AiRuntimeInstanceCommandGrpcClient? client;

        /// <summary>
        /// Gets the timeout for the real process-boundary proof.
        /// </summary>
        public TimeSpan TestTimeout { get; } =
            TimeSpan.FromMinutes(3);

        /// <summary>
        /// Gets the stable gRPC client.
        /// </summary>
        public AiRuntimeInstanceCommandGrpc
            .AiRuntimeInstanceCommandGrpcClient Client =>
            this.client
            ?? throw new InvalidOperationException(
                "The gRPC Process Pool fixture has not been initialized.");

        /// <summary>
        /// Gets the exact ready runtime instance identifiers.
        /// </summary>
        public IReadOnlyList<string> RuntimeInstanceIds { get; private set; } =
            Array.Empty<string>();

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            var suffix =
                Guid.NewGuid()
                    .ToString("N");

            var controlPlaneId =
                string.Concat(
                    "grpc-process-pool-5f-cp-",
                    suffix);

            var poolId =
                string.Concat(
                    "grpc-process-pool-5f-",
                    suffix);

            this.redisConnection =
                await CreateRedisConnectionAsync()
                    .ConfigureAwait(false);

            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(
                options =>
                    options.Listen(
                        IPAddress.Loopback,
                        port: 0,
                        listenOptions =>
                            listenOptions.Protocols =
                                HttpProtocols.Http2));

            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AiEngine:ControlPlane:ControlPlaneId"] =
                        controlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] =
                        string.Concat(
                            "multiplexed-ai:",
                            controlPlaneId),
                    ["AiRuntimeInstanceRegistration:Enabled"] =
                        "false"
                });

            builder.Services.AddSingleton(
                this.redisConnection);

            builder.Services.AddSingleton<IAiControlPlaneIdResolver>(
                new GrpcRuntimePoolFixedControlPlaneIdResolver(
                    controlPlaneId));

            builder.Services.AddAiControlPlane(
                configuration: builder.Configuration);

            var runtimeHostAssemblyPath =
                ResolveRuntimeHostAssemblyPath();

            builder.Services.AddAiRuntimeProcessPool(
                new AiRuntimeProcessPoolOptions
                {
                    Enabled = true,
                    PoolId = poolId,
                    HostIdPrefix =
                        "grpc-process-pool-5f-host",
                    RuntimeInstanceIdPrefix =
                        "grpc-process-pool-5f-runtime",
                    InitialProcessCount = 3,
                    MinimumProcessCount = 3,
                    MaximumProcessCount = 3,
                    StartupParallelism = 1,
                    ShutdownTimeoutSeconds = 30
                },
                new AiRuntimeProcessPoolRuntimeInstanceOptions
                {
                    RuntimeHostAssemblyPath =
                        runtimeHostAssemblyPath,
                    WorkingDirectory =
                        Path.GetDirectoryName(
                            runtimeHostAssemblyPath),
                    BasePort = BasePort,
                    MaxPort =
                        checked(
                            BasePort + PortRangeSize),
                    EndpointHost = "127.0.0.1",
                    ControlPlaneId = controlPlaneId,
                    EnableControlPlaneDiscovery = false,
                    RequireControlPlaneDiscovery = false,
                    ExecutionContextSnapshot =
                        CreateExecutionContextSnapshot(poolId),
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    RuntimeVersion =
                        "grpc-process-pool-5f",
                    WorkerCountPerInstance = 2,
                    MaxConcurrentRunsPerInstance = 2,
                    LocalQueueCapacity = 16,
                    StartupTimeout =
                        TimeSpan.FromSeconds(90),
                    ReadinessPollInterval =
                        TimeSpan.FromMilliseconds(100),
                    HeartbeatInterval =
                        TimeSpan.FromSeconds(1),
                    StopTimeoutSeconds = 15
                });

            this.application =
                builder.Build();

            this.application
                .MapAiRuntimePoolGrpcCommandService();

            using var timeout =
                new CancellationTokenSource(
                    this.TestTimeout);

            await this.application
                .StartAsync(timeout.Token)
                .ConfigureAwait(false);

            var manager =
                this.application.Services
                    .GetRequiredService<
                        IAiRuntimeProcessPoolManager>();

            var routeRegistry =
                this.application.Services
                    .GetRequiredService<
                        IAiRuntimePoolRouteRegistry>();

            var snapshot =
                await WaitForHealthyPoolAsync(
                        manager,
                        routeRegistry,
                        timeout.Token)
                    .ConfigureAwait(false);

            this.RuntimeInstanceIds =
                snapshot.Children
                    .OrderBy(child => child.Ordinal)
                    .Select(child => child.RuntimeInstanceId)
                    .ToArray();

            this.channel =
                CreateStableRouterChannel(
                    this.application.Services);

            this.client =
                new AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient(
                        this.channel);
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            this.client = null;

            this.channel?.Dispose();
            this.channel = null;

            if (this.application is not null)
            {
                using var cleanup =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(30));

                await this.application
                    .StopAsync(cleanup.Token)
                    .ConfigureAwait(false);

                await this.application
                    .DisposeAsync()
                    .ConfigureAwait(false);

                this.application = null;
            }

            if (this.redisConnection is not null)
            {
                await this.redisConnection
                    .CloseAsync()
                    .ConfigureAwait(false);

                this.redisConnection.Dispose();
                this.redisConnection = null;
            }
        }

        /// <summary>
        /// Creates the standard local Redis/Memurai connection.
        /// </summary>
        private static async Task<IConnectionMultiplexer>
            CreateRedisConnectionAsync()
        {
            var configuration =
                new ConfigurationOptions
                {
                    AbortOnConnectFail = false,
                    ConnectRetry = 2,
                    ConnectTimeout = 5000,
                    SyncTimeout = 5000
                };

            configuration.EndPoints.Add(
                "127.0.0.1",
                6379);

            return await ConnectionMultiplexer
                .ConnectAsync(configuration)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates the durable tenant context shared by all children.
        /// </summary>
        private static ExecutionContextSnapshot
            CreateExecutionContextSnapshot(
                string poolId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = poolId,
                Project = "grpc-process-pool-5f",
                UserId = "system",
                TenantId =
                    "grpc-process-pool-5f-tenant",
                TenantGroupId =
                    "grpc-process-pool-5f-group",
                CurrentNamespace = "tests",
                Namespaces =
                    new List<NamespaceEntry>()
            };
        }

        /// <summary>
        /// Waits for three independently routable child processes.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForHealthyPoolAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolRouteRegistry routeRegistry,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot =
                    await manager
                        .GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);

                var routes =
                    await routeRegistry
                        .ListByHostIdAsync(
                            snapshot.HostId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    !snapshot.IsBelowMinimumCapacity &&
                    routes.Count == 3 &&
                    snapshot.Children.All(
                        child =>
                            routes.Any(
                                route =>
                                    StringComparer.Ordinal.Equals(
                                        route.RuntimeInstanceId,
                                        child.RuntimeInstanceId) &&
                                    StringComparer.Ordinal.Equals(
                                        route.PoolId,
                                        snapshot.PoolId) &&
                                    StringComparer.Ordinal.Equals(
                                        route.HostId,
                                        snapshot.HostId) &&
                                    StringComparer.OrdinalIgnoreCase.Equals(
                                        route.TransportName,
                                        "grpc") &&
                                    route.Status ==
                                        AiRuntimePoolRouteStatus.Ready)))
                {
                    return snapshot;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates a channel targeting the dynamic stable Kestrel HTTP/2 endpoint.
        /// </summary>
        private static GrpcChannel CreateStableRouterChannel(
            IServiceProvider services)
        {
            var server =
                services.GetRequiredService<AspNetCoreServer>();

            var addresses =
                server.Features
                    .Get<IServerAddressesFeature>()
                    ?.Addresses
                ?? throw new InvalidOperationException(
                    "The Kestrel server did not expose a listening address.");

            var stableAddress =
                addresses.Single(
                    address =>
                        address.StartsWith(
                            "http://127.0.0.1:",
                            StringComparison.OrdinalIgnoreCase));

            return GrpcChannel.ForAddress(
                stableAddress);
        }

        /// <summary>
        /// Resolves the real RuntimeInstanceOnly host assembly.
        /// </summary>
        private static string ResolveRuntimeHostAssemblyPath()
        {
            var repositoryRoot =
                ResolveRepositoryRoot();

            var testOutputDirectory =
                new DirectoryInfo(
                    AppContext.BaseDirectory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));

            var targetFramework =
                testOutputDirectory.Name;

            var buildConfiguration =
                testOutputDirectory.Parent?.Name
                ?? throw new InvalidOperationException(
                    "The current test build configuration could not be resolved.");

            var hostAssemblyPath =
                Path.Combine(
                    repositoryRoot,
                    "implementations",
                    "dotnet",
                    "src",
                    "Multiplexed.AI.McpServer.Host",
                    "bin",
                    buildConfiguration,
                    targetFramework,
                    "Multiplexed.AI.McpServer.Host.dll");

            if (File.Exists(hostAssemblyPath))
            {
                return hostAssemblyPath;
            }

            throw new FileNotFoundException(
                "The RuntimeInstanceOnly host assembly was not found. Build " +
                "Multiplexed.AI.McpServer.Host with the current configuration first.",
                hostAssemblyPath);
        }

        /// <summary>
        /// Resolves the repository root without machine-specific paths.
        /// </summary>
        private static string ResolveRepositoryRoot()
        {
            DirectoryInfo? directory =
                new(
                    AppContext.BaseDirectory);

            while (directory is not null)
            {
                var hostProjectPath =
                    Path.Combine(
                        directory.FullName,
                        "implementations",
                        "dotnet",
                        "src",
                        "Multiplexed.AI.McpServer.Host",
                        "Multiplexed.AI.McpServer.Host.csproj");

                var testsProjectPath =
                    Path.Combine(
                        directory.FullName,
                        "implementations",
                        "dotnet",
                        "Tests",
                        "Multiplexed.AI.McpServer.Tests.Integration",
                        "Multiplexed.AI.McpServer.Tests.Integration.csproj");

                if (File.Exists(hostProjectPath) &&
                    File.Exists(testsProjectPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "The deterministic-ai-runtime repository root could not be resolved.");
        }
    }

    /// <summary>
    /// Serializes real gRPC Runtime Pool proofs that own local process ports.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GrpcRuntimePoolMcpProofCollection :
        ICollectionFixture<GrpcProcessPoolMcpProofFixture>
    {
        /// <summary>
        /// Gets the collection name.
        /// </summary>
        public const string Name =
            "gRPC Runtime Pool MCP proof collection";
    }

    /// <summary>
    /// Resolves one exact logical control-plane identity.
    /// </summary>
    internal sealed class GrpcRuntimePoolFixedControlPlaneIdResolver :
        IAiControlPlaneIdResolver
    {
        private readonly string controlPlaneId;

        /// <summary>
        /// Initializes the resolver.
        /// </summary>
        public GrpcRuntimePoolFixedControlPlaneIdResolver(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            this.controlPlaneId =
                controlPlaneId.Trim();
        }

        /// <inheritdoc />
        public Task<string> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                this.controlPlaneId);
        }

        /// <inheritdoc />
        public Task<string> ResolveAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                this.controlPlaneId);
        }

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<string, string>>
            ResolveMetadataAsync(
                AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyDictionary<string, string> metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["controlPlaneId"] =
                        this.controlPlaneId,
                    ["logicalControlPlaneId"] =
                        this.controlPlaneId,
                    ["runtime.controlPlaneId"] =
                        this.controlPlaneId
                };

            return Task.FromResult(metadata);
        }
    }
}
