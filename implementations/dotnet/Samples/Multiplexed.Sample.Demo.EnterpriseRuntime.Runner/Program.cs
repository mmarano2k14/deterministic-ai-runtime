using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.DI.Persistence.Mongo;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.Realtime.Events.Abstractions;
using Multiplexed.Realtime.Handlers;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Configuration;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Control;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Persistence;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Progress;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Retention;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Retry;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Throttling;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Infrastructure;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Interactive;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Options;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Realtime;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Realtime.Formatting;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Scenarios;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Scenarios.Chaos;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Scenarios.DI;
using Multiplexed.Sample.External.Plugins.Steps;

namespace Multiplexed.Sample.Demo.EnterpriseRuntime.Runner
{
    /// <summary>
    /// Console runner for the enterprise runtime demo.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Runs the enterprise runtime demo.
        /// </summary>
        /// <param name="args">
        /// The command-line arguments.
        /// </param>
        /// <returns>
        /// The process exit code.
        /// </returns>
        public static async Task<int> Main(
            string[] args)
        {
            try
            {
                var settings = EnterpriseRuntimeDemoSettingsLoader.Load();

                var options = EnterpriseRuntimeDemoOptionsParser.Parse(
                    args);

                EnterpriseRuntimeExecutionReporter.PrintHeader();

                if (args.Length == 0)
                {
                    EnterpriseRuntimeConsoleIntro.Print();

                    EnterpriseRuntimeInteractiveConsoleSelector.Apply(
                        options,
                        new[]
                        {
                            EnterpriseRuntimeScenarioNames.Json,
                            EnterpriseRuntimeScenarioNames.Chaos100,
                            EnterpriseRuntimeScenarioNames.Chaos500,
                            EnterpriseRuntimeScenarioNames.Throttling100
                        });

                    Console.WriteLine();
                }

                var infrastructureGuard = new EnterpriseRuntimeInfrastructureGuard();

                await infrastructureGuard.EnsureAsync(
                        options.StartInfrastructure)
                    .ConfigureAwait(false);

                var runtimeOptions = EnterpriseRuntimeOptionsFactory.Create(
                    options.Scenario);

                await using var host = EnterpriseRuntimeDemoHost.Create(
                    runtimeOptions,
                    settings,
                    services => ConfigureDemoServices(
                        services,
                        options,
                        settings));

                var scenarioRunner = new EnterpriseRuntimeScenarioRunner(
                    host.ServiceProvider.GetServices<IEnterpriseRuntimeScenario>());

                var scenarioContext = new EnterpriseRuntimeScenarioContext
                {
                    Services = host.ServiceProvider,
                    Configuration = new ConfigurationManager(),
                    LoggerFactory = host.ServiceProvider.GetRequiredService<ILoggerFactory>()
                };

                Console.WriteLine("Available scenarios:");

                foreach (var scenarioName in scenarioRunner.GetScenarioNames())
                {
                    Console.WriteLine($"  - {scenarioName}");
                }

                Console.WriteLine();

                if (options.Verbose)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Verbose runtime output enabled.");

                    if (options.VerboseRaw)
                    {
                        Console.WriteLine("Verbose raw realtime output enabled.");
                    }

                    if (options.VerboseNoise)
                    {
                        Console.WriteLine("Verbose noisy realtime output enabled.");
                    }

                    Console.ResetColor();
                    Console.WriteLine();
                }

                var result = await scenarioRunner.RunScenarioAsync(
                        options.Scenario,
                        scenarioContext)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    EnterpriseRuntimeExecutionReporter.WriteError(
                        result.Message ?? "Scenario execution failed.");

                    return 1;
                }

                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine();

                EnterpriseRuntimeExecutionReporter.WriteError(
                    "Enterprise Runtime Demo failed.");

                EnterpriseRuntimeExecutionReporter.WriteError(
                    exception.Message);

                return 1;
            }
        }

        /// <summary>
        /// Configures enterprise runtime demo services.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="options">
        /// The demo options.
        /// </param>
        /// <param name="settings">
        /// The demo settings.
        /// </param>
        private static void ConfigureDemoServices(
            IServiceCollection services,
            EnterpriseRuntimeDemoOptions options,
            EnterpriseRuntimeDemoSettings settings)
        {
            ArgumentNullException.ThrowIfNull(
                services);

            ArgumentNullException.ThrowIfNull(
                options);

            ArgumentNullException.ThrowIfNull(
                settings);

            services.AddSingleton(
                settings);

            services.AddSingleton(
                new EnterpriseRuntimeVerboseConsoleOptions
                {
                    Enabled = options.Verbose,
                    Raw = options.VerboseRaw,
                    Noise = options.VerboseNoise
                });

            services.AddSingleton<EnterpriseRuntimeExecutionReporter>();

            services.AddSingleton<EnterpriseRuntimeExecutionPersistenceLoader>();

            services.AddSingleton<EnterpriseRuntimeRetryAnalyzer>();

            services.AddSingleton<EnterpriseRuntimeRetentionAnalyzer>();

            services.AddSingleton<EnterpriseRuntimeThrottlingAnalyzer>();

            services.AddSingleton<EnterpriseRuntimeExecutionProgressMonitor>();

            services.AddSingleton<EnterpriseRuntimeExecutionRunner>();

            services.AddSingleton<EnterpriseRuntimeRuntimeLogEventClassifier>();

            services.AddSingleton<EnterpriseRuntimeRuntimeLogEventFormatter>();

            services.AddSingleton<EnterpriseRuntimeExecutionControlCommandExecutor>();

            services.AddSingleton<EnterpriseRuntimeExecutionControlState>();

            services.AddSingleton<EnterpriseRuntimeExecutionControlHotkeyListener>();

            if (options.Verbose)
            {
                services.AddScoped(
                    typeof(IRuntimeEventHandler<>),
                    typeof(EnterpriseRuntimeConsoleRuntimeEventHandler<>));
            }

            var finalizedHook = new DistributedChaosRunFinalizedHook();

            services.AddSingleton(
                finalizedHook);

            services.AddMongoAiExecutionSnapshots<ExecutionContextSnapshot>(
                snapshotOptions =>
                {
                    snapshotOptions.ConnectionString = settings.Mongo.ConnectionString;
                    snapshotOptions.DatabaseName = settings.Mongo.DatabaseName;
                    snapshotOptions.CollectionName =
                        $"execution_snapshots_demo_{Guid.NewGuid():N}";
                });

            services.AddAiExecutionReplay();

            services.AddAiControlPlaneDiscoveryCore();
             

            services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                finalizedHook);

            services.AddEnterpriseRuntimeScenariosFromAssemblies(
                typeof(EnterpriseRuntimeScenarioAssemblyMarker).Assembly);

            services.AddAiStepsFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(DemoStepsAssemblyMarker).Assembly);
        }
    }
}