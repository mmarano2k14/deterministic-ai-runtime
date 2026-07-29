using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using System.Globalization;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic MCP server host for startup-bound integration tests.
    /// </summary>
    public sealed class GenericMcpServerTestHost
        : WebApplicationFactory<Program>
    {
        private const string ControlPlaneIdSettingKey = "AiEngine:ControlPlane:ControlPlaneId";
        private const string RegistrationControlPlaneIdSettingKey = "AiRuntimeInstanceRegistration:ControlPlaneId";
        private const string RuntimeInstanceIdSettingKey = "AiRuntimeInstanceRegistration:RuntimeInstanceId";
        private const string EngineRuntimeInstanceIdSettingKey = "AiEngine:RuntimeInstanceId";
        private const string HostModeSettingKey = "AiMcpHost:Mode";
        private const string HttpScaleOutModeSettingKey = "AiHttpRuntimeScaleOut:Mode";
        private const string UseRegisteringTestRuntimeHostManagerSettingKey = "Tests:UseRegisteringTestRuntimeHostManager";
        private const string HttpControlPlaneMode = "ControlPlaneWithHttpRuntimeInstances";
        private const string GrpcControlPlaneMode = "ControlPlaneWithGrpcRuntimeInstances";
        private const string LocalControlPlaneMode = "ControlPlaneWithLocalRuntimeInstances";
        private const string HttpScaleOutHostManagerMode = "HostManager";
        private const string UseCapturingLedgerRecorderSettingKey = "Tests:UseCapturingLedgerRecorder";

        /// <summary>
        /// The configuration settings used to start the test host.
        /// </summary>
        private readonly IReadOnlyDictionary<string, string?> settings;

        /// <summary>
        /// The optional single runtime HTTP client used by HTTP provider tests.
        /// </summary>
        private readonly HttpClient? runtimeClient;

        /// <summary>
        /// The runtime HTTP clients indexed by runtime instance identifier.
        /// </summary>
        private readonly IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class.
        /// </summary>
        /// <param name="settings">The configuration settings used to start the test host.</param>
        /// <param name="runtimeClient">The optional runtime HTTP client.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            HttpClient? runtimeClient = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(this.settings);

            this.runtimeClient = runtimeClient;

            runtimeClientsByRuntimeInstanceId =
                runtimeClient is null
                    ? new Dictionary<string, HttpClient>()
                    : new Dictionary<string, HttpClient>
                    {
                        ["default"] = runtimeClient
                    };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class.
        /// </summary>
        /// <param name="settings">The configuration settings used to start the test host.</param>
        /// <param name="runtimeClients">The runtime HTTP clients.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyList<HttpClient> runtimeClients)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(this.settings);

            ArgumentNullException.ThrowIfNull(runtimeClients);

            runtimeClient = runtimeClients.Count == 0 ? null : runtimeClients[0];

            var clients =
                runtimeClients
                    .Select(
                        (client, index) => new
                        {
                            RuntimeInstanceId = $"runtime-http-{index + 1}",
                            Client = client
                        })
                    .ToDictionary(
                        item => item.RuntimeInstanceId,
                        item => item.Client,
                        StringComparer.Ordinal);

            if (runtimeClient is not null)
            {
                clients["default"] = runtimeClient;
            }

            runtimeClientsByRuntimeInstanceId = clients;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class.
        /// </summary>
        /// <param name="settings">The configuration settings used to start the test host.</param>
        /// <param name="runtimeClientsByRuntimeInstanceId">The runtime HTTP clients indexed by runtime instance identifier.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(this.settings);

            this.runtimeClientsByRuntimeInstanceId =
                runtimeClientsByRuntimeInstanceId
                ?? throw new ArgumentNullException(nameof(runtimeClientsByRuntimeInstanceId));

            foreach (var pair in runtimeClientsByRuntimeInstanceId)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException(
                        "Runtime HTTP client dictionary keys must be non-empty runtime instance identifiers.",
                        nameof(runtimeClientsByRuntimeInstanceId));
                }

                ArgumentNullException.ThrowIfNull(pair.Value);
            }

            runtimeClient = runtimeClientsByRuntimeInstanceId.Values.FirstOrDefault();
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

            foreach (var setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = FakeAuthHandler.AuthenticationScheme;
                        options.DefaultChallengeScheme = FakeAuthHandler.AuthenticationScheme;
                        options.DefaultScheme = FakeAuthHandler.AuthenticationScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(
                        FakeAuthHandler.AuthenticationScheme,
                        _ => { });

                services.AddAuthorization();

                var testConfiguration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(settings)
                        .Build();

                services.RemoveAll<IConfigureOptions<AiHttpRuntimeScaleOutOptions>>();
                services.Configure<AiHttpRuntimeScaleOutOptions>(
                    testConfiguration.GetSection("AiHttpRuntimeScaleOut"));

                services.RemoveAll<IConfigureOptions<AiRuntimeProcessHostCreationOptions>>();
                services.Configure<AiRuntimeProcessHostCreationOptions>(
                    testConfiguration.GetSection("AiRuntimeProcessHostCreation"));

                RegisterKubernetesRuntimePoolTestOptions(
                    services,
                    testConfiguration);

                if (ShouldUseCapturingLedgerRecorder(settings))
                {
                    RegisterIntegrationLedgerProofServices(services);
                }
                else
                {
                    services.AddAiControlPlaneRuntimeObservability();
                }

                RegisterHostManagerModeTestServices(services);
            });

            builder.ConfigureServices(services =>
            {
                if (ShouldUseRealNetworkHttpClient(settings))
                {
                    Console.WriteLine(
                        "[TEST MCP HOST] HTTP HostManager Process mode detected. Runtime HTTP client factory override skipped. Real network HttpClient preserved.");

                    return;
                }

                if (runtimeClientsByRuntimeInstanceId.Count == 1 &&
                    runtimeClient is not null)
                {
                    services.AddSingleton(runtimeClient);
                    services.AddSingleton<IHttpClientFactory>(new TestRuntimeHttpClientFactory(runtimeClient));

                    Console.WriteLine(
                        "[TEST MCP HOST] Single runtime HTTP client injected into control-plane host.");

                    return;
                }

                services.AddSingleton(runtimeClientsByRuntimeInstanceId);
                services.AddSingleton<IReadOnlyDictionary<string, HttpClient>>(runtimeClientsByRuntimeInstanceId);
                services.AddSingleton<IHttpClientFactory>(new MultiRuntimeHttpClientFactory(runtimeClientsByRuntimeInstanceId));

                Console.WriteLine(
                    $"[TEST MCP HOST] Runtime HTTP client factory injected into control-plane host. RuntimeClientCount='{runtimeClientsByRuntimeInstanceId.Count}', RuntimeInstances='{string.Join(", ", runtimeClientsByRuntimeInstanceId.Keys)}'.");
            });
        }

        /// <summary>
        /// Rebinds the exact Kubernetes Runtime Pool test settings after the application
        /// has registered its production option bindings.
        /// </summary>
        /// <remarks>
        /// Destructive Kubernetes scenarios must not silently fall back to an older
        /// NodePort transport contract. Rebinding only the explicitly supplied sections
        /// makes the effective test contract deterministic and fail-fast.
        /// </remarks>
        private void RegisterKubernetesRuntimePoolTestOptions(
            IServiceCollection services,
            IConfiguration testConfiguration)
        {
            var hasRuntimePoolSettings =
                settings.Keys.Any(key =>
                    key.StartsWith(
                        "AiKubernetesRuntimePool:",
                        StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith(
                        "AiKubernetesRuntimePoolHost:",
                        StringComparison.OrdinalIgnoreCase));

            if (!hasRuntimePoolSettings)
            {
                return;
            }

            services.RemoveAll<
                IConfigureOptions<AiKubernetesRuntimePoolOptions>>();
            services.Configure<AiKubernetesRuntimePoolOptions>(
                testConfiguration.GetSection("AiKubernetesRuntimePool"));

            services.RemoveAll<
                IConfigureOptions<AiKubernetesRuntimePoolHostOptions>>();
            services.Configure<AiKubernetesRuntimePoolHostOptions>(
                testConfiguration.GetSection("AiKubernetesRuntimePoolHost"));

            services.RemoveAll<
                IConfigureOptions<AiKubernetesRuntimeHostOptions>>();
            services.Configure<AiKubernetesRuntimeHostOptions>(
                testConfiguration.GetSection("AiKubernetesRuntimeHost"));

            services.PostConfigure<AiKubernetesRuntimePoolOptions>(options =>
            {
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePool:Enabled",
                    options.Enabled.ToString());
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePool:PoolId",
                    options.PoolId);
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePool:ProviderName",
                    options.ProviderName);
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePool:TransportName",
                    options.TransportName);

                Console.WriteLine(
                    $"[TEST MCP HOST] Kubernetes Runtime Pool effective options. Enabled='{options.Enabled}', PoolId='{options.PoolId}', ProviderName='{options.ProviderName}', TransportName='{options.TransportName}'.");
            });

            services.PostConfigure<AiKubernetesRuntimePoolHostOptions>(options =>
            {
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePoolHost:ServiceType",
                    options.ServiceType);
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimePoolHost:UseGatewayTransportEndpoint",
                    options.UseGatewayTransportEndpoint.ToString());

                Console.WriteLine(
                    $"[TEST MCP HOST] Kubernetes Runtime Pool host effective options. ServiceType='{options.ServiceType}', UseGatewayTransportEndpoint='{options.UseGatewayTransportEndpoint}', ClientMode='{options.ClientMode}'.");
            });

            services.PostConfigure<AiKubernetesRuntimeHostOptions>(options =>
            {
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimeHost:UseGatewayTransportEndpoint",
                    options.UseGatewayTransportEndpoint.ToString());
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimeHost:UsePortForwardTransportEndpoint",
                    options.UsePortForwardTransportEndpoint.ToString());
                ValidateEffectiveSetting(
                    "AiKubernetesRuntimeHost:PublishNodePortTransportEndpoint",
                    options.PublishNodePortTransportEndpoint.ToString());

                Console.WriteLine(
                    $"[TEST MCP HOST] Kubernetes Gateway effective options. UseGatewayTransportEndpoint='{options.UseGatewayTransportEndpoint}', UsePortForwardTransportEndpoint='{options.UsePortForwardTransportEndpoint}', PublishNodePortTransportEndpoint='{options.PublishNodePortTransportEndpoint}', GatewayName='{options.GatewayName}'.");
            });

            Console.WriteLine(
                "[TEST MCP HOST] Kubernetes Runtime Pool options rebound from exact in-memory scenario settings.");
        }

        /// <summary>
        /// Verifies that a strongly typed option retained the exact scenario setting.
        /// </summary>
        private void ValidateEffectiveSetting(
            string key,
            string actualValue)
        {
            if (!settings.TryGetValue(key, out var expectedValue)
                || string.IsNullOrWhiteSpace(expectedValue))
            {
                return;
            }

            if (string.Equals(
                    expectedValue,
                    actualValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                string.Concat(
                    "Kubernetes Runtime Pool test option binding mismatch. Key='",
                    key,
                    "', Expected='",
                    expectedValue,
                    "', Actual='",
                    actualValue,
                    "'."));
        }

        private static bool ShouldUseCapturingLedgerRecorder(
            IReadOnlyDictionary<string, string?> settings)
        {
            return !settings.TryGetValue(UseCapturingLedgerRecorderSettingKey, out var value) ||
                bool.Parse(value ?? "true");
        }

        /// <summary>
        /// Registers test-only ledger capture services used by production scenario proof outputs.
        /// </summary>
        /// <param name="services">The service collection.</param>
        private static void RegisterIntegrationLedgerProofServices(
            IServiceCollection services)
        {
            services.TryAddSingleton<CapturingIntegrationDecisionLedgerRecorder>();
            services.RemoveAll<IAiDecisionLedgerRecorder>();
            services.AddSingleton<IAiDecisionLedgerRecorder>(serviceProvider =>
                serviceProvider.GetRequiredService<CapturingIntegrationDecisionLedgerRecorder>());
            services.AddAiControlPlaneRuntimeObservability();

            Console.WriteLine(
                "[TEST MCP HOST] Integration control-plane ledger recorder registered.");
        }

        /// <summary>
        /// Determines whether the test host must preserve the real network HTTP client instead of installing fixture routing clients.
        /// </summary>
        /// <param name="settings">The test host settings.</param>
        /// <returns><c>true</c> when real HTTP process hosts are used; otherwise, <c>false</c>.</returns>
        private static bool ShouldUseRealNetworkHttpClient(
            IReadOnlyDictionary<string, string?> settings)
        {
            if (!settings.TryGetValue(HttpScaleOutModeSettingKey, out var scaleOutMode) ||
                !string.Equals(scaleOutMode, HttpScaleOutHostManagerMode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!settings.TryGetValue(UseRegisteringTestRuntimeHostManagerSettingKey, out var value))
            {
                return false;
            }

            return bool.TryParse(value, out var parsed) &&
                !parsed;
        }

        /// <summary>
        /// Registers test-only runtime host manager services when HTTP HostManager mode is enabled.
        /// </summary>
        /// <param name="services">The service collection.</param>
        private void RegisterHostManagerModeTestServices(
            IServiceCollection services)
        {
            if (!settings.TryGetValue(HttpScaleOutModeSettingKey, out var mode) ||
                !string.Equals(mode, HttpScaleOutHostManagerMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var useRegisteringTestRuntimeHostManager =
                !settings.TryGetValue(UseRegisteringTestRuntimeHostManagerSettingKey, out var value) ||
                bool.Parse(value ?? "true");

            if (!useRegisteringTestRuntimeHostManager)
            {
                Console.WriteLine(
                    "[TEST MCP HOST] HTTP HostManager scale-out mode enabled. Default runtime host manager preserved.");

                return;
            }

            services.RemoveAll<IAiRuntimeHostManager>();
            services.AddSingleton<IAiRuntimeHostManager, RegisteringTestRuntimeHostManager>();

            Console.WriteLine(
                "[TEST MCP HOST] HTTP HostManager scale-out mode enabled. Test runtime host manager registered.");
        }

        /// <summary>
        /// Validates the required MCP control-plane test host settings.
        /// </summary>
        /// <param name="settings">The settings to validate.</param>
        private static void ValidateSettings(
            IReadOnlyDictionary<string, string?> settings)
        {
            var mode = GetRequiredSetting(settings, HostModeSettingKey);

            if (!IsSupportedControlPlaneMode(mode))
            {
                throw new ArgumentException(
                    $"Generic MCP server test host requires '{HostModeSettingKey}' to be one of " +
                    $"'{HttpControlPlaneMode}', '{GrpcControlPlaneMode}' or '{LocalControlPlaneMode}', but found '{mode}'.",
                    nameof(settings));
            }

            var controlPlaneId = GetRequiredSetting(settings, ControlPlaneIdSettingKey);
            var registrationControlPlaneId = GetRequiredSetting(settings, RegistrationControlPlaneIdSettingKey);

            if (!string.Equals(NormalizeKeySegment(controlPlaneId), NormalizeKeySegment(registrationControlPlaneId), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Control-plane id mismatch. Setting '{ControlPlaneIdSettingKey}' is '{controlPlaneId}', " +
                    $"but '{RegistrationControlPlaneIdSettingKey}' is '{registrationControlPlaneId}'.",
                    nameof(settings));
            }

            var registrationRuntimeInstanceId = GetRequiredSetting(settings, RuntimeInstanceIdSettingKey);
            var engineRuntimeInstanceId = GetRequiredSetting(settings, EngineRuntimeInstanceIdSettingKey);

            if (!string.Equals(NormalizeKeySegment(registrationRuntimeInstanceId), NormalizeKeySegment(engineRuntimeInstanceId), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Runtime instance id mismatch. Setting '{RuntimeInstanceIdSettingKey}' is '{registrationRuntimeInstanceId}', " +
                    $"but '{EngineRuntimeInstanceIdSettingKey}' is '{engineRuntimeInstanceId}'.",
                    nameof(settings));
            }
        }

        /// <summary>
        /// Determines whether the supplied MCP host mode is supported by this generic test host.
        /// </summary>
        /// <param name="mode">The host mode.</param>
        /// <returns><c>true</c> when the mode is supported; otherwise, <c>false</c>.</returns>
        private static bool IsSupportedControlPlaneMode(
            string mode)
        {
            return string.Equals(mode, HttpControlPlaneMode, StringComparison.Ordinal)
                || string.Equals(mode, GrpcControlPlaneMode, StringComparison.Ordinal)
                || string.Equals(mode, LocalControlPlaneMode, StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets a required setting value.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        /// <param name="key">The required setting key.</param>
        /// <returns>The required setting value.</returns>
        private static string GetRequiredSetting(
            IReadOnlyDictionary<string, string?> settings,
            string key)
        {
            if (!settings.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Required MCP control-plane host setting '{key}' is missing.",
                    nameof(settings));
            }

            return value;
        }

        /// <summary>
        /// Normalizes a setting value for key segment comparison.
        /// </summary>
        /// <param name="value">The setting value.</param>
        /// <returns>The normalized value.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Test runtime host manager that directly registers runtime capacity for legacy HostManager tests.
        /// </summary>
        private sealed class RegisteringTestRuntimeHostManager : IAiRuntimeHostManager
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly IAiRuntimeInstanceCapacityStore capacityStore;

            public RegisteringTestRuntimeHostManager(
                IAiRuntimeInstanceRegistry registry,
                IAiRuntimeInstanceCapacityStore capacityStore)
            {
                this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
                this.capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            }

            /// <inheritdoc />
            public async Task<AiRuntimeHostStartResult> StartRuntimeAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                var metadata = CreateRuntimeMetadata(request);

                await registry
                    .RegisterAsync(
                        new AiRuntimeInstanceRegistration
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ControlPlaneId = request.ControlPlaneId,
                            ControlPlaneHostId = $"control-plane-host-{request.ControlPlaneId}",
                            HostId = request.RuntimeInstanceId,
                            RuntimeId = request.RuntimeInstanceId,
                            Role = AiRuntimeInstanceRole.Runtime,
                            WorkerCount = request.WorkerCountPerInstance,
                            QueueCapacity = request.LocalQueueCapacity,
                            MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                            RegisteredAtUtc = DateTimeOffset.UtcNow,
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                await capacityStore
                    .PublishAsync(
                        new AiRuntimeInstanceCapacityDescriptor
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ControlPlaneId = request.ControlPlaneId,
                            ControlPlaneHostId = $"control-plane-host-{request.ControlPlaneId}",
                            Role = AiRuntimeInstanceRole.Runtime,
                            Status = AiRuntimeInstanceStatus.Ready,
                            WorkerCount = request.WorkerCountPerInstance,
                            ActiveWorkerCount = 0,
                            AvailableWorkerCount = request.WorkerCountPerInstance,
                            MaxWorkersPerRun = request.WorkerCountPerInstance,
                            MinWorkersRequiredPerRun = 1,
                            QueuedRunCount = 0,
                            RunningRunCount = 0,
                            ActiveRunCount = 0,
                            MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                            MaxRunSlots = request.MaxConcurrentRunsPerInstance,
                            AvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                            ReservedRunSlots = 0,
                            EffectiveAvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                            IsQueuePaused = false,
                            CanAcceptRun = true,
                            LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new AiRuntimeHostStartResult
                {
                    Success = true,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ProviderName = request.ProviderName,
                    TransportName = request.TransportName,
                    TransportEndpoint = request.TransportEndpoint,
                    ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                    Metadata = metadata
                };
            }

            /// <summary>
            /// Creates metadata for the registered test runtime host.
            /// </summary>
            /// <param name="request">The runtime host start request.</param>
            /// <returns>The metadata dictionary.</returns>
            private static Dictionary<string, string> CreateRuntimeMetadata(
                AiRuntimeHostStartRequest request)
            {
                var metadata =
                    new Dictionary<string, string>(
                        request.Metadata,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = request.ProviderName,
                        ["provider.name"] = request.ProviderName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = request.TransportName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = request.TransportEndpoint ?? string.Empty,
                        ["runtime.instance.id"] = request.RuntimeInstanceId,
                        ["runtime.localQueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture),
                        ["queueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture),
                        ["controlPlaneId"] = request.ControlPlaneId
                    };

                if (!string.IsNullOrWhiteSpace(request.TenantId))
                {
                    metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
                }

                if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
                {
                    metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
                }

                if (!string.IsNullOrWhiteSpace(request.IsolationMode))
                {
                    metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode;
                }

                metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] =
                    request.PreferDedicatedCapacity.ToString();

                metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] =
                    request.AllowSharedFallback.ToString();

                return metadata;
            }
        }

        /// <summary>
        /// HTTP client factory that resolves runtime clients by runtime instance identifier.
        /// </summary>
        private sealed class MultiRuntimeHttpClientFactory : IHttpClientFactory
        {
            private readonly IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId;
            private readonly HttpClient startupRoutingClient;

            public MultiRuntimeHttpClientFactory(
                IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId)
            {
                this.clientsByRuntimeInstanceId =
                    clientsByRuntimeInstanceId
                    ?? throw new ArgumentNullException(nameof(clientsByRuntimeInstanceId));

                startupRoutingClient =
                    new HttpClient(
                        new RuntimeClientRoutingHandler(
                            clientsByRuntimeInstanceId))
                    {
                        BaseAddress = new Uri("http://localhost")
                    };
            }

            /// <inheritdoc />
            public HttpClient CreateClient(
                string name)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    clientsByRuntimeInstanceId.TryGetValue(name, out var client))
                {
                    return client;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var matchingClient =
                        clientsByRuntimeInstanceId
                            .FirstOrDefault(pair =>
                                name.Contains(pair.Key, StringComparison.Ordinal));

                    if (matchingClient.Value is not null)
                    {
                        return matchingClient.Value;
                    }
                }

                if (clientsByRuntimeInstanceId.TryGetValue("default", out var defaultClient))
                {
                    return defaultClient;
                }

                var fallbackClient = clientsByRuntimeInstanceId.Values.FirstOrDefault();

                if (fallbackClient is not null)
                {
                    return fallbackClient;
                }

                return startupRoutingClient;
            }

            /// <summary>
            /// Routes outgoing HTTP requests to the currently available runtime client.
            /// </summary>
            private sealed class RuntimeClientRoutingHandler : HttpMessageHandler
            {
                private readonly IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId;

                public RuntimeClientRoutingHandler(
                    IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId)
                {
                    this.clientsByRuntimeInstanceId =
                        clientsByRuntimeInstanceId
                        ?? throw new ArgumentNullException(nameof(clientsByRuntimeInstanceId));
                }

                /// <inheritdoc />
                protected override async Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    var client = ResolveClient();

                    var forwardedRequest =
                        await CloneRequestAsync(request, cancellationToken)
                            .ConfigureAwait(false);

                    if (forwardedRequest.RequestUri is not null &&
                        forwardedRequest.RequestUri.IsAbsoluteUri &&
                        string.Equals(forwardedRequest.RequestUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        forwardedRequest.RequestUri =
                            new Uri(forwardedRequest.RequestUri.PathAndQuery, UriKind.Relative);
                    }

                    return await client
                        .SendAsync(forwardedRequest, cancellationToken)
                        .ConfigureAwait(false);
                }

                private HttpClient ResolveClient()
                {
                    if (clientsByRuntimeInstanceId.TryGetValue("default", out var defaultClient))
                    {
                        return defaultClient;
                    }

                    var fallbackClient = clientsByRuntimeInstanceId.Values.FirstOrDefault();

                    if (fallbackClient is not null)
                    {
                        return fallbackClient;
                    }

                    throw new InvalidOperationException(
                        "No runtime HTTP client is available yet. The MCP control-plane was started before runtime hosts, but a runtime HTTP request was sent before any runtime client was registered.");
                }

                private static async Task<HttpRequestMessage> CloneRequestAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    var clone = new HttpRequestMessage(request.Method, request.RequestUri);

                    foreach (var header in request.Headers)
                    {
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    foreach (var option in request.Options)
                    {
                        clone.Options.TryAdd(option.Key, option.Value);
                    }

                    if (request.Content is not null)
                    {
                        var contentBytes =
                            await request.Content
                                .ReadAsByteArrayAsync(cancellationToken)
                                .ConfigureAwait(false);

                        clone.Content = new ByteArrayContent(contentBytes);

                        foreach (var header in request.Content.Headers)
                        {
                            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    return clone;
                }
            }
        }
    }
}