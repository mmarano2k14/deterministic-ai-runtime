using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.AI.Tests.Runtime.Execution.Instance;
using StackExchange.Redis;
using System.Text.Json;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.MultiInstance
{
    /// <summary>
    /// Integration tests for multiple AI runtime instances participating in distributed DAG execution.
    /// </summary>
    [Collection("redis")]
    public sealed class MultiInstanceDagExecutionIntegrationTests
    {
        private readonly IConnectionMultiplexer _redis;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiInstanceDagExecutionIntegrationTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared Redis fixture.</param>
        public MultiInstanceDagExecutionIntegrationTests(
            RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            _redis = fixture.Connection;
        }

        /// <summary>
        /// Verifies that multiple integration runtime hosts can be created with isolated service providers,
        /// different runtime instance identities, and resolvable DAG execution engines.
        /// </summary>
        [Fact]
        public async Task MultiInstanceIntegrationHosts_Should_Resolve_Engines_With_Different_Runtime_Instance_Identities()
        {
            await using var hostA = await CreateIntegrationHostAsync("runtime-instance-a");
            await using var hostB = await CreateIntegrationHostAsync("runtime-instance-b");
            await using var hostC = await CreateIntegrationHostAsync("runtime-instance-c");

            Assert.Equal("runtime-instance-a", hostA.RuntimeInstanceIdentity.RuntimeInstanceId);
            Assert.Equal("runtime-instance-b", hostB.RuntimeInstanceIdentity.RuntimeInstanceId);
            Assert.Equal("runtime-instance-c", hostC.RuntimeInstanceIdentity.RuntimeInstanceId);

            Assert.NotSame(hostA.ServiceProvider, hostB.ServiceProvider);
            Assert.NotSame(hostA.ServiceProvider, hostC.ServiceProvider);
            Assert.NotSame(hostB.ServiceProvider, hostC.ServiceProvider);

            Assert.NotNull(hostA.Engine);
            Assert.NotNull(hostB.Engine);
            Assert.NotNull(hostC.Engine);

            Assert.NotSame(hostA.Engine, hostB.Engine);
            Assert.NotSame(hostA.Engine, hostC.Engine);
            Assert.NotSame(hostB.Engine, hostC.Engine);
        }

        /// <summary>
        /// Verifies that the runtime instance identity is stable within a single integration host lifetime.
        /// </summary>
        [Fact]
        public async Task MultiInstanceIntegrationHost_Should_Keep_Same_Runtime_Instance_Identity_For_Host_Lifetime()
        {
            await using var host = await CreateIntegrationHostAsync("runtime-instance-a");

            var first = host.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();
            var second = host.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.Same(first, second);
            Assert.Equal("runtime-instance-a", first.RuntimeInstanceId);
            Assert.Equal(first.RuntimeInstanceId, second.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that multiple runtime instances can safely execute the same DAG execution
        /// concurrently without duplicate step completion or broken convergence.
        /// </summary>
        [RedisFact]
        public async Task MultiInstanceDagExecution_Should_Converge_On_Same_Execution()
        {
            var pipelineName = $"multi-instance-dag-{Guid.NewGuid():N}";
            var filePath = WriteMultiInstancePipelineDefinitionToConfig(pipelineName);

            await using var hostA = await CreateIntegrationHostAsync(
                "runtime-instance-a",
                $"{pipelineName}.json");

            await using var hostB = await CreateIntegrationHostAsync(
                "runtime-instance-b",
                $"{pipelineName}.json");

            await using var hostC = await CreateIntegrationHostAsync(
                "runtime-instance-c",
                $"{pipelineName}.json");

            var created = await hostA.Engine.CreateAsync(
                pipelineName,
                "multi-instance-test");

            try
            {
                AiExecutionRecord? record = null;

                for (var attempt = 0; attempt < 100; attempt++)
                {
                    await Task.WhenAll(
                        ExecuteDistributedBatchCycleAsync(hostA.Engine, created.ExecutionId),
                        ExecuteDistributedBatchCycleAsync(hostB.Engine, created.ExecutionId),
                        ExecuteDistributedBatchCycleAsync(hostC.Engine, created.ExecutionId));

                    var dagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                    record = await dagStore.GetRecordAsync(created.ExecutionId);

                    if (record is not null && record.IsTerminal)
                    {
                        break;
                    }

                    await Task.Delay(25);
                }

                Assert.NotNull(record);
                Assert.Equal(AiExecutionStatus.Completed, record!.Status);
                Assert.Equal(AiExecutionMode.Dag, record.ExecutionMode);
                Assert.Equal(6, record.CompletedSteps.Count);

                Assert.Contains("step-a", record.CompletedSteps);
                Assert.Contains("step-b", record.CompletedSteps);
                Assert.Contains("step-c", record.CompletedSteps);
                Assert.Contains("step-d", record.CompletedSteps);
                Assert.Contains("step-e", record.CompletedSteps);
                Assert.Contains("step-f", record.CompletedSteps);

                var finalDagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var state = await finalDagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(6, state!.Steps.Count);

                Assert.All(
                    state.Steps.Values,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.Null(step.ClaimedBy);
                        Assert.Null(step.ClaimToken);
                        Assert.Null(step.ClaimedAtUtc);
                        Assert.Null(step.LeaseExpiresAtUtc);
                    });
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    hostA.ServiceProvider,
                    created.ExecutionId);

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Executes one distributed batch worker cycle for the target execution.
        /// </summary>
        /// <param name="engine">The DAG execution engine.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <summary>
        /// Executes one distributed DAG batch cycle for the target execution.
        /// </summary>
        /// <param name="engine">The DAG execution engine representing one runtime instance.</param>
        /// <param name="executionId">The execution identifier.</param>
        private static async Task ExecuteDistributedBatchCycleAsync(
            AiDagExecutionEngine engine,
            string executionId)
        {
            try
            {
                await engine.ExecuteBatchAsync(
                    executionId,
                    maxSteps: 3);
            }
            catch (InvalidOperationException ex)
                when (string.Equals(
                    ex.Message,
                    "Concurrency conflict on execution update.",
                    StringComparison.Ordinal))
            {
                // Expected race loser in distributed multi-instance execution.
            }
        }

        /// <summary>
        /// Verifies that provider-level throttling is enforced globally across multiple runtime instances.
        /// </summary>
        [RedisFact]
        public async Task MultiInstanceDagExecution_Should_Respect_Provider_Throttle_Across_Runtime_Instances()
        {
            var pipelineName = $"multi-instance-provider-throttle-{Guid.NewGuid():N}";
            var filePath = WriteProviderThrottlePipelineDefinitionToConfig(pipelineName);

            await using var hostA = await CreateIntegrationHostAsync(
                "runtime-instance-a",
                $"{pipelineName}.json");

            await using var hostB = await CreateIntegrationHostAsync(
                "runtime-instance-b",
                $"{pipelineName}.json");

            await using var hostC = await CreateIntegrationHostAsync(
                "runtime-instance-c",
                $"{pipelineName}.json");

            var created = await hostA.Engine.CreateAsync(
                pipelineName,
                "provider-throttle-test");

            var providerScopeKey = "ai:concurrency:scope:provider:openai";
            var database = _redis.GetDatabase();

            var maxObservedProviderConcurrency = 0L;

            try
            {
                AiExecutionRecord? record = null;

                for (var attempt = 0; attempt < 150; attempt++)
                {
                    await Task.WhenAll(
                        ExecuteDistributedBatchCycleAsync(hostA.Engine, created.ExecutionId),
                        ExecuteDistributedBatchCycleAsync(hostB.Engine, created.ExecutionId),
                        ExecuteDistributedBatchCycleAsync(hostC.Engine, created.ExecutionId));

                    var activeProviderLeases = await database.SortedSetLengthAsync(
                        providerScopeKey);

                    if (activeProviderLeases > maxObservedProviderConcurrency)
                    {
                        maxObservedProviderConcurrency = activeProviderLeases;
                    }

                    var dagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                    record = await dagStore.GetRecordAsync(created.ExecutionId);

                    if (record is not null && record.IsTerminal)
                    {
                        break;
                    }

                    await Task.Delay(25);
                }

                Assert.NotNull(record);
                Assert.Equal(AiExecutionStatus.Completed, record!.Status);
                Assert.Equal(AiExecutionMode.Dag, record.ExecutionMode);
                Assert.Equal(10, record.CompletedSteps.Count);

                Assert.True(
                    maxObservedProviderConcurrency <= 2,
                    $"Provider throttle was exceeded. MaxObserved='{maxObservedProviderConcurrency}', Limit='2'.");

                var finalDagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var state = await finalDagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(10, state!.Steps.Count);

                Assert.All(
                    state.Steps.Values,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.Null(step.ClaimedBy);
                        Assert.Null(step.ClaimToken);
                        Assert.Null(step.ClaimedAtUtc);
                        Assert.Null(step.LeaseExpiresAtUtc);
                    });
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    hostA.ServiceProvider,
                    created.ExecutionId);

                await database.KeyDeleteAsync(providerScopeKey);

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Creates one fully wired integration runtime host with in-memory pipeline options.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier.</param>
        /// <returns>The created multi-instance integration host.</returns>
        private static Task<MultiInstanceIntegrationHost> CreateIntegrationHostAsync(
            string runtimeInstanceId)
        {
            return CreateIntegrationHostAsync(
                runtimeInstanceId,
                CreateInMemoryOptions());
        }

        /// <summary>
        /// Creates one fully wired integration runtime host with a JSON pipeline definition file.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier.</param>
        /// <param name="jsonFileName">The JSON pipeline definition file name under the config directory.</param>
        /// <returns>The created multi-instance integration host.</returns>
        private static Task<MultiInstanceIntegrationHost> CreateIntegrationHostAsync(
            string runtimeInstanceId,
            string jsonFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonFileName);

            return CreateIntegrationHostAsync(
                runtimeInstanceId,
                CreateJsonOptions(jsonFileName));
        }

        /// <summary>
        /// Creates one fully wired integration runtime host with the provided AI engine options.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier.</param>
        /// <param name="options">The AI engine options.</param>
        /// <returns>The created multi-instance integration host.</returns>
        private static async Task<MultiInstanceIntegrationHost> CreateIntegrationHostAsync(
            string runtimeInstanceId,
            AiEngineOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(options);

            var host = await AiDagExecutionEngineFixture.CreateAsync(
                options,
                configureServices: services =>
                {
                    services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
                    services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                        new TestAiRuntimeInstanceIdentity(runtimeInstanceId));
                });

            return new MultiInstanceIntegrationHost(
                runtimeInstanceId,
                host);
        }

        /// <summary>
        /// Creates AI engine options using the in-memory pipeline definition source.
        /// </summary>
        /// <returns>The configured AI engine options.</returns>
        private static AiEngineOptions CreateInMemoryOptions()
        {
            return new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "InMemory"
            };
        }

        /// <summary>
        /// Creates AI engine options using a JSON pipeline definition file.
        /// </summary>
        /// <param name="jsonFileName">The JSON pipeline definition file name under the config directory.</param>
        /// <returns>The configured AI engine options.</returns>
        private static AiEngineOptions CreateJsonOptions(
            string jsonFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonFileName);

            return new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = "config/" + jsonFileName
            };
        }

        /// <summary>
        /// Writes a deterministic multi-instance DAG pipeline definition to the test config directory.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The created file path.</returns>
        private static string WriteMultiInstancePipelineDefinitionToConfig(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var definition = new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
                {
                    new()
                    {
                        Name = "step-a",
                        StepKey = "hello-world",
                        Order = 1,
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-b",
                        StepKey = "hello-world",
                        Order = 2,
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-c",
                        StepKey = "hello-world",
                        Order = 3,
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-d",
                        StepKey = "hello-world",
                        Order = 4,
                        DependsOn = new[] { "step-a", "step-b" },
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-e",
                        StepKey = "hello-world",
                        Order = 5,
                        DependsOn = new[] { "step-c" },
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-f",
                        StepKey = "hello-world",
                        Order = 6,
                        DependsOn = new[] { "step-d", "step-e" },
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    }
                }
            };

            var root = new
            {
                pipelines = new[]
                {
                    definition
                }
            };

            var json = JsonSerializer.Serialize(
                root,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            var baseDir = AppContext.BaseDirectory;
            var configDir = Path.Combine(baseDir, "config");

            Directory.CreateDirectory(configDir);

            var filePath = Path.Combine(configDir, $"{pipelineName}.json");

            File.WriteAllText(filePath, json);

            return filePath;
        }

        /// <summary>
        /// Deletes the distributed DAG execution bundle created by the test.
        /// </summary>
        /// <param name="serviceProvider">The service provider used to resolve the DAG store.</param>
        /// <param name="executionId">The execution identifier.</param>
        private static async Task CleanupDagExecutionAsync(
            IServiceProvider serviceProvider,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var dagStore = serviceProvider.GetRequiredService<IAiDagExecutionStore>();

            await dagStore.DeleteExecutionBundleAsync(
                executionId);
        }

        /// <summary>
        /// Deletes a file when it exists.
        /// </summary>
        /// <param name="filePath">The file path.</param>
        private static void TryDeleteFile(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        /// <summary>
        /// Writes a deterministic provider-throttled DAG pipeline definition to the test config directory.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The created file path.</returns>
        private static string WriteProviderThrottlePipelineDefinitionToConfig(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var steps = Enumerable
                .Range(1, 10)
                .Select(index => new AiPipelineStepDefinition
                {
                    Name = $"provider-step-{index:00}",
                    StepKey = "hello-world",
                    Order = index,
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = 100,
                        ["provider"] = "openai",
                        ["model"] = "gpt-test",
                        ["operation"] = "llm.chat",
                        ["concurrency"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["leaseSeconds"] = 30,
                            ["maxProviderConcurrency"] = 2,
                            ["defaultRetryAfterMs"] = 25
                        }
                    }
                })
                .ToList();

            var definition = new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };

            var root = new
            {
                pipelines = new[]
                {
            definition
        }
            };

            var json = JsonSerializer.Serialize(
                root,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            var baseDir = AppContext.BaseDirectory;
            var configDir = Path.Combine(baseDir, "config");

            Directory.CreateDirectory(configDir);

            var filePath = Path.Combine(configDir, $"{pipelineName}.json");

            File.WriteAllText(filePath, json);

            return filePath;
        }

        /// <summary>
        /// Represents one fully wired integration runtime host participating in multi-instance tests.
        /// </summary>
        private sealed class MultiInstanceIntegrationHost : IAsyncDisposable, IDisposable
        {
            private readonly AiDagExecutionEngineTestHost _innerHost;

            /// <summary>
            /// Initializes a new instance of the <see cref="MultiInstanceIntegrationHost"/> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The expected runtime instance identifier.</param>
            /// <param name="innerHost">The underlying DAG execution engine test host.</param>
            public MultiInstanceIntegrationHost(
                string runtimeInstanceId,
                AiDagExecutionEngineTestHost innerHost)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                ArgumentNullException.ThrowIfNull(innerHost);

                RuntimeInstanceId = runtimeInstanceId;
                _innerHost = innerHost;

                RuntimeInstanceIdentity =
                    _innerHost.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

                Engine = _innerHost.Engine;
            }

            /// <summary>
            /// Gets the expected runtime instance identifier.
            /// </summary>
            public string RuntimeInstanceId { get; }

            /// <summary>
            /// Gets the scoped service provider for this runtime host.
            /// </summary>
            public IServiceProvider ServiceProvider => _innerHost.ServiceProvider;

            /// <summary>
            /// Gets the resolved runtime instance identity.
            /// </summary>
            public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

            /// <summary>
            /// Gets the DAG execution engine.
            /// </summary>
            public AiDagExecutionEngine Engine { get; }

            /// <summary>
            /// Disposes the underlying runtime host.
            /// </summary>
            public void Dispose()
            {
                _innerHost.Dispose();
            }

            /// <summary>
            /// Asynchronously disposes the underlying runtime host.
            /// </summary>
            public async ValueTask DisposeAsync()
            {
                await _innerHost.DisposeAsync();
            }
        }
    }
}