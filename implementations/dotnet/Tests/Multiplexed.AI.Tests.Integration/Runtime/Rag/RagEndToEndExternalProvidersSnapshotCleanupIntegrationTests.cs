using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Mongo;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Rag.Models;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Rag.Abstractions.Models;
using Multiplexed.AI.Runtime.AI.Rag.DI;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution.Payloads;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.Sample.External.Plugins.Rag;
using Multiplexed.Sample.External.Plugins.Rag.DI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Rag
{
    public sealed class RagEndToEndExternalProvidersSnapshotCleanupIntegrationTests
    {
        private const string SqlServerConnectionString =
            "Server=MSI\\SQLEXPRESS;Database=TestAiRuntimeRag;Trusted_Connection=True;TrustServerCertificate=True;";

        private const string PostgresConnectionString =
            "Host=localhost;Port=5432;Database=TestAiRuntimeRag;Username=postgres;Password=sa";

        private const string MongoConnectionString = "mongodb://localhost:27017";
        private const string MongoDatabaseName = "multiplexed_ai_tests";
        private const string MongoCollectionName = "ai_execution_snapshots_tests";

        [Fact]
        public async Task ExecuteAllAsync_Ef_Provider_Should_Persist_Snapshot_And_Replay()
        {
            await using var host = await CreateHostAsync(
                enableCleanup: false,
                deleteSnapshotsIfExist: false,
                services =>
                {
                    services.AddExternalSqlServerEf(SqlServerConnectionString);
                    services.AddExternalPostgresEf(PostgresConnectionString);
                    services.AddExternalRag();
                });

            var payloadOptions = host.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AiPayloadStoreOptions>>()
                .Value;

            Assert.Equal("mongo", payloadOptions.Provider);
            Assert.True(payloadOptions.RequireReplaySafePayloads);
            Assert.Equal(1, payloadOptions.MaxInlineSizeBytes);

            var dataPolicy = host.ServiceProvider.GetRequiredService<IAiExecutionDataPolicy>();
            Assert.IsType<SmartInlineAiExecutionDataPolicy>(dataPolicy);

            var payloadResolver = host.ServiceProvider.GetRequiredService<IAiExecutionPayloadResolver>();
            Assert.IsType<DefaultAiExecutionPayloadResolver>(payloadResolver);

            var payloadStoreResolver = host.ServiceProvider.GetRequiredService<IAiPayloadStoreResolver>();

            // Prove runtime DI can externalize with the SAME host used by the RAG test.
            // Use a deliberately large structured payload instead of a scalar string,
            // because scalar values may be kept inline by the smart data policy.
            var probeData = new Dictionary<string, object?>
            {
                ["kind"] = "externalization-probe",
                ["text"] = new string('x', 4096),
                ["items"] = Enumerable.Range(0, 128)
                    .Select(i => new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["value"] = $"probe-value-{i}",
                        ["payload"] = new string('y', 128)
                    })
                    .ToList()
            };

            var probePayload = await dataPolicy.StoreAsync(probeData);

            Assert.False(probePayload.IsInline);
            Assert.False(string.IsNullOrWhiteSpace(probePayload.ArtifactId));

            var probeResolved = await payloadResolver.ResolveAsync(probePayload);
            Assert.NotNull(probeResolved);

            var engine = host.Engine;

            var created = await engine.CreateAsync(
                "rag-endtoend-external-final-test",
                new Dictionary<string, object?>
                {
                    ["candidateId"] = "cand-001",
                    ["jobId"] = "job-001",
                    ["candidateProviderKey"] = "sqlserver",
                    ["jobProviderKey"] = "postgres",
                    ["candidateExecutionMode"] = "provider",
                    ["jobExecutionMode"] = "provider"
                });

            Assert.NotNull(created);
            Assert.False(string.IsNullOrWhiteSpace(created.ExecutionId));

            var final = await engine.ExecuteAllAsync(created.ExecutionId);

            Assert.NotNull(final);

            var dagStore =
                host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            var state =
                await dagStore.GetStateAsync(created.ExecutionId);

            var stepSummary = state is null
                ? "state-not-found"
                : string.Join(
                    ", ",
                    state.Steps.Values
                        .GroupBy(step => step.Status)
                        .OrderBy(group => group.Key)
                        .Select(group => $"{group.Key}={group.Count()}"));

            Assert.True(
                final!.IsTerminal,
                $"Execution '{final.ExecutionId}' did not reach a terminal state. " +
                $"Status='{final.Status}', " +
                $"IsTerminal='{final.IsTerminal}', " +
                $"StepSummary='{stepSummary}'.");

            Assert.Equal(
                AiExecutionStatus.Completed,
                final.Status);

            var snapshotStore =
                host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            var snapshot = await snapshotStore.GetAsync(created.ExecutionId);

            Assert.NotNull(snapshot);

            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();

            var replayResult = await replayService.ReplayAsync(
                new AiExecutionReplayRequest
                {
                    ExecutionId = created.ExecutionId,
                    Mode = AiExecutionReplayMode.ResumeIncomplete
                });

            Assert.NotNull(replayResult);
            Assert.True(replayResult.ReplayValid);

            await ValidatePayloadWasRecomposedAfterReplay(host, created.ExecutionId);

            if (!string.IsNullOrWhiteSpace(probePayload.ArtifactId))
            {
                await payloadStoreResolver.Resolve().DeleteAsync(probePayload.ArtifactId);
            }
        }

        /// <summary>
        /// Validates that replayed DAG state contains payload references and that
        /// externalized payloads can be recomposed through the configured resolver.
        /// </summary>
        private static async Task ValidatePayloadWasRecomposedAfterReplay(
            AiDagExecutionEngineTestHost host,
            string executionId)
        {
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            var replayedState = await dagStore.GetStateAsync(executionId);

            Assert.NotNull(replayedState);

            var stepsWithPayloads = replayedState!.Steps.Values
                .Where(s =>
                    s.Result?.Payload != null ||
                    (s.Result?.DataPayloads != null && s.Result.DataPayloads.Count > 0))
                .ToList();

            Assert.NotEmpty(stepsWithPayloads);

            var payloadResolver =
                host.ServiceProvider.GetRequiredService<IAiExecutionPayloadResolver>();

            var externalizedCount = 0;

            foreach (var step in stepsWithPayloads)
            {
                var result = step.Result!;

                if (result.Payload is not null)
                {
                    await AssertPayloadCanBeResolved(
                        result.Payload,
                        payloadResolver);

                    if (!result.Payload.IsInline)
                    {
                        externalizedCount++;
                    }
                }

                if (result.DataPayloads is not null)
                {
                    foreach (var payload in result.DataPayloads.Values)
                    {
                        await AssertPayloadCanBeResolved(
                            payload,
                            payloadResolver);

                        if (!payload.IsInline)
                        {
                            externalizedCount++;
                        }
                    }
                }
            }

            Assert.True(
                externalizedCount > 0,
                "Expected at least one externalized payload reference after DAG execution.");
        }

        /// <summary>
        /// Validates that a payload can be consumed safely.
        ///
        /// IMPORTANT:
        /// - Inline payloads are already available in state and may legally contain a null InlineValue.
        /// - Artifact-backed payloads must have an ArtifactId and must resolve through the payload resolver.
        /// </summary>
        private static async Task AssertPayloadCanBeResolved(
            AiStoredPayload payload,
            IAiExecutionPayloadResolver payloadResolver)
        {
            Assert.NotNull(payload);

            if (payload.IsInline)
            {
                return;
            }

            Assert.False(string.IsNullOrWhiteSpace(payload.ArtifactId));

            var resolved = await payloadResolver.ResolveAsync(payload);

            AssertResolvedPayload(resolved);
        }

        private static void AssertResolvedPayload(object? resolved)
        {
            Assert.NotNull(resolved);

            if (resolved is JsonElement json)
            {
                Assert.NotEqual(JsonValueKind.Undefined, json.ValueKind);
                Assert.NotEqual(JsonValueKind.Null, json.ValueKind);
            }
        }

        private static async Task<AiDagExecutionEngineTestHost> CreateHostAsync(
            bool enableCleanup,
            bool deleteSnapshotsIfExist,
            Action<IServiceCollection> configure)
        {
            var options = new AiEngineOptions
            {
                JsonPipelineDefinitionFilePath = "config\\rag-endtoend-external-final-test.json",
                PayloadStore = new AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "mongo",
                    RequireReplaySafePayloads = true,
                    MaxInlineSizeBytes = 1,
                    Mongo = new MongoAiPayloadStoreOptions
                    {
                        Enabled = true,
                        ConnectionString = MongoConnectionString,
                        DatabaseName = MongoDatabaseName,
                        CollectionName = "ai_execution_payloads_tests"
                    }
                },
                Snapshots = new AiExecutionSnapshotOptions
                {
                    Enabled = true,
                    Mongo = new AiExecutionSnapshotMongoOptions
                    {
                        Enabled = true,
                        ConnectionString = MongoConnectionString,
                        DatabaseName = MongoDatabaseName,
                        CollectionName = MongoCollectionName
                    }
                },
                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = enableCleanup,
                    AutoCleanupOnFailed = enableCleanup,
                    SuppressSnapshotIfExist = deleteSnapshotsIfExist,
                    SuppressCleanupExceptions = true
                }
            };

            return await AiDagExecutionEngineFixture.CreateAsync(
                options,
                services =>
                {
                    services.AddMultiplexAI(options);
                    configure(services);
                    services.AddRagFromAssemblies(
                        typeof(RagPluginsAssemblyMarker).Assembly,
                        typeof(AiRuntimeAssemblyMarker).Assembly);
                },
                mongoConnectionString: MongoConnectionString,
                mongoDatabaseName: MongoDatabaseName);
        }
    }
}