using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.AI.Concurrency
{
    /// <summary>
    /// Integration tests for the Redis-backed AI concurrency gate.
    /// </summary>
    /// <remarks>
    /// These tests validate distributed concurrency admission behavior using the Redis ZSET lease model.
    /// The most important invariant is crash-safe capacity recovery: if a worker acquires a lease and
    /// crashes before releasing it, the lease must expire and capacity must become available again.
    /// </remarks>
    public sealed class RedisAiConcurrencyGateIntegrationTests : IAsyncLifetime
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _database;

        public RedisAiConcurrencyGateIntegrationTests()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING")
                ?? "localhost:6379";

            _redis = ConnectionMultiplexer.Connect(connectionString);
            _database = _redis.GetDatabase();
        }

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            await CleanupConcurrencyKeysAsync();
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            await CleanupConcurrencyKeysAsync();

            await _redis.CloseAsync();
            _redis.Dispose();
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Allow_When_No_Limit_Is_Reached()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var context = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var decision = await gate.TryAcquireAsync(context, definition);

            Assert.True(decision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Deny_When_Global_Limit_Is_Reached()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondContext, definition);

            Assert.True(firstDecision.Allowed);
            Assert.False(secondDecision.Allowed);
        }

        [Fact]
        public async Task ReleaseAsync_Should_Free_Capacity_For_Next_Acquire()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            Assert.True(firstDecision.Allowed);

            var blockedDecision = await gate.TryAcquireAsync(secondContext, definition);
            Assert.False(blockedDecision.Allowed);

            await gate.ReleaseAsync(firstContext, definition);

            var recoveredDecision = await gate.TryAcquireAsync(secondContext, definition);
            Assert.True(recoveredDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Recover_Capacity_When_Lease_Expires()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 1,
                DefaultRetryAfterMs = 50
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            Assert.True(firstDecision.Allowed);

            var blockedDecision = await gate.TryAcquireAsync(secondContext, definition);
            Assert.False(blockedDecision.Allowed);

            await Task.Delay(TimeSpan.FromMilliseconds(1200));

            var recoveredDecision = await gate.TryAcquireAsync(secondContext, definition);
            Assert.True(recoveredDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Be_Idempotent_For_Same_Lease_Id()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var context = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "same-lease");

            var firstDecision = await gate.TryAcquireAsync(context, definition);
            var secondDecision = await gate.TryAcquireAsync(context, definition);

            Assert.True(firstDecision.Allowed);
            Assert.True(secondDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Respect_Pipeline_Scope()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxPipelineConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                pipelineKey: "pipeline-a:v1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                pipelineKey: "pipeline-a:v1",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var thirdContextDifferentPipeline = CreateContext(
                executionId: "exec-3",
                pipelineKey: "pipeline-b:v1",
                stepName: "step-c",
                workerId: "worker-3",
                leaseId: "lease-3");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondContext, definition);
            var thirdDecision = await gate.TryAcquireAsync(thirdContextDifferentPipeline, definition);

            Assert.True(firstDecision.Allowed);
            Assert.False(secondDecision.Allowed);
            Assert.True(thirdDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Respect_PipelineStep_Scope()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxStepConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                pipelineKey: "pipeline-a:v1",
                stepName: "llm.summary",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondSamePipelineSameStep = CreateContext(
                executionId: "exec-2",
                pipelineKey: "pipeline-a:v1",
                stepName: "llm.summary",
                workerId: "worker-2",
                leaseId: "lease-2");

            var thirdSamePipelineDifferentStep = CreateContext(
                executionId: "exec-3",
                pipelineKey: "pipeline-a:v1",
                stepName: "rag.retrieve",
                workerId: "worker-3",
                leaseId: "lease-3");

            var fourthDifferentPipelineSameStep = CreateContext(
                executionId: "exec-4",
                pipelineKey: "pipeline-b:v1",
                stepName: "llm.summary",
                workerId: "worker-4",
                leaseId: "lease-4");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondSamePipelineSameStep, definition);
            var thirdDecision = await gate.TryAcquireAsync(thirdSamePipelineDifferentStep, definition);
            var fourthDecision = await gate.TryAcquireAsync(fourthDifferentPipelineSameStep, definition);

            Assert.True(firstDecision.Allowed);
            Assert.False(secondDecision.Allowed);
            Assert.True(thirdDecision.Allowed);
            Assert.True(fourthDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Respect_Execution_Scope()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxExecutionConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondSameExecution = CreateContext(
                executionId: "exec-1",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var thirdDifferentExecution = CreateContext(
                executionId: "exec-2",
                stepName: "step-c",
                workerId: "worker-3",
                leaseId: "lease-3");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondSameExecution, definition);
            var thirdDecision = await gate.TryAcquireAsync(thirdDifferentExecution, definition);

            Assert.True(firstDecision.Allowed);
            Assert.False(secondDecision.Allowed);
            Assert.True(thirdDecision.Allowed);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Return_Diagnostic_Reason_When_Global_Limit_Is_Reached()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxGlobalConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                stepName: "step-a",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                stepName: "step-b",
                workerId: "worker-2",
                leaseId: "lease-2");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondContext, definition);

            Assert.True(firstDecision.Allowed);

            Assert.False(secondDecision.Allowed);
            Assert.NotNull(secondDecision.Reason);
            Assert.Contains("Concurrency limit reached", secondDecision.Reason);
            Assert.Contains("ai:concurrency:scope:global", secondDecision.Reason);
            Assert.Contains("Current='1'", secondDecision.Reason);
            Assert.Contains("Limit='1'", secondDecision.Reason);
        }

        [Fact]
        public async Task TryAcquireAsync_Should_Return_Diagnostic_Reason_When_PipelineStep_Limit_Is_Reached()
        {
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                MaxStepConcurrency = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            };

            var firstContext = CreateContext(
                executionId: "exec-1",
                pipelineKey: "pipeline-a:v1",
                stepName: "llm.summary",
                workerId: "worker-1",
                leaseId: "lease-1");

            var secondContext = CreateContext(
                executionId: "exec-2",
                pipelineKey: "pipeline-a:v1",
                stepName: "llm.summary",
                workerId: "worker-2",
                leaseId: "lease-2");

            var firstDecision = await gate.TryAcquireAsync(firstContext, definition);
            var secondDecision = await gate.TryAcquireAsync(secondContext, definition);

            Assert.True(firstDecision.Allowed);

            Assert.False(secondDecision.Allowed);
            Assert.NotNull(secondDecision.Reason);
            Assert.Contains("Concurrency limit reached", secondDecision.Reason);
            Assert.Contains(
                "ai:concurrency:scope:pipeline-step:pipeline-a:v1:llm.summary",
                secondDecision.Reason);
            Assert.Contains("Current='1'", secondDecision.Reason);
            Assert.Contains("Limit='1'", secondDecision.Reason);
        }

        /// <summary>
        /// Verifies that the claim service does not claim a ready step when the
        /// distributed concurrency gate denies admission.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_Should_Not_Claim_When_Concurrency_Gate_Denies()
        {
            // Arrange
            var executionId = "exec-throttled";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
                {
                    [stepName] = new AiStepState
                    {
                        StepName = stepName,
                        Status = AiStepExecutionStatus.Ready,
                        Config = stepConfig
                    }
                }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Deny(
                    "Concurrency limit reached for scope 'ai:concurrency:scope:global'. Current='1', Limit='1'.",
                    TimeSpan.FromMilliseconds(100)));

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            _ = concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());

            _ = dagStore.DidNotReceive().TryClaimStepAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

            _ = concurrencyGate.DidNotReceive().ReleaseAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }



        /// <summary>
        /// Verifies that the claim service releases distributed concurrency capacity
        /// when admission succeeds but the DAG step claim fails.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        /// <remarks>
        /// This test validates the distributed claim race path:
        /// a ready step is admitted by the concurrency gate, but another worker may claim
        /// the step first before this worker reaches <c>TryClaimStepAsync</c>.
        /// In that case, the claim service must release the previously acquired
        /// concurrency lease immediately to avoid leaking distributed capacity.
        /// </remarks>
        [Fact]
        public async Task ClaimNextAsync_Should_Release_Concurrency_Lease_When_Dag_Claim_Fails()
        {
            // Arrange
            var executionId = "exec-claim-race";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready
            }
        }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns((AiClaimedStep?)null);

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            await concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());

            await dagStore.Received(1).TryClaimStepAsync(
                executionId,
                stepName,
                workerId,
                Arg.Any<CancellationToken>());

            await concurrencyGate.Received(1).ReleaseAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that batch claiming does not attempt to claim a ready DAG step
        /// when the distributed concurrency gate denies admission.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        /// <remarks>
        /// This test validates the throttled batch path:
        /// the DAG store exposes a ready step, the concurrency gate denies capacity,
        /// and the claim service must return an empty batch without attempting a Redis step claim
        /// and without releasing a lease that was never acquired.
        /// </remarks>
        [Fact]
        public async Task ClaimBatchAsync_Should_Not_Claim_When_Concurrency_Gate_Denies()
        {
            // Arrange
            var executionId = "exec-batch-throttled";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready
            }
        }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Deny(
                    "Concurrency limit reached for scope 'ai:concurrency:scope:global'. Current='1', Limit='1'.",
                    TimeSpan.FromMilliseconds(100)));

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimedSteps = await service.ClaimBatchAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId,
                maxSteps: 4);

            // Assert
            Assert.Empty(claimedSteps);

            _ = dagStore.DidNotReceive().TryClaimStepAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

            _ = concurrencyGate.DidNotReceive().ReleaseAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that batch claiming releases distributed concurrency capacity
        /// when admission succeeds but the DAG step claim fails.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        /// <remarks>
        /// This test validates the distributed batch claim race path:
        /// a ready step is admitted by the concurrency gate, but another worker may claim
        /// the step first before this worker reaches <c>TryClaimStepAsync</c>.
        /// The service must release the acquired concurrency lease immediately and exclude
        /// the step from the returned batch.
        /// </remarks>
        [Fact]
        public async Task ClaimBatchAsync_Should_Release_Concurrency_Lease_When_Dag_Claim_Fails()
        {
            // Arrange
            var executionId = "exec-batch-claim-race";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready
            }
        }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns((AiClaimedStep?)null);

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            // Act
            var claimedSteps = await service.ClaimBatchAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId,
                maxSteps: 4);

            // Assert
            Assert.Empty(claimedSteps);

            _ = concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());

            _ = dagStore.Received(1).TryClaimStepAsync(
                executionId,
                stepName,
                workerId,
                Arg.Any<CancellationToken>());

            _ = concurrencyGate.Received(1).ReleaseAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }
        
        /// <summary>
        /// Verifies that step-level provider, model, and operation concurrency values override pipeline-level values.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        [Fact]
        public void Resolve_Should_Merge_Provider_Model_And_Operation_Concurrency_From_Pipeline_And_Step_Config()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 20,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 500
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["maxProviderConcurrency"] = 3,
                        ["maxModelConcurrency"] = 2
                    }
                }
            };

            // Act
            var definition = resolver.Resolve(pipeline, step);

            // Assert
            Assert.True(definition.Enabled);

            Assert.Equal(3, definition.MaxProviderConcurrency);
            Assert.Equal(2, definition.MaxModelConcurrency);

            // Falls back to pipeline because step does not override it.
            Assert.Equal(20, definition.MaxOperationConcurrency);

            Assert.Equal(60, definition.LeaseSeconds);
            Assert.Equal(500, definition.DefaultRetryAfterMs);
        }

        /// <summary>
        /// Verifies that omitted step-level lease configuration does not override
        /// a pipeline-level lease value with the runtime default.
        /// </summary>
        [Fact]
        public void Resolve_Should_Keep_Pipeline_LeaseSeconds_When_Step_LeaseSeconds_Is_Missing()
        {
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 500
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["maxProviderConcurrency"] = 3
                    }
                }
            };

            var definition = resolver.Resolve(pipeline, step);

            Assert.True(definition.Enabled);
            Assert.Equal(60, definition.LeaseSeconds);
            Assert.Equal(500, definition.DefaultRetryAfterMs);
            Assert.Equal(3, definition.MaxProviderConcurrency);
        }

        /// <summary>
        /// Verifies that an explicitly configured step-level lease value overrides
        /// the pipeline-level lease value.
        /// </summary>
        [Fact]
        public void Resolve_Should_Override_Pipeline_LeaseSeconds_When_Step_LeaseSeconds_Is_Configured()
        {
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["leaseSeconds"] = 60
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["leaseSeconds"] = 15
                    }
                }
            };

            var definition = resolver.Resolve(pipeline, step);

            Assert.True(definition.Enabled);
            Assert.Equal(15, definition.LeaseSeconds);
        }

        /// <summary>
        /// Verifies that provider, model, and operation limits fall back to pipeline-level
        /// configuration when the step does not override them.
        /// </summary>
        [Fact]
        public void Resolve_Should_Fallback_To_Pipeline_Provider_Model_And_Operation_Limits_When_Step_Does_Not_Override()
        {
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 20
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["maxStepConcurrency"] = 2
                    }
                }
            };

            var definition = resolver.Resolve(pipeline, step);

            Assert.True(definition.Enabled);
            Assert.Equal(10, definition.MaxProviderConcurrency);
            Assert.Equal(5, definition.MaxModelConcurrency);
            Assert.Equal(20, definition.MaxOperationConcurrency);
            Assert.Equal(2, definition.MaxStepConcurrency);
        }

        

        /// <summary>
        /// Verifies that the claim service passes provider, model, and operation metadata
        /// from the step state configuration into the concurrency gate context.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        /// <remarks>
        /// This test validates the full admission context used by provider, model, and
        /// operation-level throttling scopes.
        /// </remarks>
        [Fact]
        public async Task ClaimNextAsync_Should_Pass_Provider_Model_And_Operation_To_Concurrency_Gate()
        {
            // Arrange
            var executionId = "exec-provider-model-operation";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "llm-summary";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-4.1",
                    ["operation"] = "llm.chat",
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 20
                    }
                }
            }
        }
            };

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxProviderConcurrency"] = 10,
                    ["maxModelConcurrency"] = 5,
                    ["maxOperationConcurrency"] = 20
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                 [
                     new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                 ]
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns(new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = "claim-token"
                });

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.NotNull(claimed);
            Assert.Equal(stepName, claimed.StepName);

            _ = concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}" &&
                    context.Provider == "openai" &&
                    context.Model == "gpt-4.1" &&
                    context.Operation == "llm.chat"),
                Arg.Is<AiConcurrencyDefinition>(definition =>
                    definition.Enabled &&
                    definition.MaxProviderConcurrency == 10 &&
                    definition.MaxModelConcurrency == 5 &&
                    definition.MaxOperationConcurrency == 20),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that batch claiming passes provider, model, and operation metadata
        /// from the step state configuration into the concurrency gate context.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous test operation.
        /// </returns>
        /// <remarks>
        /// This test validates the batch admission path used by provider, model, and
        /// operation-level throttling scopes.
        /// </remarks>
        [Fact]
        public async Task ClaimBatchAsync_Should_Pass_Provider_Model_And_Operation_To_Concurrency_Gate()
        {
            // Arrange
            var executionId = "exec-batch-provider-model-operation";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "llm-summary";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-4.1",
                    ["operation"] = "llm.chat",
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 20
                    }
                }
            }
        }
            };


            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxProviderConcurrency"] = 10,
                    ["maxModelConcurrency"] = 5,
                    ["maxOperationConcurrency"] = 20
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns(new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = "claim-token"
                });

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimedSteps = await service.ClaimBatchAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId,
                maxSteps: 4);

            // Assert
            var claimed = Assert.Single(claimedSteps);
            Assert.Equal(stepName, claimed.StepName);

            _ = concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}" &&
                    context.Provider == "openai" &&
                    context.Model == "gpt-4.1" &&
                    context.Operation == "llm.chat"),
                Arg.Is<AiConcurrencyDefinition>(definition =>
                    definition.Enabled &&
                    definition.MaxProviderConcurrency == 10 &&
                    definition.MaxModelConcurrency == 5 &&
                    definition.MaxOperationConcurrency == 20),
                Arg.Any<CancellationToken>());

            _ = dagStore.Received(1).TryClaimStepAsync(
                executionId,
                stepName,
                workerId,
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that configured concurrency policies are resolved from JSON-style step configuration.
        /// </summary>
        /// <remarks>
        /// This test validates only JSON/config parsing. It does not execute concurrency policies.
        /// The current distributed throttling engine remains config-driven, while the policies collection
        /// is preserved for future policy-driven concurrency behavior.
        /// </remarks>
        [Fact]
        public void Resolve_Should_Read_Configured_Concurrency_Policies_From_Step_Config()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var stepState = new AiStepState
            {
                StepName = "llm-summary",
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-4.1",
                    ["operation"] = "llm.chat",
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 8,
                        ["policies"] = new[]
                        {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "provider.openai.standard",
                        ["kind"] = "Concurrency",
                        ["enabled"] = true,
                        ["config"] = new Dictionary<string, object?>
                        {
                            ["maxProviderConcurrency"] = 10,
                            ["maxModelConcurrency"] = 5,
                            ["leaseSeconds"] = 30,
                            ["defaultRetryAfterMs"] = 500
                        }
                    }
                }
                    }
                }
            };

            // Act
            var definition = resolver.Resolve(stepState);

            // Assert
            Assert.True(definition.Enabled);
            Assert.Equal(10, definition.MaxProviderConcurrency);
            Assert.Equal(5, definition.MaxModelConcurrency);
            Assert.Equal(8, definition.MaxOperationConcurrency);

            var policy = Assert.Single(definition.Policies);

            Assert.Equal("provider.openai.standard", policy.Name);
            Assert.Equal(AiPolicyKind.Concurrency.ToString(), policy.Kind);
            Assert.NotNull(policy.Config);
        }

        /// <summary>
        /// Verifies that configured concurrency policy config values are applied
        /// as defaults when direct concurrency values are missing.
        /// </summary>
        /// <remarks>
        /// This test validates policy-config enrichment only. It does not execute policies.
        /// Direct concurrency configuration remains authoritative when present.
        /// </remarks>
        [Fact]
        public void Resolve_Should_Apply_Concurrency_Limits_From_Configured_Policy_Config()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var stepState = new AiStepState
            {
                StepName = "llm-summary",
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["policies"] = new[]
                        {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "provider.openai.standard",
                        ["kind"] = "Concurrency",
                        ["config"] = new Dictionary<string, object?>
                        {
                            ["maxProviderConcurrency"] = 10,
                            ["maxModelConcurrency"] = 5,
                            ["maxOperationConcurrency"] = 8,
                            ["leaseSeconds"] = 30,
                            ["defaultRetryAfterMs"] = 500,
                            ["jitter"] = true,
                            ["maxJitterMs"] = 75
                        }
                    }
                }
                    }
                }
            };

            // Act
            var definition = resolver.Resolve(stepState);

            // Assert
            Assert.True(definition.Enabled);

            Assert.Equal(10, definition.MaxProviderConcurrency);
            Assert.Equal(5, definition.MaxModelConcurrency);
            Assert.Equal(8, definition.MaxOperationConcurrency);

            Assert.Equal(30, definition.LeaseSeconds);
            Assert.Equal(500, definition.DefaultRetryAfterMs);

            Assert.True(definition.Jitter);
            Assert.Equal(75, definition.MaxJitterMs);

            var policy = Assert.Single(definition.Policies);
            Assert.Equal("provider.openai.standard", policy.Name);
            Assert.Equal("Concurrency", policy.Kind);
        }

        /// <summary>
        /// Verifies that direct concurrency configuration values remain authoritative
        /// when configured policy config also defines the same values.
        /// </summary>
        /// <remarks>
        /// Policy config acts as a default bundle only. It must not override direct
        /// config.concurrency values.
        /// </remarks>
        [Fact]
        public void Resolve_Should_Keep_Direct_Concurrency_Config_When_Policy_Config_Also_Defines_Value()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var stepState = new AiStepState
            {
                StepName = "llm-summary",
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,

                        // Direct values must win.
                        ["maxProviderConcurrency"] = 3,
                        ["maxModelConcurrency"] = 2,
                        ["maxOperationConcurrency"] = 4,
                        ["leaseSeconds"] = 15,
                        ["defaultRetryAfterMs"] = 100,

                        ["policies"] = new[]
                        {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "provider.openai.standard",
                        ["kind"] = "Concurrency",
                        ["config"] = new Dictionary<string, object?>
                        {
                            ["maxProviderConcurrency"] = 10,
                            ["maxModelConcurrency"] = 5,
                            ["maxOperationConcurrency"] = 8,
                            ["leaseSeconds"] = 30,
                            ["defaultRetryAfterMs"] = 500
                        }
                    }
                }
                    }
                }
            };

            // Act
            var definition = resolver.Resolve(stepState);

            // Assert
            Assert.True(definition.Enabled);

            Assert.Equal(3, definition.MaxProviderConcurrency);
            Assert.Equal(2, definition.MaxModelConcurrency);
            Assert.Equal(4, definition.MaxOperationConcurrency);

            Assert.Equal(15, definition.LeaseSeconds);
            Assert.Equal(100, definition.DefaultRetryAfterMs);

            var policy = Assert.Single(definition.Policies);
            Assert.Equal("provider.openai.standard", policy.Name);
            Assert.Equal("Concurrency", policy.Kind);
        }

        /// <summary>
        /// Verifies that step-level policy config values override pipeline-level direct concurrency values.
        /// </summary>
        /// <remarks>
        /// Effective priority:
        /// step direct config > step policy config > pipeline direct config > pipeline policy config > defaults.
        /// </remarks>
        [Fact]
        public void Resolve_Should_Use_Step_Policy_Config_Before_Pipeline_Direct_Config()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10,
                        ["maxModelConcurrency"] = 5,
                        ["maxOperationConcurrency"] = 20,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 500
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["policies"] = new[]
                        {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "step.openai.fast",
                        ["kind"] = "Concurrency",
                        ["config"] = new Dictionary<string, object?>
                        {
                            ["maxProviderConcurrency"] = 3,
                            ["maxModelConcurrency"] = 2,
                            ["maxOperationConcurrency"] = 4,
                            ["leaseSeconds"] = 15,
                            ["defaultRetryAfterMs"] = 100
                        }
                    }
                }
                    }
                }
            };

            // Act
            var definition = resolver.Resolve(pipeline, step);

            // Assert
            Assert.True(definition.Enabled);

            Assert.Equal(3, definition.MaxProviderConcurrency);
            Assert.Equal(2, definition.MaxModelConcurrency);
            Assert.Equal(4, definition.MaxOperationConcurrency);

            Assert.Equal(15, definition.LeaseSeconds);
            Assert.Equal(100, definition.DefaultRetryAfterMs);

            var policy = Assert.Single(definition.Policies);
            Assert.Equal("step.openai.fast", policy.Name);
            Assert.Equal("Concurrency", policy.Kind);
        }

        /// <summary>
        /// Verifies that pipeline-level policy config values are used when the step has no concurrency config.
        /// </summary>
        /// <remarks>
        /// This validates that policy config can act as a pipeline-level concurrency template.
        /// </remarks>
        [Fact]
        public void Resolve_Should_Use_Pipeline_Policy_Config_When_Step_Config_Is_Missing()
        {
            // Arrange
            var resolver = new DefaultAiConcurrencyDefinitionResolver();

            var pipeline = new AiPipelineDefinition
            {
                Name = "test-pipeline",
                Version = "v1",
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["policies"] = new[]
                        {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "pipeline.openai.standard",
                        ["kind"] = "Concurrency",
                        ["config"] = new Dictionary<string, object?>
                        {
                            ["maxProviderConcurrency"] = 10,
                            ["maxModelConcurrency"] = 5,
                            ["maxOperationConcurrency"] = 20,
                            ["leaseSeconds"] = 60,
                            ["defaultRetryAfterMs"] = 500,
                            ["jitter"] = true,
                            ["maxJitterMs"] = 90
                        }
                    }
                }
                    }
                }
            };

            var step = new AiPipelineStepDefinition
            {
                Name = "llm-summary",
                StepKey = "llm.summary",
                Config = new Dictionary<string, object?>()
            };

            // Act
            var definition = resolver.Resolve(pipeline, step);

            // Assert
            Assert.True(definition.Enabled);

            Assert.Equal(10, definition.MaxProviderConcurrency);
            Assert.Equal(5, definition.MaxModelConcurrency);
            Assert.Equal(20, definition.MaxOperationConcurrency);

            Assert.Equal(60, definition.LeaseSeconds);
            Assert.Equal(500, definition.DefaultRetryAfterMs);

            Assert.True(definition.Jitter);
            Assert.Equal(90, definition.MaxJitterMs);

            var policy = Assert.Single(definition.Policies);
            Assert.Equal("pipeline.openai.standard", policy.Name);
            Assert.Equal("Concurrency", policy.Kind);
        }

        /// <summary>
        /// Verifies that the claim service records a decision ledger event when
        /// concurrency admission is denied.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_Should_Record_Ledger_Event_When_Concurrency_Gate_Denies()
        {
            // Arrange
            var executionId = "exec-ledger-throttled";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var ledger = new InMemoryAiDecisionLedger();

            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity = new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Strict
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
            {
                Name = stepName,
                StepKey = stepName,
                Config = stepConfig
            }
                ]
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = stepConfig
            }
        }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Deny(
                    "Concurrency limit reached for scope 'ai:concurrency:scope:global'. Current='1', Limit='1'.",
                    TimeSpan.FromMilliseconds(100)));

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate,
                ledgerRecorder: recorder);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.NotEmpty(entries);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Attempted &&
                    entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Denied &&
                    entry.Outcome == AiDecisionLedgerOutcome.Denied);

            var entry = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.Denied);

            Assert.Equal(AiDecisionLedgerCategory.Concurrency, entry.Category);
            Assert.Equal(AiDecisionLedgerEvents.Concurrency.Denied, entry.EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, entry.Outcome);
            Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, entry.CorrelationContext.PipelineName);
            Assert.Equal(stepName, entry.CorrelationContext.StepId);
            Assert.Equal(stepName, entry.CorrelationContext.StepKey);
            Assert.Equal(workerId, entry.CorrelationContext.WorkerId);
            Assert.Equal(workerId, entry.CorrelationContext.RuntimeInstanceId);
            Assert.Contains("Concurrency limit reached", entry.Reason);
        }

        /// <summary>
        /// Verifies that the claim service records a decision ledger event when
        /// a DAG step claim is acquired.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_Should_Record_Ledger_Event_When_Step_Claim_Is_Acquired()
        {
            // Arrange
            var executionId = "exec-ledger-claim-acquired";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "llm-summary";
            var claimToken = "claim-token";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var ledger = new InMemoryAiDecisionLedger();


            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =
            new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Strict,
                    StorageMode = AiDecisionLedgerStorageMode.InMemory
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-4.1",
                    ["operation"] = "llm.chat",
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10
                    }
                }
            }
        }
            };

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxProviderConcurrency"] = 10
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
            {
                Name = stepName,
                StepKey = stepName,
                Config = stepConfig
            }
                ]
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns(new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = claimToken
                });

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate,
                ledgerRecorder: recorder);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.NotNull(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            var entry = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Acquired);

            Assert.NotEmpty(entries);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Attempted &&
                    entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.LeaseAcquired &&
                    entry.Outcome == AiDecisionLedgerOutcome.Allowed);



            Assert.Equal(AiDecisionLedgerCategory.Claim, entry.Category);
            Assert.Equal(AiDecisionLedgerEvents.Claim.Acquired, entry.EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Allowed, entry.Outcome);
            Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, entry.CorrelationContext.PipelineName);
            Assert.Equal(stepName, entry.CorrelationContext.StepId);
            Assert.Equal(stepName, entry.CorrelationContext.StepKey);
            Assert.Equal(workerId, entry.CorrelationContext.WorkerId);
            Assert.Equal(workerId, entry.CorrelationContext.RuntimeInstanceId);
            Assert.Equal(claimToken, entry.CorrelationContext.ClaimToken);
            Assert.Equal("openai", entry.CorrelationContext.Provider);
            Assert.Equal("gpt-4.1", entry.CorrelationContext.Model);
            Assert.Equal("llm.chat", entry.CorrelationContext.Operation);
        }

        /// <summary>
        /// Verifies that the claim service records decision ledger events when
        /// a concurrency lease is acquired but the DAG step claim is lost.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_Should_Record_Ledger_Events_When_Claim_Fails_After_Concurrency_Lease()
        {
            // Arrange
            var executionId = "exec-ledger-claim-race";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

            var ledger = new InMemoryAiDecisionLedger();

            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =
            new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Strict,
                    StorageMode = AiDecisionLedgerStorageMode.InMemory
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
            {
                Name = stepName,
                StepKey = stepName,
                Config = stepConfig
            }
                ]
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = stepConfig
            }
        }
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns((AiClaimedStep?)null);

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate,
                ledgerRecorder: recorder);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);
            Assert.NotEmpty(entries);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Attempted &&
                    entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.LeaseAcquired &&
                    entry.Outcome == AiDecisionLedgerOutcome.Allowed);

            var claimDenied = Assert.Single(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Claim &&
                entry.EventType == AiDecisionLedgerEvents.Claim.Denied &&
                entry.Outcome == AiDecisionLedgerOutcome.Denied &&
                entry.CorrelationContext.StepId == stepName);

            Assert.Equal(executionId, claimDenied.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, claimDenied.CorrelationContext.PipelineName);
            Assert.Equal(stepName, claimDenied.CorrelationContext.StepId);
            Assert.Equal(workerId, claimDenied.CorrelationContext.WorkerId);
            Assert.Equal($"{executionId}:{stepName}:{workerId}", claimDenied.CorrelationContext.ClaimToken);
            Assert.Contains("failed after concurrency lease", claimDenied.Reason);

            var leaseReleased = Assert.Single(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Concurrency &&
                entry.EventType == AiDecisionLedgerEvents.Concurrency.LeaseReleased &&
                entry.Outcome == AiDecisionLedgerOutcome.Released);

            Assert.Equal(executionId, leaseReleased.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, leaseReleased.CorrelationContext.PipelineName);
            Assert.Equal(stepName, leaseReleased.CorrelationContext.StepId);
            Assert.Equal(workerId, leaseReleased.CorrelationContext.WorkerId);
            Assert.Equal($"{executionId}:{stepName}:{workerId}", leaseReleased.CorrelationContext.ClaimToken);
            Assert.Contains(
                "Concurrency lease released",
                leaseReleased.Reason);

            Assert.True(
                leaseReleased.Reason?.Contains(
                    "step claim",
                    StringComparison.OrdinalIgnoreCase) == true,
                $"Expected the lease-release reason to describe the failed step claim. Actual='{leaseReleased.Reason}'.");

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Denied &&
                    entry.Outcome == AiDecisionLedgerOutcome.Denied &&
                    entry.CorrelationContext.StepId == "_claim");
        }

        /// <summary>
        /// Creates the minimal engine service bundle required by the step claim service tests.
        /// </summary>
        /// <param name="dagStore">
        /// The substituted distributed DAG store.
        /// </param>
        /// <param name="concurrencyGate">
        /// The substituted concurrency gate.
        /// </param>
        /// <param name="policyEngineFactory">
        /// The optional substituted policy engine factory.
        /// </param>
        /// <returns>
        /// A substituted <see cref="IAiDagExecutionEngineServices"/> instance.
        /// </returns>
        private static IAiDagExecutionEngineServices CreateEngineServices(
            IAiDagExecutionStore dagStore,
            IAiConcurrencyGate concurrencyGate,
            IAiPolicyEngineFactory? policyEngineFactory = null,
            IAiDecisionLedgerRecorder? ledgerRecorder = null)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();

            var logger = Substitute.For<IAiRuntimeLogger>();
            var observability = Substitute.For<IAiRuntimeObservability>();
            var tracer = new PassthroughAiRuntimeTracer();

            services.DagStore.Returns(dagStore);
            services.ConcurrencyGate.Returns(concurrencyGate);
            services.PolicyEngineFactory.Returns(
                policyEngineFactory ?? Substitute.For<IAiPolicyEngineFactory>());

            services.Services.Returns(Substitute.For<IServiceProvider>());
            services.StateReader.Returns(Substitute.For<IAiExecutionStateReader>());
            services.StateWriter.Returns(Substitute.For<IAiExecutionStateWriter>());

            services.Logger.Returns(logger);

            services.ObservabilityService.Returns(observability);
            observability.Tracer.Returns(tracer);
            observability.Ledger.Returns(ledgerRecorder ?? new NoOpAiDecisionLedgerRecorder());

            return services;
        }

        private sealed class PassthroughAiRuntimeTracer : IAiRuntimeTracer
        {
            public IAiTraceScope StartExecution(AiExecutionTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartResolver(AiResolverTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartRetention(AiRetentionTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartStep(AiStepTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartStorage(AiStorageTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public Task<TResult> TraceStorageAsync<TResult>(
                AiStorageTraceContext context,
                Func<IAiTraceScope, Task<TResult>> action,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scope = Substitute.For<IAiTraceScope>();

                return action(scope);
            }
        }

        /// <summary>
        /// Configures storage tracing to execute the supplied callback immediately.
        /// </summary>
        /// <param name="tracer">
        /// The substituted runtime tracer.
        /// </param>
        /// <remarks>
        /// The claim service wraps Redis operations in storage traces. These tests are not validating
        /// tracing behavior, so the tracer is configured as a passthrough wrapper.
        /// </remarks>
        private static void ConfigureTraceStoragePassthrough(IAiRuntimeTracer tracer)
        {
            tracer.TraceStorageAsync(
                    Arg.Any<AiStorageTraceContext>(),
                    Arg.Any<Func<IAiTraceScope, Task<int>>>())
                .Returns(callInfo =>
                {
                    var callback = callInfo.Arg<Func<IAiTraceScope, Task<int>>>();
                    var scope = Substitute.For<IAiTraceScope>();

                    return callback(scope);
                });

            tracer.TraceStorageAsync(
                    Arg.Any<AiStorageTraceContext>(),
                    Arg.Any<Func<IAiTraceScope, Task<AiClaimedStep?>>>())
                .Returns(callInfo =>
                {
                    var callback = callInfo.Arg<Func<IAiTraceScope, Task<AiClaimedStep?>>>();
                    var scope = Substitute.For<IAiTraceScope>();

                    return callback(scope);
                });

            tracer.TraceStorageAsync(
                    Arg.Any<AiStorageTraceContext>(),
                    Arg.Any<Func<IAiTraceScope, Task<AiConcurrencyDecision>>>())
                .Returns(callInfo =>
                {
                    var callback = callInfo.Arg<Func<IAiTraceScope, Task<AiConcurrencyDecision>>>();
                    var scope = Substitute.For<IAiTraceScope>();

                    return callback(scope);
                });
        }

        /// <summary>
        /// Verifies that the claim service does not create a concurrency policy engine
        /// when no concurrency policies are configured.
        /// </summary>
        /// <remarks>
        /// The fast path remains config-driven. Without configured policies, the service should go
        /// directly to the Redis concurrency gate.
        /// </remarks>
        [Fact]
        public async Task ClaimNextAsync_Should_Not_Create_Concurrency_Policy_Engine_When_No_Policies_Are_Configured()
        {
            // Arrange
            var executionId = "exec-no-policy";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var policyEngineFactory = Substitute.For<IAiPolicyEngineFactory>();

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxProviderConcurrency"] = 10
                    }
                }
            }
        }
            };

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxProviderConcurrency"] = 10
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns(new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = "claim-token"
                });

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate,
                policyEngineFactory: policyEngineFactory);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.NotNull(claimed);

            _ = policyEngineFactory.DidNotReceive().Create(
                Arg.Any<AiPolicyKind>(),
                Arg.Any<AiStepExecutionContext>());

            _ = concurrencyGate.Received(1).TryAcquireAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a denied concurrency policy decision prevents Redis lease acquisition
        /// and DAG step claiming.
        /// </summary>
        /// <remarks>
        /// Policy admission runs before the distributed Redis gate. If the policy-aware
        /// concurrency engine denies admission, no Redis lease should be acquired and
        /// the DAG step should not be claimed.
        /// </remarks>
        [Fact]
        public async Task ClaimNextAsync_Should_Not_Acquire_Redis_Lease_When_Concurrency_Policy_Denies()
        {
            // Arrange
            var executionId = "exec-policy-denied";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var policyEngineFactory = Substitute.For<IAiPolicyEngineFactory>();

            var policyEngine = Substitute.For<IAiPolicyEngine, IAiConcurrencyEngine>();
            var concurrencyEngine = (IAiConcurrencyEngine)policyEngine;

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["policies"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "concurrency.block.test",
                                ["kind"] = "Concurrency"
                            }
                        }
                    }
                }
            }
        }
            };

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["policies"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "concurrency.block.test",
                                ["kind"] = "Concurrency"
                            }
                        }
                }
            };

            var pipeline = new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
            new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = "ready-token"
            }
                });

            policyEngineFactory.Create(
                    AiPolicyKind.Concurrency,
                    Arg.Any<AiStepExecutionContext>())
                .Returns((IAiPolicyEngine)policyEngine);

            concurrencyEngine.DecideAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Deny(
                    "Blocked by concurrency policy.",
                    TimeSpan.FromMilliseconds(100)));

            var services = CreateEngineServices(
                dagStore: dagStore,
                concurrencyGate: concurrencyGate,
                policyEngineFactory: policyEngineFactory);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            _ = policyEngineFactory.Received(1).Create(
                AiPolicyKind.Concurrency,
                Arg.Any<AiStepExecutionContext>());

            _ = concurrencyEngine.Received(1).DecideAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == $"{executionId}:{stepName}:{workerId}"),
                Arg.Any<CancellationToken>());

            _ = concurrencyGate.DidNotReceive().TryAcquireAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());

            _ = dagStore.DidNotReceive().TryClaimStepAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

            _ = concurrencyGate.DidNotReceive().ReleaseAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a generic provider throttle rule is ignored when the provider target does not match.
        /// </summary>
        [Fact]
        public async Task TryAcquireAsync_Should_Not_Apply_Generic_Provider_Throttle_When_Target_Does_Not_Match()
        {
            // Arrange
            var gate = new RedisAiConcurrencyGate(_redis);

            var definition = new AiConcurrencyDefinition
            {
                Enabled = true,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100,
                ThrottleRules = new List<AiConcurrencyThrottleRule>
        {
            new()
            {
                Scope = "provider",
                Target = "openai",
                Limit = 1,
                LeaseSeconds = 10,
                DefaultRetryAfterMs = 100
            }
        }
            };

            var firstContext = CreateContext(
                executionId: "exec-generic-provider-no-match-1",
                stepName: "summary-step",
                workerId: "worker-1",
                leaseId: "lease-generic-provider-no-match-1",
                provider: "anthropic",
                model: "claude-sonnet",
                operation: "llm.chat");

            var secondContext = CreateContext(
                executionId: "exec-generic-provider-no-match-2",
                stepName: "compose-step",
                workerId: "worker-2",
                leaseId: "lease-generic-provider-no-match-2",
                provider: "anthropic",
                model: "claude-sonnet",
                operation: "llm.chat");

            var firstDefinition = AiConcurrencyThrottleRuleApplicator.Apply(
                definition,
                firstContext);

            var secondDefinition = AiConcurrencyThrottleRuleApplicator.Apply(
                definition,
                secondContext);

            // Act
            var firstDecision = await gate.TryAcquireAsync(
                firstContext,
                firstDefinition);

            var secondDecision = await gate.TryAcquireAsync(
                secondContext,
                secondDefinition);

            // Assert
            Assert.True(firstDecision.Allowed);
            Assert.True(secondDecision.Allowed);
        }

        

        
        /// <summary>
        /// Creates a concurrency context used by the Redis concurrency gate tests.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier used for execution-level throttling.
        /// </param>
        /// <param name="stepName">
        /// The logical step name used as both step id and step key.
        /// </param>
        /// <param name="workerId">
        /// The runtime worker or instance identifier.
        /// </param>
        /// <param name="leaseId">
        /// The lease identifier stored as a Redis ZSET member.
        /// </param>
        /// <param name="pipelineKey">
        /// The stable pipeline key used for pipeline and pipeline-step throttling.
        /// </param>
        /// <param name="provider">
        /// The optional provider used for provider/model-level throttling.
        /// </param>
        /// <param name="model">
        /// The optional model used for model-level throttling.
        /// </param>
        /// <param name="operation">
        /// The optional logical operation used for operation-level throttling.
        /// </param>
        /// <returns>
        /// A populated <see cref="AiConcurrencyContext"/>.
        /// </returns>
        private static AiConcurrencyContext CreateContext(
            string executionId,
            string stepName,
            string workerId,
            string leaseId,
            string pipelineKey = "pipeline-a:v1",
            string? provider = null,
            string? model = null,
            string? operation = null)
        {
            return new AiConcurrencyContext
            {
                ExecutionId = executionId,
                PipelineKey = pipelineKey,
                StepId = stepName,
                StepKey = stepName,
                RuntimeInstanceId = workerId,
                LeaseId = leaseId,
                Provider = provider,
                Model = model,
                Operation = operation
            };
        }

        private async Task CleanupConcurrencyKeysAsync()
        {
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var keys = server.Keys(pattern: "ai:concurrency:scope:*").ToArray();

                foreach (var key in keys)
                {
                    await _database.KeyDeleteAsync(key);
                }
            }
        }
    }
}