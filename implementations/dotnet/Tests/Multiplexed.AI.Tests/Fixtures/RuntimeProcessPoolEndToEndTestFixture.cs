using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides the complete strongly typed local configuration required by the real
    /// RuntimeInstanceOnly process-pool proof.
    /// </summary>
    /// <remarks>
    /// The fixture deliberately does not read environment variables or JSON files. Test settings
    /// are centralized here so future changes remain compile-time visible and affect only the test
    /// project.
    /// </remarks>
    public sealed class RuntimeProcessPoolEndToEndTestFixture :
        IAsyncLifetime
    {
        private const string RedisHost = "127.0.0.1";
        private const int RedisPort = 6379;
        private const int HttpBasePort = 6100;
        private const int GrpcBasePort = 6200;
        private const int PortRangeSize = 10;
        private IConnectionMultiplexer? redisConnection;

        /// <summary>
        /// Gets the total timeout of the real end-to-end proof.
        /// </summary>
        public TimeSpan TestTimeout { get; } =
            TimeSpan.FromMinutes(3);

        /// <summary>
        /// Gets the shared Redis/Memurai connection initialized for the fixture.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the fixture has not completed asynchronous initialization.
        /// </exception>
        public IConnectionMultiplexer RedisConnection =>
            this.redisConnection
            ?? throw new InvalidOperationException(
                "The runtime process-pool test fixture has not been initialized.");

        /// <summary>
        /// Initializes the shared Redis/Memurai connection using the standard local test endpoint.
        /// </summary>
        public async Task InitializeAsync()
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
                RedisHost,
                RedisPort);

            this.redisConnection =
                await ConnectionMultiplexer
                    .ConnectAsync(configuration)
                    .ConfigureAwait(false);
        }

        /// <summary>
        /// Closes the shared fixture connection.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (this.redisConnection is null)
            {
                return;
            }

            await this.redisConnection
                .CloseAsync()
                .ConfigureAwait(false);

            this.redisConnection.Dispose();
            this.redisConnection = null;
        }

        /// <summary>
        /// Creates unique first-class identities for one proof execution.
        /// </summary>
        public RuntimeProcessPoolEndToEndIdentity CreateIdentity()
        {
            var suffix = Guid.NewGuid().ToString("N");

            return new RuntimeProcessPoolEndToEndIdentity
            {
                ControlPlaneId =
                    string.Concat(
                        "runtime-pool-e2e-cp-",
                        suffix),
                PoolId =
                    string.Concat(
                        "runtime-pool-e2e-",
                        suffix)
            };
        }

        /// <summary>
        /// Applies only the dynamic control-plane identity required by the current proof execution.
        /// </summary>
        /// <param name="configuration">The host configuration manager.</param>
        /// <param name="identity">The unique proof identity.</param>
        public void ConfigureControlPlane(
            IConfigurationManager configuration,
            RuntimeProcessPoolEndToEndIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(identity);

            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AiEngine:ControlPlane:ControlPlaneId"] =
                        identity.ControlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] =
                        string.Concat(
                            "multiplexed-ai:",
                            identity.ControlPlaneId),
                    ["AiRuntimeInstanceRegistration:Enabled"] =
                        "false"
                });
        }

        /// <summary>
        /// Registers the exact logical control-plane identity used by the isolated process-pool
        /// proof.
        /// </summary>
        /// <param name="services">The test host service collection.</param>
        /// <param name="identity">The unique proof identity.</param>
        /// <remarks>
        /// The complete application host composes its control-plane resolver outside
        /// <c>AddAiControlPlane</c>. This isolated test host therefore supplies a deterministic
        /// first-class resolver through the fixture.
        /// </remarks>
        public void RegisterControlPlaneIdentity(
            IServiceCollection services,
            RuntimeProcessPoolEndToEndIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(identity);

            services.AddSingleton<IAiControlPlaneIdResolver>(
                new FixedAiControlPlaneIdResolver(
                    identity.ControlPlaneId));
        }

        /// <summary>
        /// Creates the fixed three-child process-pool settings used by the proof.
        /// </summary>
        /// <param name="identity">The unique proof identity.</param>
        public AiRuntimeProcessPoolOptions CreatePoolOptions(
            RuntimeProcessPoolEndToEndIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            return new AiRuntimeProcessPoolOptions
            {
                Enabled = true,
                PoolId = identity.PoolId,
                HostIdPrefix = "runtime-pool-e2e-host",
                RuntimeInstanceIdPrefix =
                    "runtime-pool-e2e-runtime",
                InitialProcessCount = 3,
                MinimumProcessCount = 3,
                MaximumProcessCount = 3,
                StartupParallelism = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// Creates the HTTP RuntimeInstanceOnly child settings used by the process-pool proofs.
        /// </summary>
        /// <param name="identity">The unique proof identity.</param>
        public AiRuntimeProcessPoolRuntimeInstanceOptions
            CreateRuntimeInstanceOptions(
                RuntimeProcessPoolEndToEndIdentity identity)
        {
            return CreateRuntimeInstanceOptions(
                identity,
                basePort: HttpBasePort,
                providerName: "http",
                transportName: "http",
                runtimeVersion:
                    "runtime-process-pool-http-e2e");
        }

        /// <summary>
        /// Creates the gRPC RuntimeInstanceOnly child settings used by the process-pool proof.
        /// </summary>
        /// <param name="identity">The unique proof identity.</param>
        public AiRuntimeProcessPoolRuntimeInstanceOptions
            CreateGrpcRuntimeInstanceOptions(
                RuntimeProcessPoolEndToEndIdentity identity)
        {
            return CreateRuntimeInstanceOptions(
                identity,
                basePort: GrpcBasePort,
                providerName: "grpc",
                transportName: "grpc",
                runtimeVersion:
                    "runtime-process-pool-grpc-e2e");
        }

        /// <summary>
        /// Creates one strongly typed transport-specific RuntimeInstanceOnly profile.
        /// </summary>
        private static AiRuntimeProcessPoolRuntimeInstanceOptions
            CreateRuntimeInstanceOptions(
                RuntimeProcessPoolEndToEndIdentity identity,
                int basePort,
                string providerName,
                string transportName,
                string runtimeVersion)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);

            var hostAssemblyPath =
                ResolveRuntimeHostAssemblyPath();

            return new AiRuntimeProcessPoolRuntimeInstanceOptions
            {
                RuntimeHostAssemblyPath = hostAssemblyPath,
                WorkingDirectory =
                    Path.GetDirectoryName(hostAssemblyPath),
                BasePort = basePort,
                MaxPort = checked(
                    basePort + PortRangeSize),
                EndpointHost = "127.0.0.1",
                ControlPlaneId = identity.ControlPlaneId,
                EnableControlPlaneDiscovery = false,
                RequireControlPlaneDiscovery = false,
                ExecutionContextSnapshot =
                    CreateExecutionContextSnapshot(identity),
                ProviderName = providerName,
                TransportName = transportName,
                RuntimeVersion = runtimeVersion,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 8,
                StartupTimeout =
                    TimeSpan.FromSeconds(90),
                ReadinessPollInterval =
                    TimeSpan.FromMilliseconds(100),
                HeartbeatInterval =
                    TimeSpan.FromSeconds(1),
                StopTimeoutSeconds = 15
            };
        }

        /// <summary>
        /// Creates the durable tenant context shared by all child runtimes.
        /// </summary>
        private static ExecutionContextSnapshot
            CreateExecutionContextSnapshot(
                RuntimeProcessPoolEndToEndIdentity identity)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = identity.PoolId,
                Project = "runtime-pool-e2e",
                UserId = "system",
                TenantId = "runtime-pool-e2e-tenant",
                TenantGroupId = "runtime-pool-e2e-group",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>()
            };
        }

        /// <summary>
        /// Resolves the host assembly from the repository build matching the current test build
        /// configuration and target framework.
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
                "The RuntimeInstanceOnly host assembly was not found for the current test build. " +
                "Build implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Multiplexed.AI.McpServer.Host.csproj " +
                $"with configuration '{buildConfiguration}' before running the Step 2G proof.",
                hostAssemblyPath);
        }

        /// <summary>
        /// Finds the repository root from the test binary location without relying on machine
        /// specific paths or external settings.
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
                        "Multiplexed.AI.Tests",
                        "Multiplexed.AI.Tests.csproj");

                if (File.Exists(hostProjectPath) &&
                    File.Exists(testsProjectPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "The deterministic-ai-runtime repository root could not be resolved from the test output directory.");
        }
    }

    /// <summary>
    /// Resolves one exact first-class control-plane identity for the isolated process-pool proof.
    /// </summary>
    internal sealed class FixedAiControlPlaneIdResolver :
        IAiControlPlaneIdResolver
    {
        private readonly string controlPlaneId;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="FixedAiControlPlaneIdResolver"/> class.
        /// </summary>
        /// <param name="controlPlaneId">The exact logical control-plane identifier.</param>
        public FixedAiControlPlaneIdResolver(
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

    /// <summary>
    /// Represents unique identities generated for one process-pool proof execution.
    /// </summary>
    public sealed record RuntimeProcessPoolEndToEndIdentity
    {
        /// <summary>
        /// Gets the unique logical control-plane identifier.
        /// </summary>
        public required string ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the unique logical process-pool identifier.
        /// </summary>
        public required string PoolId { get; init; }
    }
}
