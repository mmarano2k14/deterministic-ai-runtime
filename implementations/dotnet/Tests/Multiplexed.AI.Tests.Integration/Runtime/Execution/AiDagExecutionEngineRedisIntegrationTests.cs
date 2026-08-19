using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Rag.Normalization;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Normalization;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Stores.Cache;
using Multiplexed.AI.Stores.Cache.Redis;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Helpers;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Models;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;
using StackExchange.Redis;
using System.Text.Json;
using Xunit;
using static Multiplexed.AI.Tests.Integration.Helpers.MetricsFactory;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// End-to-end Redis integration tests for <see cref="AiDagExecutionEngine"/>
    /// using the distributed DAG Redis/Lua store.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Validate DAG execution creation.
    /// - Validate step-by-step distributed progression.
    /// - Validate ExecuteAll orchestration.
    /// - Validate persisted step-state propagation across Redis round-trips.
    /// - Validate claim ownership safety.
    /// - Validate timeout recovery.
    ///
    /// RETENTION MIGRATION:
    /// - These tests no longer wire legacy options-driven retention services.
    /// - Engine creation uses the production-like fixture, which is aligned with the
    ///   policy-driven retention engine.
    /// - Pipelines without retention config run with retention as a no-op.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiDagExecutionEngineRedisIntegrationTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineRedisIntegrationTests"/> class.
        /// </summary>
        /// <param name="fixture">The Redis fixture.</param>
        public AiDagExecutionEngineRedisIntegrationTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            _connection = fixture.Connection;
        }

        /// <summary>
        /// Verifies that ExecuteAll completes a basic DAG pipeline using the production fixture.
        /// </summary>
        [RedisFact]
        public async Task ExecuteAllAsync_Should_Complete_Basic_Dag_Pipeline()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");

            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                var finalRecord = await host.Engine.ExecuteAllAsync(created.ExecutionId);

                Assert.NotNull(finalRecord);
                Assert.Equal(AiExecutionMode.Dag, finalRecord.ExecutionMode);
                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);

                Assert.Equal(4, finalRecord.CompletedSteps.Count);
                Assert.Contains("start", finalRecord.CompletedSteps);
                Assert.Contains("a1", finalRecord.CompletedSteps);
                Assert.Contains("a2", finalRecord.CompletedSteps);
                Assert.Contains("merge", finalRecord.CompletedSteps);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

                var state = await dagStore.GetStateAsync(finalRecord.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(state!, "start").Status);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(state, "a1").Status);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(state, "a2").Status);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(state, "merge").Status);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that concurrent exact-id creation attempts converge on one Redis-backed DAG execution.
        /// </summary>
        [RedisFact]
        public async Task CreateIfAbsentAsync_Should_Converge_On_One_Exact_Dag_Execution()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");
            var executionId = Guid.NewGuid().ToString("N");
            var definition = new AiPipelineDefinition
            {
                Name = "dag-parallel-basic",
                Version = "frozen-child-v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "frozen-only",
                        StepKey = "hello-world",
                        Order = 1
                    }
                ]
            };

            try
            {
                var snapshotService = new AiChildDagSnapshotService(
                    host.ServiceProvider.GetRequiredService<IAiPayloadStoreResolver>(),
                    host.ServiceProvider.GetRequiredService<IOptions<AiPayloadStoreOptions>>());
                var definitionSnapshot = await snapshotService
                    .FreezeDefinitionAsync(definition, "exact-create-if-absent-test");

                var attempts = Enumerable.Range(0, 8)
                    .Select(_ => host.Engine.CreateIfAbsentAsync(
                        executionId,
                        definition,
                        definitionSnapshot,
                        "Marco"))
                    .ToArray();

                var results = await Task.WhenAll(attempts);

                Assert.All(results, result => Assert.Equal(executionId, result.ExecutionId));

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var record = await dagStore.GetRecordAsync(executionId);
                var state = await dagStore.GetStateAsync(executionId);

                Assert.NotNull(record);
                Assert.NotNull(state);
                Assert.Equal("dag-parallel-basic", record!.PipelineName);
                Assert.Equal(definitionSnapshot.ContentHash, record.PipelineDefinitionSnapshot?.ContentHash);
                Assert.Equal("dag-parallel-basic", state!.PipelineName);
                Assert.Equal(
                    "frozen-child-v1",
                    AiResultDataAssertions.ExtractString(
                        state.Metadata["pipeline.definition.version"],
                        "pipeline.definition.version"));
                Assert.Single(state.Steps);
                Assert.Equal(new[] { "frozen-only" }, record.Steps);

                var completed = await host.Engine.ExecuteAllAsync(executionId);

                Assert.Equal(AiExecutionStatus.Completed, completed.Status);
                Assert.Equal(new[] { "frozen-only" }, completed.CompletedSteps);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies that Redis atomically parks an owned running step without consuming retry or recovery budget.
        /// </summary>
        [RedisFact]
        public async Task TryParkStepAsync_Should_Commit_WaitingForExternal_And_Clear_Claim_Without_Counters()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");
            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var claimed = await dagStore.TryClaimNextReadyStepAsync(
                    created.ExecutionId,
                    "park-worker");

                Assert.NotNull(claimed);

                var claimedState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(claimedState);
                var before = claimedState!.Steps[claimed!.StepName];
                Assert.Equal(AiStepExecutionStatus.Running, before.Status);

                var retryCount = before.RetryState?.RetryCount ?? 0;
                var recoveryCount = before.RecoveryCount;

                var parked = await dagStore.TryParkStepAsync(
                    created.ExecutionId,
                    claimed.StepName,
                    claimed.ClaimToken);

                Assert.True(parked);

                var parkedState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(parkedState);
                var step = parkedState!.Steps[claimed.StepName];

                Assert.Equal(AiStepExecutionStatus.WaitingForExternal, step.Status);
                Assert.Null(step.ClaimedBy);
                Assert.Null(step.ClaimToken);
                Assert.Null(step.ClaimedAtUtc);
                Assert.Null(step.LeaseExpiresAtUtc);
                Assert.Equal(retryCount, step.RetryState?.RetryCount ?? 0);
                Assert.Equal(recoveryCount, step.RecoveryCount);
                Assert.Null(step.CompletedAtUtc);
                Assert.False(step.IsTerminal);

                var duplicatePark = await dagStore.TryParkStepAsync(
                    created.ExecutionId,
                    claimed.StepName,
                    claimed.ClaimToken);

                Assert.False(duplicatePark);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that Redis atomically reactivates one externally waiting step without changing retry or recovery counters.
        /// </summary>
        [RedisFact]
        public async Task TryResumeExternalWaitingStepAsync_Should_Commit_Ready_And_Remain_Idempotent()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");
            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var claimed = await dagStore.TryClaimNextReadyStepAsync(
                    created.ExecutionId,
                    "external-wait-worker");

                Assert.NotNull(claimed);
                var claimedState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(claimedState);
                var before = claimedState!.Steps[claimed!.StepName];
                var retryCount = before.RetryState?.RetryCount ?? 0;
                var recoveryCount = before.RecoveryCount;

                Assert.True(await dagStore.TryParkStepAsync(
                    created.ExecutionId,
                    claimed.StepName,
                    claimed.ClaimToken));

                var resumed = await dagStore.TryResumeExternalWaitingStepAsync(
                    created.ExecutionId,
                    claimed.StepName);

                Assert.True(resumed);

                var resumedState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(resumedState);
                var step = resumedState!.Steps[claimed.StepName];
                Assert.Equal(AiStepExecutionStatus.Ready, step.Status);
                Assert.Null(step.ClaimedBy);
                Assert.Null(step.ClaimToken);
                Assert.Null(step.ClaimedAtUtc);
                Assert.Null(step.LeaseExpiresAtUtc);
                Assert.Equal(retryCount, step.RetryState?.RetryCount ?? 0);
                Assert.Equal(recoveryCount, step.RecoveryCount);
                Assert.Null(step.CompletedAtUtc);

                Assert.False(await dagStore.TryResumeExternalWaitingStepAsync(
                    created.ExecutionId,
                    claimed.StepName));
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that ExecuteNext executes the root step first.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Execute_Root_First()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");

            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                var record = await host.Engine.ExecuteNextAsync(created.ExecutionId);

                Assert.Contains("start", record.CompletedSteps);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

                var state = await dagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(state!, "start").Status);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that repeated ExecuteNext calls complete all steps.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Complete_All_Steps_In_Order()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");

            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                _ = await host.Engine.ExecuteNextAsync(created.ExecutionId);
                _ = await host.Engine.ExecuteNextAsync(created.ExecutionId);
                _ = await host.Engine.ExecuteNextAsync(created.ExecutionId);

                var r4 = await host.Engine.ExecuteNextAsync(created.ExecutionId);

                Assert.Equal(AiExecutionStatus.Completed, r4.Status);
                Assert.Equal(4, r4.CompletedSteps.Count);
                Assert.Contains("merge", r4.CompletedSteps);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that distributed DAG state survives Redis reloads.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Persist_Dag_State_Across_Redis_Reload()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");

            var created = await host.Engine.CreateAsync("dag-parallel-basic", "Marco");

            try
            {
                var afterFirst = await host.Engine.ExecuteNextAsync(created.ExecutionId);

                Assert.Contains("start", afterFirst.CompletedSteps);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

                var persistedRecord = await dagStore.GetRecordAsync(created.ExecutionId);
                var persistedState = await dagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(persistedRecord);
                Assert.NotNull(persistedState);

                Assert.Equal(AiExecutionMode.Dag, persistedRecord!.ExecutionMode);
                Assert.Contains("start", afterFirst.CompletedSteps);
                Assert.Equal(AiStepExecutionStatus.Completed, stateWriter.GetOrCreateStep(persistedState!, "start").Status);

                var finalRecord = await host.Engine.ExecuteAllAsync(created.ExecutionId);

                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                Assert.Equal(4, finalRecord.CompletedSteps.Count);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that the DAG engine rejects non-DAG executions.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Throw_When_Not_Dag_Mode()
        {
            await using var host = await CreateHostAsync("dag-parallel-basic.json");

            var store = host.ServiceProvider.GetRequiredService<IAiExecutionStore>();

            var record = new AiExecutionRecord
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                PipelineName = "dag-parallel-basic",
                ExecutionMode = AiExecutionMode.Sequential,
                ContextKey = "ctx",
                Status = AiExecutionStatus.Pending
            };

            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName
            };

            await store.CreateAsync(record, state);

            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => host.Engine.ExecuteNextAsync(record.ExecutionId));
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that only one worker can claim a root step.
        /// </summary>
        [RedisFact]
        public async Task TryClaimNextReadyStepAsync_Should_Allow_Only_One_Worker_To_Claim_Root_Step()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["start"] = new AiStepState
            {
                StepName = "start",
                Status = AiStepExecutionStatus.Ready
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var claims = await Task.WhenAll(
                    dagStore.TryClaimNextReadyStepAsync(executionId, "worker-1"),
                    dagStore.TryClaimNextReadyStepAsync(executionId, "worker-2"),
                    dagStore.TryClaimNextReadyStepAsync(executionId, "worker-3"));

                var success = claims.Where(x => x is not null).ToArray();

                Assert.Single(success);
                Assert.Equal("start", success[0]!.StepName);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies that dependent steps cannot be claimed before dependencies complete.
        /// </summary>
        [RedisFact]
        public async Task TryClaimNextReadyStepAsync_Should_Not_Claim_Dependent_Step_Before_Dependency_Is_Completed()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["start"] = new AiStepState
            {
                StepName = "start",
                Status = AiStepExecutionStatus.Ready
            };

            state.Steps["merge"] = new AiStepState
            {
                StepName = "merge",
                Status = AiStepExecutionStatus.Ready,
                DependsOn = new List<string> { "start" }
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var claim1 = await dagStore.TryClaimNextReadyStepAsync(executionId, "worker-1");

                Assert.NotNull(claim1);
                Assert.Equal("start", claim1!.StepName);

                var claim2 = await dagStore.TryClaimNextReadyStepAsync(executionId, "worker-2");

                Assert.Null(claim2);

                var completed = await dagStore.TryCompleteStepAsync(
                    executionId,
                    "start",
                    claim1.ClaimToken,
                    AiStepResult.Ok("done"));

                Assert.True(completed);

                var claim3 = await dagStore.TryClaimNextReadyStepAsync(executionId, "worker-3");

                Assert.NotNull(claim3);
                Assert.Equal("merge", claim3!.StepName);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies that expired running steps are recovered.
        /// </summary>
        [RedisFact]
        public async Task RecoverTimedOutStepsAsync_Should_Requeue_Expired_Running_Step()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["start"] = new AiStepState
            {
                StepName = "start",
                Status = AiStepExecutionStatus.Running,
                ClaimedBy = "worker-old",
                ClaimToken = "claim-old",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-9),
                ClaimTimeoutSeconds = 30
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var recovered = await dagStore.RecoverTimedOutStepsAsync(executionId);

                Assert.Equal(1, recovered);

                var snapshot = await dagStore.GetStateAsync(executionId);

                Assert.NotNull(snapshot);

                var step = snapshot!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.Ready, step.Status);
                Assert.Null(step.ClaimedBy);
                Assert.Null(step.ClaimToken);
                Assert.Null(step.ClaimedAtUtc);
                Assert.Null(step.LeaseExpiresAtUtc);
                Assert.Equal(1, step.RecoveryCount);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies that a recovered timed-out running step can be claimed again and that
        /// dependent steps remain blocked until the recovered step completes.
        ///
        /// EXPECTED:
        /// - already completed dependency remains Completed
        /// - expired running step is recovered back to Ready
        /// - recovered step can be claimed by a new worker
        /// - dependent step is not claimable before the recovered step completes
        /// - dependent step becomes claimable after the recovered step completes
        /// </summary>
        [RedisFact]
        public async Task RecoverTimedOutStepsAsync_And_TryClaimNextReadyStepAsync_Should_Resume_From_Recovered_Step()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["start"] = new AiStepState
            {
                StepName = "start",
                Status = AiStepExecutionStatus.Completed
            };

            state.Steps["work"] = new AiStepState
            {
                StepName = "work",
                Status = AiStepExecutionStatus.Running,
                ClaimedBy = "worker-old",
                ClaimToken = "claim-old",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-9),
                ClaimTimeoutSeconds = 30,
                DependsOn = new List<string> { "start" }
            };

            state.Steps["merge"] = new AiStepState
            {
                StepName = "merge",
                Status = AiStepExecutionStatus.Ready,
                DependsOn = new List<string> { "work" }
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var recovered = await dagStore.RecoverTimedOutStepsAsync(executionId);

                Assert.Equal(1, recovered);

                var recoveredSnapshot = await dagStore.GetStateAsync(executionId);

                Assert.NotNull(recoveredSnapshot);
                Assert.Equal(AiStepExecutionStatus.Completed, recoveredSnapshot!.Steps["start"].Status);
                Assert.Equal(AiStepExecutionStatus.Ready, recoveredSnapshot.Steps["work"].Status);
                Assert.Equal(1, recoveredSnapshot.Steps["work"].RecoveryCount);
                Assert.Null(recoveredSnapshot.Steps["work"].ClaimedBy);
                Assert.Null(recoveredSnapshot.Steps["work"].ClaimToken);
                Assert.Null(recoveredSnapshot.Steps["work"].ClaimedAtUtc);
                Assert.Null(recoveredSnapshot.Steps["work"].LeaseExpiresAtUtc);
                Assert.Equal(AiStepExecutionStatus.Ready, recoveredSnapshot.Steps["merge"].Status);

                var recoveredClaim = await dagStore.TryClaimNextReadyStepAsync(
                    executionId,
                    "worker-new");

                Assert.NotNull(recoveredClaim);
                Assert.Equal("work", recoveredClaim!.StepName);

                var blockedClaim = await dagStore.TryClaimNextReadyStepAsync(
                    executionId,
                    "worker-blocked");

                Assert.Null(blockedClaim);

                var completed = await dagStore.TryCompleteStepAsync(
                    executionId,
                    "work",
                    recoveredClaim.ClaimToken,
                    AiStepResult.Ok("recovered work done"));

                Assert.True(completed);

                var dependentClaim = await dagStore.TryClaimNextReadyStepAsync(
                    executionId,
                    "worker-dependent");

                Assert.NotNull(dependentClaim);
                Assert.Equal("merge", dependentClaim!.StepName);

                var finalSnapshot = await dagStore.GetStateAsync(executionId);

                Assert.NotNull(finalSnapshot);
                Assert.Equal(AiStepExecutionStatus.Completed, finalSnapshot!.Steps["start"].Status);
                Assert.Equal(AiStepExecutionStatus.Completed, finalSnapshot.Steps["work"].Status);
                Assert.Equal(AiStepExecutionStatus.Running, finalSnapshot.Steps["merge"].Status);
                Assert.Equal("worker-dependent", finalSnapshot.Steps["merge"].ClaimedBy);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies that completion rejects an invalid claim token.
        /// </summary>
        [RedisFact]
        public async Task TryCompleteStepAsync_Should_Reject_Wrong_ClaimToken()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["start"] = new AiStepState
            {
                StepName = "start",
                Status = AiStepExecutionStatus.Ready
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var claim = await dagStore.TryClaimNextReadyStepAsync(executionId, "worker-1");

                Assert.NotNull(claim);

                var completed = await dagStore.TryCompleteStepAsync(
                    executionId,
                    "start",
                    "wrong-token",
                    AiStepResult.Ok("done"));

                Assert.False(completed);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Verifies completion of a generated random 100-step DAG.
        /// </summary>
        [Fact]
        public async Task ExecuteAllAsync_Should_Complete_Random_100_Step_Dag()
        {
            await RunGeneratedDagScenarioAsync(
                pipelineName: "dag-random-100",
                stepCount: 100,
                seed: 12345,
                mode: GeneratedDagMode.Random);
        }

        /// <summary>
        /// Verifies completion of a generated parallel-heavy 100-step DAG.
        /// </summary>
        [Fact]
        public async Task ExecuteAllAsync_Should_Complete_Parallel_Heavy_100_Step_Dag()
        {
            await RunGeneratedDagScenarioAsync(
                pipelineName: "dag-parallel-heavy-100",
                stepCount: 100,
                seed: 42,
                mode: GeneratedDagMode.ParallelHeavy);
        }

        /// <summary>
        /// Verifies completion of a generated linear 100-step DAG.
        /// </summary>
        [Fact]
        public async Task ExecuteAllAsync_Should_Complete_Linear_100_Step_Dag()
        {
            await RunGeneratedDagScenarioAsync(
                pipelineName: "dag-linear-100",
                stepCount: 100,
                seed: 7,
                mode: GeneratedDagMode.Linear);
        }

        /// <summary>
        /// Verifies completion of a generated fan-in 100-step DAG.
        /// </summary>
        [Fact]
        public async Task ExecuteAllAsync_Should_Complete_FanIn_100_Step_Dag()
        {
            await RunGeneratedDagScenarioAsync(
                pipelineName: "dag-fanin-100",
                stepCount: 100,
                seed: 99,
                mode: GeneratedDagMode.FanIn);
        }

        /// <summary>
        /// Runs a generated DAG scenario using the production-like fixture.
        /// </summary>
        private async Task RunGeneratedDagScenarioAsync(
            string pipelineName,
            int stepCount,
            int seed,
            GeneratedDagMode mode)
        {
            var definition = CreateGeneratedDagPipeline(
                pipelineName,
                stepCount,
                seed,
                mode);

            var filePath = WritePipelineDefinitionToConfig(definition);

            try
            {
                await using var host = await CreateHostAsync(pipelineName + ".json");

                var created = await host.Engine.CreateAsync(pipelineName, "Marco");

                try
                {
                    var finalRecord = await host.Engine.ExecuteAllAsync(created.ExecutionId);

                    Assert.NotNull(finalRecord);
                    Assert.Equal(AiExecutionMode.Dag, finalRecord.ExecutionMode);
                    Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                    Assert.Equal(stepCount, finalRecord.CompletedSteps.Count);

                    var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                    var state = await dagStore.GetStateAsync(finalRecord.ExecutionId);

                    Assert.NotNull(state);
                    Assert.Equal(stepCount, state!.Steps.Count);

                    Assert.All(
                        state.Steps.Values,
                        step => Assert.Equal(AiStepExecutionStatus.Completed, step.Status));
                }
                finally
                {
                    await CleanupDagExecutionAsync(created.ExecutionId);
                }
            }
            finally
            {
                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Creates a production-like host using JSON pipeline configuration.
        /// </summary>
        private async Task<AiDagExecutionEngineTestHost> CreateHostAsync(
            string jsonFileName)
        {
            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = "config/" + jsonFileName,

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

            return await AiDagExecutionEngineFixture.CreateAsync(
                options,
                mongoConnectionString: "mongodb://localhost:27017",
                mongoDatabaseName: "multiplexed_ai_tests",
                redisConnectionString: "localhost:6379");
        }

        /// <summary>
        /// Creates a generated DAG pipeline definition.
        /// </summary>
        private static AiPipelineDefinition CreateGeneratedDagPipeline(
            string pipelineName,
            int stepCount,
            int seed,
            GeneratedDagMode mode)
        {
            var random = new Random(seed);
            var steps = new List<AiPipelineStepDefinition>();

            for (var i = 0; i < stepCount; i++)
            {
                var name = $"s{i + 1:000}";
                var dependsOn = new List<string>();

                if (i > 0)
                {
                    var previous = steps.Select(x => x.Name).ToList();

                    switch (mode)
                    {
                        case GeneratedDagMode.Linear:
                            dependsOn.Add(previous[^1]);
                            break;

                        case GeneratedDagMode.FanIn:
                            if (i == stepCount - 1)
                            {
                                dependsOn.AddRange(previous);
                            }
                            else
                            {
                                dependsOn = previous
                                    .OrderBy(_ => random.Next())
                                    .Take(random.Next(0, Math.Min(3, previous.Count) + 1))
                                    .ToList();
                            }

                            break;

                        case GeneratedDagMode.ParallelHeavy:
                            dependsOn = previous
                                .OrderBy(_ => random.Next())
                                .Take(random.Next(0, Math.Min(1, previous.Count) + 1))
                                .ToList();

                            break;

                        case GeneratedDagMode.Random:
                        default:
                            dependsOn = previous
                                .OrderBy(_ => random.Next())
                                .Take(random.Next(0, Math.Min(3, previous.Count) + 1))
                                .ToList();

                            break;
                    }
                }

                steps.Add(new AiPipelineStepDefinition
                {
                    Name = name,
                    StepKey = "hello-world",
                    Order = i + 1,
                    DependsOn = dependsOn,
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = random.Next(0, 10)
                    }
                });
            }

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };
        }

        /// <summary>
        /// Writes a pipeline definition into the config directory.
        /// </summary>
        private static string WritePipelineDefinitionToConfig(
            AiPipelineDefinition definition)
        {
            var root = new
            {
                pipelines = new[] { definition }
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

            var filePath = Path.Combine(configDir, $"{definition.Name}.json");

            File.WriteAllText(filePath, json);

            return filePath;
        }

        /// <summary>
        /// Creates a distributed DAG store for Redis/Lua store-level tests.
        /// </summary>
        private IAiDagExecutionStore CreateDagStore()
        {
            var logger = new NoopLogger();
            var metrics = MetricsFactory.Create();
            var keyBuilder = new AiExecutionKeyBuilder();

            var normalizers = new DefaultAiStepResultNormalizerPipeline(
                [new RagStepResultNormalizer()]);

            IRedisDagStoreServices services = new RedisDagStoreServices(
                _connection,
                keyBuilder,
                logger,
                metrics,
                normalizers);

            return new RedisAiDagExecutionStore(
                services);
        }

        /// <summary>
        /// Best-effort cleanup of Redis DAG execution keys.
        /// </summary>
        private async Task CleanupDagExecutionAsync(
            string executionId)
        {
            var db = _connection.GetDatabase();

            var recordKey = $"ai:execution:record:{executionId}";
            var stateKey = $"ai:execution:state:{executionId}";
            var stepsIndexKey = $"ai:execution:steps:{executionId}";

            var stepNames = await db.SetMembersAsync(stepsIndexKey);

            foreach (var stepName in stepNames)
            {
                if (stepName.IsNullOrEmpty)
                {
                    continue;
                }

                await db.KeyDeleteAsync($"ai:execution:step:{executionId}:{stepName}");
            }

            await db.KeyDeleteAsync(recordKey);
            await db.KeyDeleteAsync(stateKey);
            await db.KeyDeleteAsync(stepsIndexKey);
        }

        /// <summary>
        /// Deletes a file when it exists.
        /// </summary>
        private static void TryDeleteFile(
            string filePath)
        {
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
        /// Generated DAG shape.
        /// </summary>
        private enum GeneratedDagMode
        {
            Random,
            ParallelHeavy,
            Linear,
            FanIn
        }

        /// <summary>
        /// Payload resolver used by store-level tests where payload resolution is not expected.
        /// </summary>
        private sealed class NoopPayloadResolver : IAiExecutionPayloadResolver
        {
            /// <inheritdoc />
            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Payload resolution is not expected in this Redis DAG integration test.");
            }
        }
    }
}
