using System.Text.Json;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Emits one production KubernetesPool host profile used by the external-SDK matrix runner.
    /// The profile composes existing production settings; it does not implement Kubernetes lifecycle.
    /// </summary>
    [Trait("Category", "HttpKubernetesPoolMatrixProfile")]
    public sealed class HttpKubernetesPoolExternalSdkMatrixProfileTests
    {
        private const string Project = "matrix";
        private const string TenantId = "matrix-tenant";
        private const string TenantGroupId = "matrix-group";
        private const string RemotePythonReference = "matrix-kubernetes-python";

        /// <summary>
        /// Writes a control-plane profile with publication authority on the parent and worker-polling authority only
        /// on RuntimeInstanceOnly children inside the existing KubernetesPool Pod.
        /// </summary>
        [Fact]
        public void Http_KubernetesPool_Should_Write_ExternalSdk_Matrix_Profile()
        {
            var outputPath = Required("MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_PROFILE_PATH");
            var runtimeImage = Required("MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_RUNTIME_IMAGE");
            var controlPlaneId = string.Concat("matrix-kubernetes-sdk-", Guid.NewGuid().ToString("N")[..8]);

            var baseline = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();
            var tenant = baseline.Tenants.Single() with
            {
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                RuntimeInstanceIdPrefix = "matrix-kubernetes-runtime",
                MaxRuntimeInstances = 1,
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                ExpectCapacityOverflow = false
            };
            var scenario = baseline with
            {
                Name = "matrix-kubernetes-external-sdk",
                ControlPlaneIdPrefix = "matrix-kubernetes-sdk",
                Tenants = new[] { tenant },
                // Match the existing KubernetesPool production canary: persist the run first so
                // the shared-queue dispatcher owns scale-out/requeue until pool capacity is visible.
                SubmitMode = ProductionRuntimeSubmitMode.QueueFirst,
                AssertReplayLedgerTrace = false,
                AssertRetention = false,
                AssertMaxRuntimeInstances = false,
                AssertTenantIsolation = false
            };

            var profile = new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile(maximumPodCount: 1);
            var settings = HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                Required("MULTIPLEXED_AI_MATRIX_CONTROL_PLANE_HOST_DLL"),
                profile);

            ConfigureReplaySafePayloadStore(settings);

            var requireImmutableImage =
                string.Equals(
                    Environment.GetEnvironmentVariable("MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_REQUIRE_IMMUTABLE_IMAGE"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            if (requireImmutableImage)
            {
                var separator = runtimeImage.LastIndexOf('@');
                if (separator <= 0 || separator == runtimeImage.Length - 1)
                {
                    throw new InvalidOperationException("Immutable Kubernetes matrix images require repository@sha256:<digest>.");
                }
                settings["AiKubernetesRuntimePoolHost:RuntimeImage"] = string.Empty;
                settings["AiKubernetesRuntimePoolHost:RuntimeImageRepository"] = runtimeImage[..separator];
                settings["AiKubernetesRuntimePoolHost:RuntimeImageDigest"] = runtimeImage[(separator + 1)..];
                settings["AiKubernetesRuntimePoolHost:RequireImmutableRuntimeImage"] = "true";
            }
            else
            {
                settings["AiKubernetesRuntimePoolHost:RuntimeImage"] = runtimeImage;
                settings["AiKubernetesRuntimePoolHost:RuntimeImageRepository"] = string.Empty;
                settings["AiKubernetesRuntimePoolHost:RuntimeImageDigest"] = string.Empty;
                settings["AiKubernetesRuntimePoolHost:RequireImmutableRuntimeImage"] = "false";
            }
            settings["AiKubernetesRuntimePoolHost:ImagePullPolicy"] = "Never";
            // Retain failed resources until the runner captures this pool's startup logs.
            // The runner's existing finally block still owns scenario cleanup.
            settings["AiKubernetesRuntimePoolHost:DeleteResourcesOnFailure"] = "false";
            settings["AiKubernetesRuntimePoolHost:DotnetExecutablePath"] =
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_DOTNET_EXECUTABLE");
            settings["AiKubernetesRuntimePoolHost:RuntimeHostAssemblyPath"] =
                "/app/Multiplexed.AI.McpServer.Host.dll";
            settings["AiKubernetesRuntimePoolHost:WorkingDirectory"] = "/app";

            ConfigureControlPlaneHostedInvocation(settings, controlPlaneId);
            ConfigureRuntimeChildrenHostedInvocation(settings, controlPlaneId);
            ConfigureMatrixHarness(settings);
            ConfigureRbacProject(settings);

            var poolId = RequiredSetting(settings, "AiKubernetesRuntimePool:PoolId");
            var output = new
            {
                schemaVersion = 1,
                controlPlaneId,
                poolId,
                runtimeImage,
                immutableRuntimeImage = requireImmutableImage,
                submitMode = scenario.SubmitMode.ToString(),
                namespaceName = RequiredSetting(settings, "AiKubernetesRuntimePool:Namespace"),
                publicationRuntime = new
                {
                    reference = RemotePythonReference,
                    language = "python",
                    version = Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_VERSION"),
                    sha256 = Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_SHA256")
                },
                authority = new
                {
                    controlPlaneWorkerPolling = false,
                    controlPlaneDagReconciliation = true,
                    runtimeWorkerPolling = true,
                    runtimeDagReconciliation = false
                },
                settings
            };

            var path = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void ConfigureReplaySafePayloadStore(
            IDictionary<string, string?> settings)
        {
            var mongoConnectionString = RequiredSetting(settings, "ConnectionStrings:Mongo");
            var mongoDatabaseName = RequiredSetting(settings, "Mongo:DatabaseName");

            // The public publication store requires replay-safe payload persistence.
            // Match the already validated local/docker matrix contract: Mongo is durable
            // truth and Redis remains the bounded hot cache.
            settings["AiEngine:PayloadStore:Enabled"] = "true";
            settings["AiEngine:PayloadStore:Provider"] = "mongo-redis";
            settings["AiEngine:PayloadStore:RequireReplaySafePayloads"] = "true";
            settings["AiEngine:PayloadStore:Mongo:Enabled"] = "true";
            settings["AiEngine:PayloadStore:Mongo:ConnectionString"] = mongoConnectionString;
            settings["AiEngine:PayloadStore:Mongo:DatabaseName"] = mongoDatabaseName;
            settings["AiEngine:PayloadStore:RedisCache:Enabled"] = "true";

            // RuntimeInstanceOnly children execute inside Minikube, so their durable-store
            // endpoint must use the existing host.minikube.internal production-test path.
            Child(settings, "AiEngine__PayloadStore__Enabled", "true");
            Child(settings, "AiEngine__PayloadStore__Provider", "mongo-redis");
            Child(settings, "AiEngine__PayloadStore__RequireReplaySafePayloads", "true");
            Child(settings, "AiEngine__PayloadStore__Mongo__Enabled", "true");
            Child(settings, "AiEngine__PayloadStore__Mongo__ConnectionString", KubernetesSdkScenarioConstants.MongoConnectionString);
            Child(settings, "AiEngine__PayloadStore__Mongo__DatabaseName", mongoDatabaseName);
            Child(settings, "AiEngine__PayloadStore__RedisCache__Enabled", "true");
            Child(settings, "ConnectionStrings__Mongo", KubernetesSdkScenarioConstants.MongoConnectionString);
            Child(settings, "ConnectionStrings__Redis", KubernetesSdkScenarioConstants.RedisConnectionString);
            Child(settings, "Mongo__DatabaseName", mongoDatabaseName);
        }

        private static void ConfigureControlPlaneHostedInvocation(
            IDictionary<string, string?> settings,
            string controlPlaneId)
        {
            settings["AiHostedInvocation:Enabled"] = "true";
            settings["AiHostedInvocation:EnableWorkerPolling"] = "false";
            settings["AiHostedInvocation:EnableDagReconciliation"] = "true";
            settings["AiHostedInvocation:EnableLocalWorkerProfiles"] = "false";
            settings["AiHostedInvocation:TenantId"] = TenantId;
            settings["AiHostedInvocation:TenantGroupId"] = TenantGroupId;
            settings["AiHostedInvocation:ControlPlaneId"] = controlPlaneId;
            settings["AiHostedInvocation:MaxConcurrentProcesses"] = "3";
            settings["AiHostedInvocation:PollPageSize"] = "16";
            settings["AiHostedInvocation:PollIntervalMilliseconds"] = "250";

            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:Reference"] = RemotePythonReference;
            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:ExecutionLanguage"] = "python";
            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:RuntimeVersion"] =
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_VERSION");
            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:RuntimeSha256"] =
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_SHA256");
            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:OperatingSystem"] = "linux";
            settings["AiHostedInvocation:PublicationOnlyRuntimes:0:Architecture"] =
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_ARCHITECTURE");
        }

        private static void ConfigureRuntimeChildrenHostedInvocation(
            IDictionary<string, string?> settings,
            string controlPlaneId)
        {
            Child(settings, "AiHostedInvocation__Enabled", "true");
            Child(settings, "AiHostedInvocation__EnableWorkerPolling", "true");
            Child(settings, "AiHostedInvocation__EnableLocalWorkerProfiles", "true");
            Child(settings, "AiHostedInvocation__EnableDagReconciliation", "false");
            Child(settings, "AiHostedInvocation__TenantId", TenantId);
            Child(settings, "AiHostedInvocation__TenantGroupId", TenantGroupId);
            Child(settings, "AiHostedInvocation__ControlPlaneId", controlPlaneId);
            Child(settings, "AiHostedInvocation__MaxConcurrentProcesses", "3");
            Child(settings, "AiHostedInvocation__PollPageSize", "16");
            Child(settings, "AiHostedInvocation__PollIntervalMilliseconds", "250");

            ChildRuntime(
                settings,
                "DotNet",
                "matrix-kubernetes-dotnet",
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_DOTNET_VERSION"),
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_DOTNET_EXECUTABLE"),
                "/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.dll",
                "/app/workers/dotnet",
                "/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.deps.json",
                "/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.runtimeconfig.json");
            ChildRuntime(
                settings,
                "TypeScript",
                "matrix-kubernetes-typescript",
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_NODE_VERSION"),
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_NODE_EXECUTABLE"),
                "/app/workers/typescript/worker.mjs",
                "/app/workers/typescript");
            ChildRuntime(
                settings,
                "Python",
                RemotePythonReference,
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_VERSION"),
                Required("MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_EXECUTABLE"),
                "/app/workers/python/worker.py",
                "/app/workers/python");
        }

        private static void ConfigureMatrixHarness(IDictionary<string, string?> settings)
        {
            settings["AiMatrixHarness:Enabled"] = "true";
            settings["AiMatrixHarness:BearerToken"] = "matrix-e2e-token";
            settings["AiMatrixHarness:UserId"] = "matrix-user";
            settings["AiMatrixHarness:TenantId"] = TenantId;
            settings["AiMatrixHarness:TenantGroupId"] = TenantGroupId;
            settings["AiMatrixHarness:Namespace"] = "default";
            settings["AiMatrixHarness:ExecutionContextTtlSeconds"] = "3600";
            settings["AiMatrixHarness:PublicEndpoint"] = "http://127.0.0.1:8081/mcp";
            settings["AiMatrixHarness:Topology"] = "kubernetes";
            settings["AiMatrixHarness:Provider"] = "KubernetesPool";
            settings["AiMatrixHarness:RuntimeProvider"] = "KubernetesPool";
            settings["AiMatrixHarness:WorkerExecutionProvider"] = "TrustedProcess";
            settings["AiMatrixHarness:PythonEnvironmentRef"] = RemotePythonReference;
            settings["AiMatrixHarness:ManifestPath"] =
                Required("MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_MANIFEST_PATH");
        }

        /// <summary>
        /// Aligns the seeded context and both host-owned TRN builders. A snapshot's Project
        /// identifies its owner but does not configure the child's authorization engine.
        /// </summary>
        internal static void ConfigureRbacProject(IDictionary<string, string?> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings["AiMatrixHarness:Project"] = Project;
            settings["Multiplexed.Rbac.Core:Project"] = Project;
            Child(settings, "Multiplexed.Rbac.Core__Project", Project);
        }

        private static void ChildRuntime(
            IDictionary<string, string?> settings,
            string name,
            string reference,
            string version,
            string executable,
            string worker,
            string workingDirectory,
            string? deps = null,
            string? runtimeConfig = null)
        {
            Child(settings, string.Concat("AiHostedInvocation__", name, "__Reference"), reference);
            Child(settings, string.Concat("AiHostedInvocation__", name, "__RuntimeVersion"), version);
            Child(settings, string.Concat("AiHostedInvocation__", name, "__ExecutablePath"), executable);
            Child(settings, string.Concat("AiHostedInvocation__", name, "__WorkerPath"), worker);
            Child(settings, string.Concat("AiHostedInvocation__", name, "__WorkingDirectory"), workingDirectory);
            if (!string.IsNullOrWhiteSpace(deps))
            {
                Child(settings, string.Concat("AiHostedInvocation__", name, "__WorkerDepsPath"), deps);
            }
            if (!string.IsNullOrWhiteSpace(runtimeConfig))
            {
                Child(settings, string.Concat("AiHostedInvocation__", name, "__WorkerRuntimeConfigPath"), runtimeConfig);
            }
        }

        private static void Child(
            IDictionary<string, string?> settings,
            string environmentName,
            string value)
        {
            settings[string.Concat(
                "AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:",
                environmentName)] = value;
        }

        private static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(string.Concat("Required matrix environment variable is missing: ", name));
            }
            return value;
        }

        private static string RequiredSetting(
            IDictionary<string, string?> settings,
            string name)
        {
            if (!settings.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(string.Concat("Required composed setting is missing: ", name));
            }
            return value;
        }
    }
}
