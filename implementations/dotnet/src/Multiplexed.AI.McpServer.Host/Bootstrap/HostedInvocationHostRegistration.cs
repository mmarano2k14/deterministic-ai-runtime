using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.Invocation.Durable.DI;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Runtime.Invocation.Workers.TypeScript;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Materializes deployment-provisioned trusted-process workers and optional isolated OCI workers through the existing
    /// publication, durable journal and worker supervisor authorities. It does not add a scheduler, queue, recovery path
    /// or result-acceptance authority.
    /// </summary>
    public static class HostedInvocationHostRegistration
    {
        public static void Configure(IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection("AiHostedInvocation").Get<AiHostedInvocationHostOptions>()
                ?? new AiHostedInvocationHostOptions();
            if (!options.Enabled)
            {
                return;
            }

            Validate(options);

            var processProfiles = options.EnableLocalWorkerProfiles
                ? CreateLocalWorkerProfiles(options)
                : Array.Empty<AiWorkerProcessProfile>();
            foreach (var profile in processProfiles)
            {
                // Deployment-owned launch paths are validated before any durable invocation can park.
                AiWorkerLaunchPaths.ValidateProfile(profile);
            }

            var containerProfiles = options.Container.Enabled
                ? CreateContainers(options.Container)
                : Array.Empty<AiContainerWorkerProfile>();
            var publicationOnlyRuntimes = CreatePublicationOnlyRuntimes(options.PublicationOnlyRuntimes);
            var environments = processProfiles.Select(profile => profile.Runtime)
                .Concat(containerProfiles.Select(profile => profile.Runtime))
                .Concat(publicationOnlyRuntimes.Select(runtime => runtime.Runtime))
                .ToArray();
            var descriptors = processProfiles
                .Select(profile => (Reference: profile.Runtime.Reference, Descriptor: profile.ExecutionDescriptor!))
                .Concat(containerProfiles.Select(profile => (Reference: profile.Runtime.Reference, Descriptor: profile.ExecutionDescriptor)))
                .Concat(publicationOnlyRuntimes.Select(runtime => (Reference: runtime.Runtime.Reference, Descriptor: runtime.Descriptor)))
                .ToDictionary(item => item.Reference, item => item.Descriptor, StringComparer.Ordinal);

            var environmentCatalog = new AiConfiguredPublicationEnvironmentCatalog(environments, descriptors);
            services.TryAddSingleton<IAiPublicationEnvironmentCatalog>(environmentCatalog);
            services.TryAddSingleton<IAiPublicationExecutionEnvironmentCatalog>(environmentCatalog);
            services.TryAddSingleton(new AiHostedInvocationEnvironmentSet(
                EnvironmentReference(processProfiles, publicationOnlyRuntimes, AiExecutionLanguages.DotNet),
                EnvironmentReference(processProfiles, publicationOnlyRuntimes, AiExecutionLanguages.TypeScript),
                EnvironmentReference(processProfiles, publicationOnlyRuntimes, AiExecutionLanguages.Python),
                containerProfiles.Select(profile => profile.Runtime).ToArray()));

            // Every enabled host resolves durable custom steps, including runtime hosts that do not
            // run reconciliation. Core registration installs adapters without starting a hosted loop.
            services.AddAiDurableInvocationDag();

            // The control-plane reconciler owns continuation scheduling after durable result acceptance.
            // Runtime hosts keep their existing execution/worker path without a second reconciliation loop.
            var invocationScope = new AiDurableInvocationScope(options.TenantId, options.TenantGroupId, options.ControlPlaneId);
            if (options.EnableDagReconciliation)
            {
                services.AddAiDurableInvocationDagReconciliation(new AiDurableInvocationDagReconciliationOptions
                {
                    Scopes = new[] { invocationScope },
                    Interval = TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds),
                    BatchSize = options.PollPageSize
                });
            }

            services.AddAiImmutablePublications(new AiPublicationOptions(
                new AiPublicationCapability("code", "publication", "publish"),
                new AiPublicationCapability("code", "publication", "read"),
                new AiPublicationCapability("code", "publication", "execute")));

            var admission = new AiWorkerExecutionAdmissionPolicy
            {
                AllowLegacyTrustedProcess = false,
                MinimumRequirements = ProcessRequirements()
            };
            services.AddAiHostedInvocationWorkers(
                new AiConfiguredWorkerProcessCatalog(processProfiles),
                new AiWorkerSupervisionOptions(maxConcurrentProcesses: options.MaxConcurrentProcesses),
                executionPolicy: admission);
            if (containerProfiles.Length > 0)
            {
                services.AddAiHostedInvocationContainerWorkers(
                    new AiConfiguredContainerWorkerCatalog(containerProfiles));
            }

            // Hosted custom policy families reuse the exact publication material and worker transport.
            // They do not create a native fallback or a second policy/scheduling authority.
            services.AddAiHostedConcurrencyPolicyExecution();
            services.AddAiHostedRetryPolicyExecution();
            services.AddAiHostedDelegationPolicyExecution();

            if (options.EnableWorkerPolling)
            {
                services.AddAiHostedInvocationWorkerPolling(new AiWorkerPollingOptions(
                    new[] { invocationScope },
                    AiExecutionLanguages.All,
                    pageSize: options.PollPageSize,
                    interval: TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds)));
            }
        }

        private static AiWorkerProcessProfile[] CreateLocalWorkerProfiles(AiHostedInvocationHostOptions options)
        {
            var dotnet = CreateDotNet(options.DotNet);
            var typescript = CreateTypeScript(options.TypeScript);
            var python = CreatePython(options.Python);
            return new[] { dotnet.Profile, typescript.Profile, python.Profile };
        }

        private static string EnvironmentReference(
            IReadOnlyList<AiWorkerProcessProfile> processProfiles,
            IReadOnlyList<(AiPublicationEnvironment Runtime, AiPublicationExecutionDescriptor Descriptor)> publicationOnlyRuntimes,
            string executionLanguage)
        {
            var local = processProfiles
                .Select(profile => profile.Runtime)
                .SingleOrDefault(runtime => string.Equals(runtime.ExecutionLanguage, executionLanguage, StringComparison.OrdinalIgnoreCase));
            if (local is not null)
            {
                return local.Reference;
            }

            var remote = publicationOnlyRuntimes
                .Select(runtime => runtime.Runtime)
                .SingleOrDefault(runtime => string.Equals(runtime.ExecutionLanguage, executionLanguage, StringComparison.OrdinalIgnoreCase));
            return remote?.Reference ?? string.Empty;
        }

        private static (AiWorkerProcessProfile Profile, string Hash) CreateDotNet(AiHostedRuntimeOptions options)
        {
            var executable = FullFile(options.ExecutablePath, "dotnet executable");
            var worker = FullFile(options.WorkerPath, ".NET worker assembly");
            var deps = FullFile(options.WorkerDepsPath, ".NET worker deps file");
            var runtimeConfig = FullFile(options.WorkerRuntimeConfigPath, ".NET worker runtimeconfig file");
            var executableHash = Hash(executable);
            var runtime = new AiPublicationEnvironment(options.Reference, AiExecutionLanguages.DotNet, options.RuntimeVersion, executableHash);
            var descriptor = Descriptor(executableHash);
            var working = WorkingDirectory(options, worker);
            var profile = AiDotNetWorkerProcessProfile.Create(
                runtime, executable, executableHash,
                worker, Hash(worker), deps, Hash(deps), runtimeConfig, Hash(runtimeConfig), working,
                environment: WorkerEnvironment(), executionDescriptor: descriptor,
                approvedLaunchRoots: LaunchRoots(executable, working));
            return (profile, executableHash);
        }

        private static (AiWorkerProcessProfile Profile, string Hash) CreateTypeScript(AiHostedRuntimeOptions options)
        {
            var executable = FullFile(options.ExecutablePath, "Node executable");
            var worker = FullFile(options.WorkerPath, "TypeScript worker loader");
            var executableHash = Hash(executable);
            var workerHash = Hash(worker);
            var runtime = AiTypeScriptWorkerProcessProfile.CreateRuntime(
                options.Reference, options.RuntimeVersion, executableHash, workerHash);
            var working = WorkingDirectory(options, worker);
            var descriptor = Descriptor(executableHash);
            var profile = AiTypeScriptWorkerProcessProfile.Create(
                runtime, executable, executableHash, worker, workerHash, working,
                environment: WorkerEnvironment(), executionDescriptor: descriptor,
                approvedLaunchRoots: LaunchRoots(executable, working));
            return (profile, executableHash);
        }

        private static (AiWorkerProcessProfile Profile, string Hash) CreatePython(AiHostedRuntimeOptions options)
        {
            var executable = FullFile(options.ExecutablePath, "Python executable");
            var worker = FullFile(options.WorkerPath, "Python worker loader");
            var executableHash = Hash(executable);
            var runtime = new AiPublicationEnvironment(options.Reference, AiExecutionLanguages.Python, options.RuntimeVersion, executableHash);
            var working = WorkingDirectory(options, worker);
            var descriptor = Descriptor(executableHash);
            var profile = AiPythonWorkerProcessProfile.Create(
                runtime, executable, executableHash, worker, Hash(worker), working,
                environment: WorkerEnvironment(), executionDescriptor: descriptor,
                approvedLaunchRoots: LaunchRoots(executable, working));
            return (profile, executableHash);
        }

        private static (AiPublicationEnvironment Runtime, AiPublicationExecutionDescriptor Descriptor)[]
            CreatePublicationOnlyRuntimes(IReadOnlyList<AiHostedPublicationOnlyRuntimeOptions>? options)
        {
            var configured = options ?? Array.Empty<AiHostedPublicationOnlyRuntimeOptions>();
            var runtimes = new List<(AiPublicationEnvironment, AiPublicationExecutionDescriptor)>(configured.Count);
            foreach (var item in configured)
            {
                ArgumentNullException.ThrowIfNull(item);
                AiPublicationExecutionDescriptors.ValidateDigest("sha256:" + item.RuntimeSha256);
                var runtime = new AiPublicationEnvironment(
                    item.Reference,
                    item.ExecutionLanguage,
                    item.RuntimeVersion,
                    item.RuntimeSha256);
                var descriptor = new AiPublicationExecutionDescriptor
                {
                    OperatingSystem = item.OperatingSystem,
                    Architecture = item.Architecture,
                    Artifact = new AiPublicationEnvironmentArtifact(
                        AiPublicationEnvironmentArtifactKind.HostRuntime,
                        "sha256:" + item.RuntimeSha256,
                        AiPublicationExecutionDescriptors.HostRuntimeMediaType),
                    Requirements = ProcessRequirements()
                };
                AiPublicationExecutionDescriptors.Validate(descriptor, runtime);
                runtimes.Add((runtime, descriptor));
            }
            return runtimes.ToArray();
        }

        private static AiContainerWorkerProfile[] CreateContainers(AiHostedContainerWorkersOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var engine = FullFile(options.EngineExecutablePath, "container engine executable");
            var workingDirectory = string.IsNullOrWhiteSpace(options.EngineWorkingDirectory)
                ? Path.GetDirectoryName(engine)!
                : FullDirectory(options.EngineWorkingDirectory, "container engine working directory");
            var engineHash = Hash(engine);
            var roots = LaunchRoots(engine, workingDirectory);
            var limits = new AiContainerWorkerResourceLimits
            {
                CpuMilliCores = options.CpuMilliCores,
                MemoryBytes = options.MemoryBytes,
                PidsLimit = options.PidsLimit,
                WritableWorkspaceBytes = options.WritableWorkspaceBytes
            };
            var configured = options.Runtimes ?? new List<AiHostedContainerRuntimeOptions>();
            if (configured.Count is < 1 or > 16)
                throw new InvalidOperationException("AiHostedInvocation container provider requires between 1 and 16 exact OCI runtimes.");

            var profiles = new List<AiContainerWorkerProfile>(configured.Count);
            foreach (var runtimeOptions in configured)
            {
                ArgumentNullException.ThrowIfNull(runtimeOptions);
                AiPublicationExecutionDescriptors.ValidateDigest(runtimeOptions.ImageDigest);
                var runtime = new AiPublicationEnvironment(
                    runtimeOptions.Reference,
                    runtimeOptions.ExecutionLanguage,
                    runtimeOptions.RuntimeVersion,
                    runtimeOptions.ImageDigest[7..]);
                var descriptor = new AiPublicationExecutionDescriptor
                {
                    OperatingSystem = AiContainerWorkerExecutionAdmission.InitialOperatingSystem,
                    Architecture = AiContainerWorkerExecutionAdmission.InitialArchitecture,
                    Artifact = new AiPublicationEnvironmentArtifact(
                        AiPublicationEnvironmentArtifactKind.OciImage,
                        runtimeOptions.ImageDigest,
                        AiPublicationExecutionDescriptors.OciImageMediaType),
                    Requirements = ContainerRequirements()
                };
                profiles.Add(new AiContainerWorkerProfile(
                    runtime,
                    descriptor,
                    engine,
                    engineHash,
                    workingDirectory,
                    runtimeOptions.ImageRepository,
                    options.ContainerOwnerScope,
                    limits,
                    runtimeOptions.ContainerUser,
                    options.EngineEnvironment,
                    roots,
                    options.HeartbeatMilliseconds));
            }
            return profiles.ToArray();
        }

        private static AiPublicationExecutionDescriptor Descriptor(string executableHash) => new()
        {
            OperatingSystem = AiWorkerExecutionAdmission.CurrentOperatingSystem,
            Architecture = AiWorkerExecutionAdmission.CurrentArchitecture,
            Artifact = new AiPublicationEnvironmentArtifact(
                AiPublicationEnvironmentArtifactKind.HostRuntime,
                "sha256:" + executableHash,
                AiPublicationExecutionDescriptors.HostRuntimeMediaType),
            Requirements = ProcessRequirements()
        };

        private static AiPublicationExecutionRequirements ProcessRequirements() => new()
        {
            IsolationTier = AiWorkerIsolationTier.TrustedProcess,
            NetworkEgress = AiWorkerNetworkEgress.HostNetwork,
            PathProtection = AiWorkerPathProtection.ValidatedPaths
        };

        private static AiPublicationExecutionRequirements ContainerRequirements() => new()
        {
            IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
            NetworkEgress = AiWorkerNetworkEgress.DenyAll,
            PathProtection = AiWorkerPathProtection.SealedClosure
        };

        private static IReadOnlyDictionary<string, string> WorkerEnvironment()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DOTNET_EnableDiagnostics"] = "0",
                ["PYTHONUTF8"] = "1"
            };
            if (OperatingSystem.IsWindows())
            {
                var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (!string.IsNullOrWhiteSpace(systemRoot))
                {
                    environment["SystemRoot"] = systemRoot;
                }
            }
            return environment;
        }

        private static string[] LaunchRoots(string executable, string workingDirectory) =>
            new[] { Path.GetDirectoryName(executable)!, workingDirectory }
                .Select(Path.GetFullPath)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();

        private static string WorkingDirectory(AiHostedRuntimeOptions options, string worker) =>
            string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Path.GetDirectoryName(worker)!
                : Path.GetFullPath(options.WorkingDirectory);

        private static string FullFile(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"AiHostedInvocation requires an exact {label} path.");
            }
            var path = Path.GetFullPath(value);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Configured {label} was not found.", path);
            }
            return path;
        }

        private static string FullDirectory(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"AiHostedInvocation requires an exact {label} path.");
            }
            var path = Path.GetFullPath(value);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Configured {label} was not found: {path}");
            }
            return path;
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void Validate(AiHostedInvocationHostOptions options)
        {
            if (options.MaxConcurrentProcesses is < 1 or > 32 ||
                options.PollPageSize is < 1 or > 100 ||
                options.PollIntervalMilliseconds is < 100 or > 300000)
            {
                throw new InvalidOperationException("AiHostedInvocation process and polling bounds are invalid.");
            }
            foreach (var scope in new[] { options.TenantId, options.TenantGroupId, options.ControlPlaneId })
            {
                if (string.IsNullOrWhiteSpace(scope))
                {
                    throw new InvalidOperationException("AiHostedInvocation requires an explicit tenant, tenant-group and control-plane scope.");
                }
            }
            if (options.EnableLocalWorkerProfiles)
            {
                foreach (var runtime in new[] { options.DotNet, options.TypeScript, options.Python })
                {
                    if (string.IsNullOrWhiteSpace(runtime.Reference) || string.IsNullOrWhiteSpace(runtime.RuntimeVersion))
                    {
                        throw new InvalidOperationException("Every enabled local hosted invocation runtime requires an immutable reference and exact version.");
                    }
                }
            }
            if (options.PublicationOnlyRuntimes is null || options.PublicationOnlyRuntimes.Count > 16)
            {
                throw new InvalidOperationException("AiHostedInvocation publication-only runtime count is invalid.");
            }
            if (options.PublicationOnlyRuntimes.Count > 0 && options.EnableWorkerPolling)
            {
                throw new InvalidOperationException(
                    "Publication-only runtimes require worker polling to be disabled on this host.");
            }
            foreach (var runtime in options.PublicationOnlyRuntimes)
            {
                if (runtime is null || string.IsNullOrWhiteSpace(runtime.Reference) ||
                    string.IsNullOrWhiteSpace(runtime.ExecutionLanguage) || string.IsNullOrWhiteSpace(runtime.RuntimeVersion) ||
                    string.IsNullOrWhiteSpace(runtime.RuntimeSha256) || string.IsNullOrWhiteSpace(runtime.OperatingSystem) ||
                    string.IsNullOrWhiteSpace(runtime.Architecture))
                {
                    throw new InvalidOperationException(
                        "Every publication-only runtime requires an exact reference, language, version, hash and platform.");
                }
            }
            if (options.Container.Enabled)
            {
                if (string.IsNullOrWhiteSpace(options.Container.EngineExecutablePath) ||
                    string.IsNullOrWhiteSpace(options.Container.ContainerOwnerScope) ||
                    options.Container.Runtimes is null || options.Container.Runtimes.Count is < 1 or > 16)
                {
                    throw new InvalidOperationException(
                        "Enabled hosted container execution requires an engine path, owner scope and bounded exact OCI runtime list.");
                }
                foreach (var runtime in options.Container.Runtimes)
                {
                    if (runtime is null || string.IsNullOrWhiteSpace(runtime.Reference) ||
                        string.IsNullOrWhiteSpace(runtime.ExecutionLanguage) || string.IsNullOrWhiteSpace(runtime.RuntimeVersion) ||
                        string.IsNullOrWhiteSpace(runtime.ImageRepository) || string.IsNullOrWhiteSpace(runtime.ImageDigest))
                    {
                        throw new InvalidOperationException("Every hosted container runtime requires an exact reference, language, version, repository and digest.");
                    }
                }
            }
        }
    }

    public sealed record AiHostedInvocationEnvironmentSet(
        string DotNetReference,
        string TypeScriptReference,
        string PythonReference,
        IReadOnlyList<AiPublicationEnvironment> ContainerRuntimes);
}
