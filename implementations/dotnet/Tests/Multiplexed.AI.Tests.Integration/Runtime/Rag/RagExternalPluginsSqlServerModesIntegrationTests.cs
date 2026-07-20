using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Rag.Models;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Rag.Abstractions.Models;
using Multiplexed.AI.Runtime.AI.Rag.DI;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.Sample.External.Plugins.Rag;
using Multiplexed.Sample.External.Plugins.Rag.DI;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Rag
{
    /// <summary>
    /// FINAL V3 VALIDATION
    ///
    /// Covers:
    /// - InMemory
    /// - EF direct (internal)
    /// - EF provider
    ///
    /// Pipeline:
    /// candidate + job → merge → compose → snapshot → cleanup
    /// </summary>
    public sealed class RagEndToEndFinalIntegrationTests
    {
        private const string SqlServerConnectionString =
            "Server=MSI\\SQLEXPRESS;Database=TestAiRuntimeRag;Trusted_Connection=True;TrustServerCertificate=True;";

        private const string PostgresConnectionString =
            "Host=localhost;Port=5432;Database=TestAiRuntimeRag;Username=postgres;Password=sa";

        // ============================================================
        // IN MEMORY
        // ============================================================

        [Fact]
        public async Task ExecuteAllAsync_InMemory_Should_Succeed()
        {
            await using var host = await CreateHostAsync(services =>
            {
                services.AddExternalSqlServerInMemory();
                services.AddExternalPostgresInMemory();
                services.AddExternalRag();
            });

            await ExecuteAndValidate(host, "direct");
        }

        // ============================================================
        // EF DIRECT (internal execution)
        // ============================================================

        [Fact(Skip = "Please setup rg db")]
        public async Task ExecuteAllAsync_Ef_Direct_Should_Succeed()
        {
            await using var host = await CreateHostAsync(services =>
            {
                services.AddExternalSqlServerEf(SqlServerConnectionString);
                services.AddExternalPostgresEf(PostgresConnectionString);
                services.AddExternalRag();
            });

            await ExecuteAndValidate(host, "direct");
        }

        // ============================================================
        // EF PROVIDER
        // ============================================================

        [Fact(Skip = "Please setup rg db")]
        public async Task ExecuteAllAsync_Ef_Provider_Should_Succeed()
        {
            await using var host = await CreateHostAsync(services =>
            {
                services.AddExternalSqlServerEf(SqlServerConnectionString);
                services.AddExternalPostgresEf(PostgresConnectionString);
                services.AddExternalRag();
            });

            await ExecuteAndValidate(host, "provider");
        }

        // ============================================================
        // EXECUTION CORE
        // ============================================================

        private static async Task ExecuteAndValidate(
            AiDagExecutionEngineTestHost host,
            string executionMode)
        {
            var engine = host.Engine;

            var created = await engine.CreateAsync(
                "rag-final-test",
                new Dictionary<string, object?>
                {
                    ["candidateId"] = "cand-001",
                    ["jobId"] = "job-001",
                    ["candidateProviderKey"] = "sqlserver",
                    ["jobProviderKey"] = "postgres",
                    ["candidateExecutionMode"] = executionMode,
                    ["jobExecutionMode"] = executionMode
                });

            var final = await engine.ExecuteAllAsync(created.ExecutionId);

            Assert.NotNull(final);
            Assert.True(final!.IsTerminal);
            Assert.Equal(AiExecutionStatus.Completed, final.Status);

            var store = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            var record = await store.GetRecordAsync(created.ExecutionId);
            var state = await store.GetStateAsync(created.ExecutionId);

            // cleanup may remove state/record depending on timing
            if (record != null)
            {
                Assert.True(record.IsTerminal);
            }

            if (state != null)
            {
                Assert.True(state.Steps["candidate"].IsCompleted);
                Assert.True(state.Steps["job"].IsCompleted);
                Assert.True(state.Steps["merge"].IsCompleted);
                Assert.True(state.Steps["compose"].IsCompleted);

                // validate compose output
                var compose = state.Steps["compose"];

                Assert.NotNull(compose.Result);
                Assert.NotNull(compose.Result!.Data);

                Assert.True(compose.Result.Data.ContainsKey("context"));
                Assert.True(compose.Result.Data.ContainsKey("fragments"));
            }
        }

        // ============================================================
        // HOST SETUP
        // ============================================================

        private static async Task<AiDagExecutionEngineTestHost> CreateHostAsync(
            Action<IServiceCollection> configure)
        {
            var options = new AiEngineOptions
            {
                JsonPipelineDefinitionFilePath = "config\\rag-final-test.json",
                Snapshots = new AiExecutionSnapshotOptions
                {
                    Enabled = true
                },
                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = true,
                    AutoCleanupOnFailed = true,
                    SuppressCleanupExceptions = true
                }
            };

            return await AiDagExecutionEngineFixture.CreateAsync(
                options,
                services =>
                {
                    //services.AddMultiplexAI(options);

                    configure(services);

                    services.AddRagFromAssemblies(
                        typeof(RagPluginsAssemblyMarker).Assembly,
                        typeof(AiRuntimeAssemblyMarker).Assembly);
                });
        }
    }
}