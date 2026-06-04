using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Context;
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
    /// Integration tests for multi-runtime resilience scenarios involving retry,
    /// retention, throttling, and deterministic convergence.
    /// </summary>
    [Collection("redis")]
    public sealed class MultiInstanceResilienceIntegrationTests
    {
        private readonly IConnectionMultiplexer _redis;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiInstanceResilienceIntegrationTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared Redis fixture.</param>
        public MultiInstanceResilienceIntegrationTests(
            RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            _redis = fixture.Connection;
        }

        /// <summary>
        /// Verifies that multiple runtime instances can safely converge the same DAG execution
        /// while retry and provider-level throttling are active.
        /// </summary>
        [RedisFact]
        public async Task MultiInstanceDagExecution_Should_Converge_With_Retry_And_Provider_Throttle()
        {
            var pipelineName = $"multi-instance-resilience-{Guid.NewGuid():N}";
            var filePath = WriteResiliencePipelineDefinitionToConfig(pipelineName);

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
                "multi-instance-resilience-test");

            var database = _redis.GetDatabase();
            var providerScopeKey = "ai:concurrency:scope:provider:openai";
            var flakyAttemptKeyPrefix = $"ai:test:multi-instance:flaky:{created.ExecutionId}:";

            var maxObservedProviderConcurrency = 0L;

            try
            {
                AiExecutionRecord? record = null;

                for (var attempt = 0; attempt < 200; attempt++)
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
                Assert.Equal(8, record.CompletedSteps.Count);

                Assert.True(
                    maxObservedProviderConcurrency <= 2,
                    $"Provider throttle was exceeded. MaxObserved='{maxObservedProviderConcurrency}', Limit='2'.");

                var finalDagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var state = await finalDagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(8, state!.Steps.Count);

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

                var flakyStep = state.Steps["flaky-provider-step"];

                Assert.NotNull(flakyStep.RetryState);
                Assert.True(
                    flakyStep.RetryState!.RetryCount >= 1,
                    "The flaky provider step should have retried at least once.");

                var flakyAttemptCount = await database.StringGetAsync(
                    flakyAttemptKeyPrefix + "flaky-provider-step");

                Assert.True(flakyAttemptCount.HasValue);
                Assert.True((int)flakyAttemptCount >= 2);
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    hostA.ServiceProvider,
                    created.ExecutionId);

                await database.KeyDeleteAsync(providerScopeKey);
                await DeleteKeysByPatternAsync(flakyAttemptKeyPrefix + "*");

                TryDeleteFile(filePath);
            }
        }

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
                    maxSteps: 4);
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
        /// Verifies that multiple runtime instances can safely converge the same DAG execution
        /// while retry, aggressive retention, and provider-level throttling are active together.
        /// </summary>
        [RedisFact]
        public async Task MultiInstanceDagExecution_Should_Converge_With_Retry_Retention_And_Provider_Throttle()
        {
            var pipelineName = $"multi-instance-retention-resilience-{Guid.NewGuid():N}";
            var filePath = WriteRetentionResiliencePipelineDefinitionToConfig(pipelineName);

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
                "multi-instance-retention-resilience-test");

            var database = _redis.GetDatabase();
            var providerScopeKey = "ai:concurrency:scope:provider:openai";
            var flakyAttemptKeyPrefix = $"ai:test:multi-instance:flaky:{created.ExecutionId}:";

            var maxObservedProviderConcurrency = 0L;

            try
            {
                AiExecutionRecord? record = null;

                for (var attempt = 0; attempt < 250; attempt++)
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
                Assert.Equal(8, record.CompletedSteps.Count);

                Assert.True(
                    maxObservedProviderConcurrency <= 2,
                    $"Provider throttle was exceeded. MaxObserved='{maxObservedProviderConcurrency}', Limit='2'.");

                var finalDagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var state = await finalDagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);

                var flakyAttemptCount = await database.StringGetAsync(
                    flakyAttemptKeyPrefix + "flaky-provider-step");

                Assert.True(flakyAttemptCount.HasValue);
                Assert.True((int)flakyAttemptCount >= 2);

                var resolver = hostA.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

                await resolver.WarmAsync(
                    created.ExecutionId,
                    state!,
                    cancellationToken: default);

                var resolvedFlakyStepStatus = await resolver.GetStepStatusAsync(
                    created.ExecutionId,
                    "flaky-provider-step",
                    state!,
                    cancellationToken: default);

                Assert.NotNull(resolvedFlakyStepStatus);
                Assert.Equal(AiStepExecutionStatus.Completed, resolvedFlakyStepStatus!.Status);

                var resolvedFlakyStep = await resolver.GetStepAsync(
                    created.ExecutionId,
                    "flaky-provider-step",
                    state!,
                    cancellationToken: default);

                Assert.NotNull(resolvedFlakyStep);
                Assert.Equal(AiStepExecutionStatus.Completed, resolvedFlakyStep!.Status);

                var resolvedFinalStep = await resolver.GetStepAsync(
                    created.ExecutionId,
                    "provider-step-08",
                    state!,
                    cancellationToken: default);

                Assert.NotNull(resolvedFinalStep);
                Assert.Equal(AiStepExecutionStatus.Completed, resolvedFinalStep!.Status);

                var terminalStepsInHotState = state!.Steps.Values
                    .Count(step => step.Status == AiStepExecutionStatus.Completed);

                Assert.True(
                    terminalStepsInHotState <= 8,
                    "Retention-enabled execution should remain valid even when terminal steps are compacted or evicted.");

                Assert.All(
                    state.Steps.Values,
                    step =>
                    {
                        Assert.True(
                            step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.None,
                            $"Unexpected hot step status. Step='{step.StepName}', Status='{step.Status}'.");

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
                await DeleteKeysByPatternAsync(flakyAttemptKeyPrefix + "*");

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Creates one fully wired integration runtime host with a JSON pipeline definition file.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier.</param>
        /// <param name="jsonFileName">The JSON pipeline definition file name under the config directory.</param>
        /// <returns>The created multi-instance integration host.</returns>
        private async Task<MultiInstanceIntegrationHost> CreateIntegrationHostAsync(
            string runtimeInstanceId,
            string jsonFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonFileName);

            var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateJsonOptions(jsonFileName),
                configureServices: services =>
                {
                    services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
                    services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                        new TestAiRuntimeInstanceIdentity(runtimeInstanceId));

                    services.TryAddSingleton<IAiStep, MultiInstanceFlakyProviderStep>();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(MultiInstanceResilienceIntegrationTests).Assembly);

                });

            return new MultiInstanceIntegrationHost(
                runtimeInstanceId,
                host);
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

                // Retention will be enabled here after we align with the existing retention fixture/options.
                // Example target:
                // Retention = aggressive compact/evict settings
                // PayloadStore = Redis/Mongo cached payload settings
            };
        }

        /// <summary>
        /// Writes a deterministic DAG pipeline definition containing provider-throttled
        /// steps and one flaky retry-enabled provider step.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The created file path.</returns>
        private static string WriteResiliencePipelineDefinitionToConfig(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var providerConcurrencyConfig = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["leaseSeconds"] = 30,
                ["maxProviderConcurrency"] = 2,
                ["defaultRetryAfterMs"] = 25
            };

            var providerConfig = new Dictionary<string, object?>
            {
                ["delayMs"] = 100,
                ["provider"] = "openai",
                ["model"] = "gpt-test",
                ["operation"] = "llm.chat",
                ["concurrency"] = providerConcurrencyConfig
            };

            var flakyProviderConfig = new Dictionary<string, object?>
            {
                ["delayMs"] = 100,
                ["provider"] = "openai",
                ["model"] = "gpt-test",
                ["operation"] = "llm.chat",
                ["failOnce"] = true,
                ["concurrency"] = providerConcurrencyConfig
            };

            var definition = new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
                {
                    new()
                    {
                        Name = "provider-step-01",
                        StepKey = "hello-world",
                        Order = 1,
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "provider-step-02",
                        StepKey = "hello-world",
                        Order = 2,
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "provider-step-03",
                        StepKey = "hello-world",
                        Order = 3,
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "flaky-provider-step",
                        StepKey = "multi-instance-flaky-provider",
                        Order = 4,
                        Config = flakyProviderConfig,
                        Execution = new AiPipelineStepExecutionDefinition
                        {
                            MaxRetries = 2,
                            RetryDelayMs = 25
                        }
                    },
                    new()
                    {
                        Name = "provider-step-05",
                        StepKey = "hello-world",
                        Order = 5,
                        DependsOn = new[] { "provider-step-01", "provider-step-02" },
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "provider-step-06",
                        StepKey = "hello-world",
                        Order = 6,
                        DependsOn = new[] { "provider-step-03", "flaky-provider-step" },
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "provider-step-07",
                        StepKey = "hello-world",
                        Order = 7,
                        DependsOn = new[] { "provider-step-05" },
                        Config = providerConfig
                    },
                    new()
                    {
                        Name = "provider-step-08",
                        StepKey = "hello-world",
                        Order = 8,
                        DependsOn = new[] { "provider-step-06", "provider-step-07" },
                        Config = providerConfig
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
        /// Writes a deterministic DAG pipeline definition containing provider-throttled steps,
        /// one flaky retry-enabled provider step, and aggressive retention configuration.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The created file path.</returns>
        private static string WriteRetentionResiliencePipelineDefinitionToConfig(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var providerConcurrencyConfig = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["leaseSeconds"] = 30,
                ["maxProviderConcurrency"] = 2,
                ["defaultRetryAfterMs"] = 25
            };

            var retentionConfig = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["archiveReason"] = "multi-instance-retention-resilience-test",
                ["policies"] = new[]
                {
            new Dictionary<string, object?>
            {
                ["name"] = "retention.hybrid.terminal"
            }
        },
                ["trigger"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxStepsInState"] = 1,
                    ["maxCompletedStepsInState"] = 0,
                    ["maxInlinePayloadBytes"] = 1
                }
            };

            var providerConfig = new Dictionary<string, object?>
            {
                ["delayMs"] = 100,
                ["provider"] = "openai",
                ["model"] = "gpt-test",
                ["operation"] = "llm.chat",
                ["concurrency"] = providerConcurrencyConfig,
                ["retention"] = retentionConfig
            };

            var flakyProviderConfig = new Dictionary<string, object?>
            {
                ["delayMs"] = 100,
                ["provider"] = "openai",
                ["model"] = "gpt-test",
                ["operation"] = "llm.chat",
                ["failOnce"] = true,
                ["concurrency"] = providerConcurrencyConfig,
                ["retention"] = retentionConfig,
            };

            var definition = new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["retention"] = retentionConfig
                },
                Steps = new List<AiPipelineStepDefinition>
        {
            new()
            {
                Name = "provider-step-01",
                StepKey = "hello-world",
                Order = 1,
                Config = providerConfig
            },
            new()
            {
                Name = "provider-step-02",
                StepKey = "hello-world",
                Order = 2,
                Config = providerConfig
            },
            new()
            {
                Name = "provider-step-03",
                StepKey = "hello-world",
                Order = 3,
                Config = providerConfig
            },
            new()
            {
                Name = "flaky-provider-step",
                StepKey = "multi-instance-flaky-provider",
                Order = 4,
                Config = flakyProviderConfig,
                Execution = new AiPipelineStepExecutionDefinition
                {
                    MaxRetries = 2,
                    RetryDelayMs = 25
                }
            },
            new()
            {
                Name = "provider-step-05",
                StepKey = "hello-world",
                Order = 5,
                DependsOn = new[] { "provider-step-01", "provider-step-02" },
                Config = providerConfig
            },
            new()
            {
                Name = "provider-step-06",
                StepKey = "hello-world",
                Order = 6,
                DependsOn = new[] { "provider-step-03", "flaky-provider-step" },
                Config = providerConfig
            },
            new()
            {
                Name = "provider-step-07",
                StepKey = "hello-world",
                Order = 7,
                DependsOn = new[] { "provider-step-05" },
                Config = providerConfig
            },
            new()
            {
                Name = "provider-step-08",
                StepKey = "hello-world",
                Order = 8,
                DependsOn = new[] { "provider-step-06", "provider-step-07" },
                Config = providerConfig
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
        /// Deletes Redis keys matching the provided pattern.
        /// </summary>
        /// <param name="pattern">The Redis key pattern.</param>
        private async Task DeleteKeysByPatternAsync(
            string pattern)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

            var database = _redis.GetDatabase();

            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var keys = server.Keys(
                    database: database.Database,
                    pattern: pattern);

                foreach (var key in keys)
                {
                    await database.KeyDeleteAsync(key);
                }

                return;
            }
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

        /// <summary>
        /// Test step that fails once per execution and step name using Redis as shared state,
        /// then succeeds on subsequent attempts.
        /// </summary>
        [AiStep("multi-instance-flaky-provider")]
        private sealed class MultiInstanceFlakyProviderStep : IAiStep
        {
            private readonly IConnectionMultiplexer _redis;

            /// <summary>
            /// Initializes a new instance of the <see cref="MultiInstanceFlakyProviderStep"/> class.
            /// </summary>
            /// <param name="redis">The shared Redis connection.</param>
            public MultiInstanceFlakyProviderStep(
                IConnectionMultiplexer redis)
            {
                _redis = redis ?? throw new ArgumentNullException(nameof(redis));
            }

            /// <summary>
            /// Gets the step implementation name.
            /// </summary>
            public string Name => "multi-instance-flaky-provider";

            /// <inheritdoc />
            public async Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(context);

                var helper = context.GetHelper();

                var delayMs = await helper.GetConfigAsync<int?>(
                    "delayMs",
                    cancellationToken).ConfigureAwait(false) ?? 0;

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }

                var executionId = context.ExecutionId;
                var stepName = context.Step.Name;

                var key = $"ai:test:multi-instance:flaky:{executionId}:{stepName}";
                var database = _redis.GetDatabase();

                var attempt = await database.StringIncrementAsync(key);

                if (attempt == 1)
                {
                    throw new InvalidOperationException(
                        $"Intentional first-attempt failure for step '{stepName}'.");
                }

                return AiStepResult.Ok(
                    output: $"Recovered after retry: {stepName}",
                    data: helper.ToDictionary(new
                    {
                        step = stepName,
                        attempt,
                        recovered = true,
                        executedAtUtc = DateTime.UtcNow
                    }));
            }
        }
    }
}