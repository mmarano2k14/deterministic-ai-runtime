using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Durable.DI;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.TypeScript;
using Multiplexed.AI.Runtime.Pipeline;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    /// <summary>
    /// Exercises the production host registration and pipeline resolver without infrastructure.
    /// Local launch files are inert registration fixtures; no worker executable or hosted loop is started.
    /// </summary>
    public sealed class HostedInvocationHostRegistrationTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Runtime_Host_Resolves_Custom_Dag_Without_Dag_Reconciliation(string language)
        {
            using var files = new WorkerRegistrationFiles();
            var services = Configure(reconciliation: false, polling: true, files: files);
            AssertFactories(services);
            AssertHostedRoles(services, reconciliation: false, polling: true);

            using var provider = services.BuildServiceProvider();
            var native = new RejectingNativeRegistry();
            var resolver = new AiPipelineResolver(native,
                provider.GetServices<IAiStepInvocationAdapterFactory>());
            var plan = await resolver.ResolveAsync(Pipeline(language));

            var step = Assert.Single(plan.Steps);
            Assert.IsType<AiDurableInvocationStepAdapter>(step.Step);
            Assert.Equal(language, step.InvocationBinding!.ExecutionLanguage);
            Assert.Equal("publication/work/v1", step.InvocationBinding.ImplementationRef);
            Assert.Equal(0, native.ResolveCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reconciliation_Remains_Explicit_And_Registers_One_Adapter_Per_Language(bool polling)
        {
            using var files = new WorkerRegistrationFiles();
            // false: publication-only control plane; true: combined process-host configuration.
            var services = Configure(reconciliation: true, polling: polling,
                files: polling ? files : null);
            AssertFactories(services);
            AssertHostedRoles(services, reconciliation: true, polling: polling);
            var options = Assert.IsType<AiDurableInvocationDagReconciliationOptions>(
                Assert.Single(services.Where(item => item.ServiceType ==
                    typeof(AiDurableInvocationDagReconciliationOptions))).ImplementationInstance);
            var scope = Assert.Single(options.Scopes);
            Assert.Equal("matrix-tenant", scope.TenantId);
            Assert.Equal("matrix-group", scope.TenantGroupId);
            Assert.Equal("matrix-control-plane", scope.ControlPlaneId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Preinstalled_Core_Adapters_Are_Not_Duplicated(bool reconciliation)
        {
            var services = new ServiceCollection();
            services.AddAiDurableInvocationDag();
            Configure(reconciliation, polling: false, existing: services);
            AssertFactories(services);
            AssertHostedRoles(services, reconciliation, polling: false);
        }

        [Fact]
        public void Disabled_Hosted_Invocation_Does_Not_Install_Adapters_Or_Loops()
        {
            var services = Configure(reconciliation: true, polling: true, enabled: false);
            Assert.Empty(services);
        }

        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Disabled_Hosted_Invocation_Still_Rejects_Custom_Without_Native_Fallback(string language)
        {
            var services = Configure(reconciliation: false, polling: false, enabled: false);
            using var provider = services.BuildServiceProvider();
            var native = new RejectingNativeRegistry();
            var resolver = new AiPipelineResolver(native,
                provider.GetServices<IAiStepInvocationAdapterFactory>());
            var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
                resolver.ResolveAsync(Pipeline(language)));
            Assert.Contains($"Custom/{language}", error.Message);
            Assert.Equal(0, native.ResolveCount);
        }

        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Adapter_Registration_Does_Not_Enable_Sequential_Custom_Execution(string language)
        {
            var services = Configure(reconciliation: false, polling: false);
            using var provider = services.BuildServiceProvider();
            var native = new RejectingNativeRegistry();
            var resolver = new AiPipelineResolver(native,
                provider.GetServices<IAiStepInvocationAdapterFactory>());
            var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
                resolver.ResolveAsync(Pipeline(language, AiExecutionMode.Sequential)));
            Assert.Contains("explicit DAG", error.Message);
            Assert.Equal(0, native.ResolveCount);
        }

        private static IServiceCollection Configure(bool reconciliation, bool polling,
            WorkerRegistrationFiles? files = null, bool enabled = true,
            IServiceCollection? existing = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["AiHostedInvocation:Enabled"] = enabled.ToString(),
                ["AiHostedInvocation:EnableDagReconciliation"] = reconciliation.ToString(),
                ["AiHostedInvocation:EnableWorkerPolling"] = polling.ToString(),
                ["AiHostedInvocation:EnableLocalWorkerProfiles"] = (files is not null).ToString(),
                ["AiHostedInvocation:TenantId"] = "matrix-tenant",
                ["AiHostedInvocation:TenantGroupId"] = "matrix-group",
                ["AiHostedInvocation:ControlPlaneId"] = "matrix-control-plane"
            };
            if (files is not null)
            {
                files.AddSettings(settings);
            }
            else if (!polling)
            {
                settings["AiHostedInvocation:PublicationOnlyRuntimes:0:Reference"] = "matrix-python";
                settings["AiHostedInvocation:PublicationOnlyRuntimes:0:ExecutionLanguage"] = "python";
                settings["AiHostedInvocation:PublicationOnlyRuntimes:0:RuntimeVersion"] = "3.12.0";
                settings["AiHostedInvocation:PublicationOnlyRuntimes:0:RuntimeSha256"] = new string('a', 64);
            }
            var services = existing ?? new ServiceCollection();
            HostedInvocationHostRegistration.Configure(services,
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            return services;
        }

        private static void AssertFactories(IServiceCollection services)
        {
            using var provider = services.BuildServiceProvider();
            var factories = provider.GetServices<IAiStepInvocationAdapterFactory>().ToArray();
            Assert.Equal(3, factories.Length);
            foreach (var language in new[] { "python", "typescript", "dotnet" })
            {
                var factory = Assert.Single(factories.Where(item =>
                    item.Kind == AiInvocationKind.Custom && item.ExecutionLanguage == language));
                Assert.IsType<AiDurableInvocationStepAdapterFactory>(factory);
            }
        }

        private static void AssertHostedRoles(IServiceCollection services, bool reconciliation, bool polling)
        {
            // Inspect registrations only. Resolving IHostedService would require infrastructure dependencies.
            var hosted = services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray();
            Assert.Equal(reconciliation ? 1 : 0, hosted.Count(item => item.ImplementationType ==
                typeof(AiDurableInvocationDagReconcilerHostedService)));
            Assert.Equal(polling ? 1 : 0, hosted.Count(item => item.ImplementationType ==
                typeof(AiWorkerDispatchHostedService)));
            Assert.Equal(reconciliation ? 1 : 0, services.Count(item => item.ServiceType ==
                typeof(AiDurableInvocationDagReconciliationOptions)));
        }

        private static AiPipelineDefinition Pipeline(string language,
            AiExecutionMode mode = AiExecutionMode.Dag) => new()
        {
            Name = "matrix-hosted-adapter-regression",
            Version = "v1",
            ExecutionMode = mode,
            ExecutionLanguage = language,
            Steps = new[]
            {
                new AiPipelineStepDefinition
                {
                    Name = "work",
                    StepKey = "work",
                    Invocation = new AiInvocationDefinition
                    {
                        Kind = AiInvocationKind.Custom,
                        ImplementationRef = "publication/work/v1"
                    }
                }
            }
        };

        private sealed class RejectingNativeRegistry : IAiStepRegistry
        {
            public int ResolveCount { get; private set; }
            public IAiStep Resolve(string stepKey)
            {
                ResolveCount++;
                throw new InvalidOperationException("Custom steps must not resolve through the native registry.");
            }
        }

        private sealed class WorkerRegistrationFiles : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(),
                "hosted-adapter-registration-" + Guid.NewGuid().ToString("N"));

            public void AddSettings(IDictionary<string, string?> settings)
            {
                AddRuntime("DotNet", "dotnet", "10.0.0", "worker.dll");
                AddRuntime("TypeScript", "typescript", "26.5.0", "worker.mjs");
                AddRuntime("Python", "python", "3.12.0", "worker.py");

                void AddRuntime(string section, string language, string version, string workerName)
                {
                    var directory = Path.Combine(_root, language);
                    Directory.CreateDirectory(directory);
                    var prefix = $"AiHostedInvocation:{section}:";
                    settings[prefix + "Reference"] = "matrix-" + language;
                    settings[prefix + "RuntimeVersion"] = version;
                    settings[prefix + "ExecutablePath"] = Write(directory, "runtime-host", "inert host fixture");
                    settings[prefix + "WorkerPath"] = Write(directory, workerName, "inert worker fixture");
                    settings[prefix + "WorkingDirectory"] = directory;
                    if (language == "dotnet")
                    {
                        settings[prefix + "WorkerDepsPath"] = Write(directory, "worker.deps.json", "{}");
                        settings[prefix + "WorkerRuntimeConfigPath"] = Write(directory, "worker.runtimeconfig.json", "{}");
                    }
                    if (language == "typescript")
                    {
                        // Existence/launch-root registration only; transport hash verification is not exercised.
                        var vendor = Path.Combine(directory, "vendor");
                        Directory.CreateDirectory(vendor);
                        Write(vendor, AiTypeScriptWorkerProcessProfile.CompilerFileName, "inert compiler fixture");
                    }
                }
            }

            private static string Write(string directory, string name, string content)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, content);
                return path;
            }

            public void Dispose()
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
        }
    }
}
