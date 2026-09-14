using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Policies
{
    /// <summary>
    /// Validates backward-compatible policy configuration support
    /// across retry, retention, and concurrency engines.
    /// </summary>
    public sealed class AiPolicyEngineCompatibilityIntegrationTests
    {
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName = "multiplexed_ai_tests";

        [Fact]
        public async Task ExecuteBatchAsync_Should_Support_Legacy_And_Structured_Policies()
        {
            var pipelineName = $"policy-compat-{Guid.NewGuid():N}";

            var jsonPath = await CreatePipelineJsonAsync(pipelineName);

            try
            {
                await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                    CreateOptions(jsonPath),
                    mongoConnectionString: ConnectionString,
                    mongoDatabaseName: DatabaseName);

                var created = await host.Engine.CreateAsync(
                    pipelineName,
                    "hello");

                AiExecutionRecord record;

                do
                {
                    record = await host.Engine.ExecuteBatchAsync(
                        created.ExecutionId,
                        maxSteps: 5);
                }
                while (!record.IsTerminal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    record.Status);

                var dagStore =
                    host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                var finalState = await dagStore.GetStateAsync(
                    created.ExecutionId,
                    CancellationToken.None);

                Assert.NotNull(finalState);

                Assert.All(
                    finalState!.Steps.Values,
                    step => Assert.True(step.IsCompleted));
            }
            finally
            {
                DeletePipelineJson(jsonPath);
            }
        }

        /// <summary>
        /// Creates runtime options for integration testing.
        /// </summary>
        private static AiEngineOptions CreateOptions(
            string jsonPipelineDefinitionFilePath)
        {
            return new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = jsonPipelineDefinitionFilePath,

                Snapshots = new AiExecutionSnapshotOptions
                {
                    Enabled = false
                },

                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = false,
                    AutoCleanupOnFailed = false,
                    SuppressCleanupExceptions = true
                }
            };
        }

        /// <summary>
        /// Creates a mixed policy compatibility pipeline.
        /// </summary>
        private static async Task<string> CreatePipelineJsonAsync(
            string pipelineName)
        {
            var fileName = $"{pipelineName}.json";

            var relativePath = Path.Combine(
                "config",
                fileName);

            var fullPath = Path.Combine(
                AppContext.BaseDirectory,
                relativePath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(fullPath)!);

            var json =
                $$"""
                {
                  "pipelines": [
                    {
                      "name": "{{pipelineName}}",
                      "version": "1",
                      "executionMode": "Dag",

                      "config": {
                        "concurrency": {
                          "enabled": true,
                          "maxDegreeOfParallelism": 4,
                          "policies": [
                            {
                              "name": "concurrency.throttle",
                              "type": "scope",
                              "config": {
                                "scope": "provider",
                                "target": "openai",
                                "limit": 5
                              }
                            }
                          ]
                        },

                        "retention": {
                          "enabled": true,
                          "policies": [
                            {
                              "name": "retention.compact.terminal",
                              "type": "retention"
                            }
                          ],
                          "trigger": {
                            "enabled": false
                          }
                        }
                      },

                      "steps": [
                        {
                          "name": "step-01",
                          "stepKey": "hello-world",
                          "order": 1,

                          "config": {
                            "retry": {
                              "policies": [
                                {
                                  "name": "retry.timeout.default",
                                  "type": "retry",
                                  "config": {
                                    "code": "timeout"
                                  }
                                }
                              ],
                              "maxRetries": 3,
                              "strategy": "Fixed",
                              "baseDelayMs": 10
                            }
                          }
                        },

                        {
                          "name": "step-02",
                          "stepKey": "hello-world",
                          "order": 2,
                          "dependsOn": [ "step-01" ]
                        }
                      ]
                    }
                  ]
                }
                """;

            await File.WriteAllTextAsync(
                fullPath,
                json);

            return relativePath;
        }

        /// <summary>
        /// Deletes the generated test pipeline file.
        /// </summary>
        private static void DeletePipelineJson(
            string jsonPipelineDefinitionFilePath)
        {
            var fullPath = Path.Combine(
                AppContext.BaseDirectory,
                jsonPipelineDefinitionFilePath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }
}