using FluentAssertions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.AI.Rag.Normalization;
using Multiplexed.AI.Runtime.Execution.Normalization;
using Multiplexed.AI.Runtime.Metrics;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Stores.Cache.Redis;
using Multiplexed.AI.Tests.Integration.Helpers;
using StackExchange.Redis;
using System.Data.Common;
using System.Reflection.PortableExecutable;
using Xunit;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// Integration tests covering recovery and lease behavior for the Redis-backed DAG execution store.
    ///
    /// PURPOSE:
    /// - Validate explicit lease persistence through LeaseExpiresAtUtc
    /// - Validate timeout recovery behavior against persisted lease expiration
    /// - Validate that stale completion/failure attempts are rejected through claim tokens
    ///
    /// IMPORTANT:
    /// - These tests exercise the real Redis/Lua store behavior
    /// - They are intended to validate distributed semantics, not in-memory logic
    /// - Each test uses an isolated key prefix to avoid cross-test collisions
    /// </summary>
    public sealed class RedisAiDagExecutionStoreRecoveryIntegrationTests : IAsyncLifetime
    {
        private IConnectionMultiplexer _multiplexer = default!;
        private IDatabase _database = default!;
        private RedisAiDagExecutionStore _store = default!;
        private TestAiExecutionKeyBuilder _keyBuilder = default!;
        private string _testPrefix = default!;

        /// <summary>
        /// Initializes Redis connectivity and a test-scoped key builder.
        /// </summary>
        public async Task InitializeAsync()
        {
            var logger = new NoopLogger();
            var redisConnectionString =
                Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS")
                ?? "localhost:6379,abortConnect=false,allowAdmin=true";

            _multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            _database = _multiplexer.GetDatabase();

            _testPrefix = $"test:dag:recovery:{Guid.NewGuid():N}";
            _keyBuilder = new TestAiExecutionKeyBuilder(_testPrefix);
            var metrics = MetricsFactory.Create();
            var normalizers = new DefaultAiStepResultNormalizerPipeline([new RagStepResultNormalizer()]);
            IRedisDagStoreServices services = new RedisDagStoreServices(
                _multiplexer,
                _keyBuilder,
                logger,
                metrics,
                normalizers);
            _store = new RedisAiDagExecutionStore(services);
        }

        /// <summary>
        /// Cleans up all keys created under the test prefix and disposes the Redis multiplexer.
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                foreach (var endpoint in _multiplexer.GetEndPoints())
                {
                    var server = _multiplexer.GetServer(endpoint);

                    if (!server.IsConnected)
                        continue;

                    await foreach (var key in server.KeysAsync(database: _database.Database, pattern: $"{_testPrefix}*"))
                    {
                        await _database.KeyDeleteAsync(key);
                    }
                }
            }
            finally
            {
                await _multiplexer.DisposeAsync();
            }
        }

        // ---------------------------------------------------------------------
        // LEASE / RECOVERY TESTS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Verifies that a successful claim persists explicit lease metadata on the step.
        ///
        /// EXPECTED:
        /// - step becomes Running
        /// - ClaimToken is assigned
        /// - ClaimedAtUtc is assigned
        /// - LeaseExpiresAtUtc is assigned
        /// - lease expiration is later than claim time
        /// </summary>
        [Fact]
        public async Task TryClaimNextReadyStepAsync_Should_Set_LeaseExpiresAtUtc()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateReadyStep("step-1", claimTimeoutSeconds: 30);

            await CreateExecutionAsync(executionId, step);

            // Act
            var claimed = await _store.TryClaimNextReadyStepAsync(executionId, "worker-a");

            // Assert
            claimed.Should().NotBeNull();

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Running);
            reloadedStep.ClaimToken.Should().NotBeNullOrWhiteSpace();
            reloadedStep.ClaimedBy.Should().Be("worker-a");
            reloadedStep.ClaimedAtUtc.Should().NotBeNull();
            reloadedStep.LeaseExpiresAtUtc.Should().NotBeNull();
            reloadedStep.LeaseExpiresAtUtc.Should().BeAfter(reloadedStep.ClaimedAtUtc!.Value);
        }

        /// <summary>
        /// Verifies that a successful completion clears all active lease metadata.
        ///
        /// EXPECTED:
        /// - step becomes Completed
        /// - claim ownership is cleared
        /// - lease expiration is cleared
        /// </summary>
        [Fact]
        public async Task TryCompleteStepAsync_Should_Clear_LeaseMetadata()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateReadyStep("step-1", claimTimeoutSeconds: 30);

            await CreateExecutionAsync(executionId, step);

            var claimed = await _store.TryClaimNextReadyStepAsync(executionId, "worker-a");
            claimed.Should().NotBeNull();

            var result = new AiStepResult();

            // Act
            var completed = await _store.TryCompleteStepAsync(
                executionId,
                "step-1",
                claimed!.ClaimToken,
                result);

            // Assert
            completed.Should().BeTrue();

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Completed);
            reloadedStep.ClaimedBy.Should().BeNull();
            reloadedStep.ClaimToken.Should().BeNull();
            reloadedStep.ClaimedAtUtc.Should().BeNull();
            reloadedStep.LeaseExpiresAtUtc.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a failed running step clears active lease metadata while
        /// still applying retry-or-fail semantics.
        ///
        /// EXPECTED:
        /// - step becomes WaitingForRetry when retry budget remains
        /// - claim ownership is cleared
        /// - lease expiration is cleared
        /// </summary>
        [Fact]
        public async Task TryFailStepAsync_Should_Clear_LeaseMetadata()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateReadyStep("step-1", claimTimeoutSeconds: 30);
            step.Retry = new AiRetryPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = "retry.transient.default"
                    }
                ],
                MaxRetries = 1,
                MaxDelayMs = 1000
            };

            await CreateExecutionAsync(executionId, step);

            var claimed = await _store.TryClaimNextReadyStepAsync(executionId, "worker-a");
            claimed.Should().NotBeNull();

            // Act
            var failed = await _store.TryFailStepAsync(
                executionId,
                "step-1",
                claimed!.ClaimToken,
                "simulated failure");

            // Assert
            failed.Should().BeTrue();

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.WaitingForRetry);
            reloadedStep.ClaimedBy.Should().BeNull();
            reloadedStep.ClaimToken.Should().BeNull();
            reloadedStep.ClaimedAtUtc.Should().BeNull();
            reloadedStep.LeaseExpiresAtUtc.Should().BeNull();
            reloadedStep.RetryState.Should().NotBeNull();
            reloadedStep.RetryState!.RetryCount.Should().Be(1);
        }

        /// <summary>
        /// Verifies that recovery does not requeue a running step before the lease expires.
        ///
        /// EXPECTED:
        /// - recovered count stays 0
        /// - step remains Running
        /// - claim metadata remains unchanged
        /// </summary>
        [Fact]
        public async Task RecoverTimedOutStepsAsync_Should_Not_Recover_Before_Lease_Expiration()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateRunningStep(
                stepName: "step-1",
                claimedBy: "worker-a",
                claimToken: "claim-token-1",
                claimedAtUtc: DateTime.UtcNow.AddSeconds(-5),
                leaseExpiresAtUtc: DateTime.UtcNow.AddSeconds(60),
                claimTimeoutSeconds: 60);

            await CreateExecutionAsync(executionId, step);

            // Act
            var recovered = await _store.RecoverTimedOutStepsAsync(executionId);

            // Assert
            recovered.Should().Be(0);

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Running);
            reloadedStep.ClaimedBy.Should().Be("worker-a");
            reloadedStep.ClaimToken.Should().Be("claim-token-1");
            reloadedStep.LeaseExpiresAtUtc.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that recovery requeues a running step once the persisted lease has expired.
        ///
        /// EXPECTED:
        /// - recovered count becomes 1
        /// - step transitions back to Ready
        /// - claim metadata is cleared
        /// - RecoveryCount is incremented
        /// </summary>
        [Fact]
        public async Task RecoverTimedOutStepsAsync_Should_Requeue_Expired_Running_Step()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateRunningStep(
                stepName: "step-1",
                claimedBy: "worker-a",
                claimToken: "claim-token-1",
                claimedAtUtc: DateTime.UtcNow.AddMinutes(-2),
                leaseExpiresAtUtc: DateTime.UtcNow.AddSeconds(-5),
                claimTimeoutSeconds: 60);

            await CreateExecutionAsync(executionId, step);

            // Act
            var recovered = await _store.RecoverTimedOutStepsAsync(executionId);

            // Assert
            recovered.Should().Be(1);

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Ready);
            reloadedStep.ClaimedBy.Should().BeNull();
            reloadedStep.ClaimToken.Should().BeNull();
            reloadedStep.ClaimedAtUtc.Should().BeNull();
            reloadedStep.LeaseExpiresAtUtc.Should().BeNull();
            reloadedStep.RecoveryCount.Should().Be(1);
        }

        /// <summary>
        /// Verifies that a successful recovery increments RecoveryCount exactly once.
        /// </summary>
        [Fact]
        public async Task RecoverTimedOutStepsAsync_Should_Increment_RecoveryCount()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateRunningStep(
                stepName: "step-1",
                claimedBy: "worker-a",
                claimToken: "claim-token-1",
                claimedAtUtc: DateTime.UtcNow.AddMinutes(-10),
                leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(-5),
                claimTimeoutSeconds: 60);

            step.RecoveryCount = 2;

            await CreateExecutionAsync(executionId, step);

            // Act
            var recovered = await _store.RecoverTimedOutStepsAsync(executionId);

            // Assert
            recovered.Should().Be(1);

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.RecoveryCount.Should().Be(3);
        }

        /// <summary>
        /// Verifies that timeout recovery preserves completed steps and only requeues
        /// running steps whose persisted lease has expired.
        ///
        /// EXPECTED:
        /// - completed steps remain Completed
        /// - expired running steps transition back to Ready
        /// - non-expired running steps remain Running
        /// - already ready steps remain Ready
        /// - recovery count is incremented only for recovered steps
        /// </summary>
        [Fact]
        public async Task RecoverTimedOutStepsAsync_Should_Preserve_Completed_Steps_And_Recover_Only_Expired_Running_Steps()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");

            var completedStep1 = CreateCompletedStep("step-1");
            var completedStep2 = CreateCompletedStep("step-2");

            var expiredRunningStep1 = CreateRunningStep(
                stepName: "step-3",
                claimedBy: "worker-expired-a",
                claimToken: "claim-token-expired-a",
                claimedAtUtc: DateTime.UtcNow.AddMinutes(-10),
                leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(-5),
                claimTimeoutSeconds: 60);

            var expiredRunningStep2 = CreateRunningStep(
                stepName: "step-4",
                claimedBy: "worker-expired-b",
                claimToken: "claim-token-expired-b",
                claimedAtUtc: DateTime.UtcNow.AddMinutes(-8),
                leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(-4),
                claimTimeoutSeconds: 60);

            expiredRunningStep2.RecoveryCount = 2;

            var activeRunningStep = CreateRunningStep(
                stepName: "step-5",
                claimedBy: "worker-active",
                claimToken: "claim-token-active",
                claimedAtUtc: DateTime.UtcNow.AddSeconds(-5),
                leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(5),
                claimTimeoutSeconds: 300);

            var readyStep = CreateReadyStep("step-6", claimTimeoutSeconds: 60);

            await CreateExecutionAsync(
                executionId,
                completedStep1,
                completedStep2,
                expiredRunningStep1,
                expiredRunningStep2,
                activeRunningStep,
                readyStep);

            // Act
            var recovered = await _store.RecoverTimedOutStepsAsync(executionId);

            // Assert
            recovered.Should().Be(2);

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedCompletedStep1 = state!.Steps["step-1"];
            var reloadedCompletedStep2 = state.Steps["step-2"];
            var reloadedExpiredRunningStep1 = state.Steps["step-3"];
            var reloadedExpiredRunningStep2 = state.Steps["step-4"];
            var reloadedActiveRunningStep = state.Steps["step-5"];
            var reloadedReadyStep = state.Steps["step-6"];

            reloadedCompletedStep1.Status.Should().Be(AiStepExecutionStatus.Completed);
            reloadedCompletedStep2.Status.Should().Be(AiStepExecutionStatus.Completed);
            reloadedCompletedStep1.RecoveryCount.Should().Be(0);
            reloadedCompletedStep2.RecoveryCount.Should().Be(0);

            reloadedExpiredRunningStep1.Status.Should().Be(AiStepExecutionStatus.Ready);
            reloadedExpiredRunningStep1.ClaimedBy.Should().BeNull();
            reloadedExpiredRunningStep1.ClaimToken.Should().BeNull();
            reloadedExpiredRunningStep1.ClaimedAtUtc.Should().BeNull();
            reloadedExpiredRunningStep1.LeaseExpiresAtUtc.Should().BeNull();
            reloadedExpiredRunningStep1.RecoveryCount.Should().Be(1);

            reloadedExpiredRunningStep2.Status.Should().Be(AiStepExecutionStatus.Ready);
            reloadedExpiredRunningStep2.ClaimedBy.Should().BeNull();
            reloadedExpiredRunningStep2.ClaimToken.Should().BeNull();
            reloadedExpiredRunningStep2.ClaimedAtUtc.Should().BeNull();
            reloadedExpiredRunningStep2.LeaseExpiresAtUtc.Should().BeNull();
            reloadedExpiredRunningStep2.RecoveryCount.Should().Be(3);

            reloadedActiveRunningStep.Status.Should().Be(AiStepExecutionStatus.Running);
            reloadedActiveRunningStep.ClaimedBy.Should().Be("worker-active");
            reloadedActiveRunningStep.ClaimToken.Should().Be("claim-token-active");
            reloadedActiveRunningStep.LeaseExpiresAtUtc.Should().NotBeNull();
            reloadedActiveRunningStep.RecoveryCount.Should().Be(0);

            reloadedReadyStep.Status.Should().Be(AiStepExecutionStatus.Ready);
            reloadedReadyStep.RecoveryCount.Should().Be(0);
        }

        /// <summary>
        /// Verifies that a stale completion attempt is rejected when the claim token does not match.
        ///
        /// EXPECTED:
        /// - completion returns false
        /// - step remains Running under the original ownership
        /// </summary>
        [Fact]
        public async Task TryCompleteStepAsync_Should_Reject_Stale_ClaimToken()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateReadyStep("step-1", claimTimeoutSeconds: 30);

            await CreateExecutionAsync(executionId, step);

            var claimed = await _store.TryClaimNextReadyStepAsync(executionId, "worker-a");
            claimed.Should().NotBeNull();

            // Act
            var completed = await _store.TryCompleteStepAsync(
                executionId,
                "step-1",
                "stale-token",
                new AiStepResult());

            // Assert
            completed.Should().BeFalse();

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Running);
            reloadedStep.ClaimToken.Should().Be(claimed!.ClaimToken);
            reloadedStep.ClaimedBy.Should().Be("worker-a");
        }

        /// <summary>
        /// Verifies that only one worker can claim the same ready step.
        ///
        /// EXPECTED:
        /// - exactly one non-null claim result
        /// - all other concurrent claim attempts return null
        /// </summary>
        [Fact]
        public async Task TryClaimNextReadyStepAsync_Should_Allow_Only_One_Winner()
        {
            // Arrange
            var executionId = Guid.NewGuid().ToString("N");
            var step = CreateReadyStep("step-1", claimTimeoutSeconds: 30);

            await CreateExecutionAsync(executionId, step);

            // Act
            var tasks = Enumerable.Range(0, 10)
                .Select(i => _store.TryClaimNextReadyStepAsync(executionId, $"worker-{i}"))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert
            results.Count(x => x is not null).Should().Be(1);

            var state = await _store.GetStateAsync(executionId);
            state.Should().NotBeNull();

            var reloadedStep = state!.Steps["step-1"];
            reloadedStep.Status.Should().Be(AiStepExecutionStatus.Running);
            reloadedStep.ClaimToken.Should().NotBeNullOrWhiteSpace();
            reloadedStep.ClaimedBy.Should().NotBeNullOrWhiteSpace();
        }

        // ---------------------------------------------------------------------
        // TEST HELPERS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Creates a full execution bundle in Redis with a minimal execution record and step state.
        /// </summary>
        private async Task CreateExecutionAsync(string executionId, params AiStepState[] steps)
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = executionId
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            foreach (var step in steps)
            {
                state.Steps[step.StepName] = step;
            }

            await _store.CreateAsync(record, state);
        }

        /// <summary>
        /// Builds a simple ready step suitable for claim and completion tests.
        /// </summary>
        private static AiStepState CreateReadyStep(string stepName, int claimTimeoutSeconds)
        {
            return new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                ClaimTimeoutSeconds = claimTimeoutSeconds,
                DependsOn = new List<string>(),
                Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                Config = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
        }

        /// <summary>
        /// Builds a completed step suitable for recovery preservation tests.
        /// </summary>
        private static AiStepState CreateCompletedStep(string stepName)
        {
            return new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Completed,
                DependsOn = new List<string>(),
                Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                Config = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
        }

        /// <summary>
        /// Builds a running step with explicit lease metadata for recovery tests.
        /// </summary>
        private static AiStepState CreateRunningStep(
            string stepName,
            string claimedBy,
            string claimToken,
            DateTime claimedAtUtc,
            DateTime leaseExpiresAtUtc,
            int claimTimeoutSeconds)
        {
            return new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Running,
                ClaimedBy = claimedBy,
                ClaimToken = claimToken,
                ClaimedAtUtc = claimedAtUtc,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                ClaimTimeoutSeconds = claimTimeoutSeconds,
                DependsOn = new List<string>(),
                Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                Config = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
        }

        // ---------------------------------------------------------------------
        // TEST KEY BUILDER
        // ---------------------------------------------------------------------

        /// <summary>
        /// Minimal test-only execution key builder.
        ///
        /// This keeps all keys scoped under a unique test prefix so integration
        /// tests remain isolated and cleanup can be targeted safely.
        /// </summary>
        private sealed class TestAiExecutionKeyBuilder : IAiExecutionKeyBuilder
        {
            private readonly string _prefix;

            public TestAiExecutionKeyBuilder(string prefix)
            {
                _prefix = prefix;
            }

            public string GetExecutionRecordKey(string executionId)
                => $"{_prefix}:exec:{executionId}:record";

            public string GetDagStepIdsKey(string executionId)
                => $"{_prefix}:exec:{executionId}:dag:steps";

            public string GetDagStepKey(string executionId, string stepName)
                => $"{GetDagStepKeyPrefix(executionId)}{stepName}";

            public string GetDagStepKeyPrefix(string executionId)
                => $"{_prefix}:exec:{executionId}:dag:step:";

            public string GetExecutionStateKey(string executionId)
            {
                throw new NotImplementedException();
            }

            public string GetDagClaimKey(string executionId, string stepId)
            {
                throw new NotImplementedException();
            }

            public string GetDagLeaseKey(string executionId, string stepId)
            {
                throw new NotImplementedException();
            }

            public string GetDagInFlightKey(string executionId)
            {
                throw new NotImplementedException();
            }

            public string GetDagMetaKey(string executionId)
            {
                throw new NotImplementedException();
            }
        }
    }
}