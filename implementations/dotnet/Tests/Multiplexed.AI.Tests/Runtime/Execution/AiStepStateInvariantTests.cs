using System;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Retry;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Execution
{
    /// <summary>
    /// Unit tests covering the local state-machine invariants of <see cref="AiStepState"/>.
    ///
    /// PURPOSE:
    /// - Validate legal transitions
    /// - Reject illegal transitions early
    /// - Protect retry / recovery / claim semantics from accidental regression
    ///
    /// IMPORTANT:
    /// These tests validate only the step-level state machine.
    /// They do not cover:
    /// - distributed Redis/Lua atomicity
    /// - execution-level convergence
    /// - retry engine policy decisions
    /// - engine orchestration
    /// </summary>
    public sealed class AiStepStateInvariantTests
    {
        /// <summary>
        /// Ensures a step cannot be claimed twice.
        /// </summary>
        [Fact]
        public void MarkRunning_Should_Throw_When_Step_Is_Already_Running()
        {
            var step = CreateStep();
            step.MarkRunning("worker-1", "token-1");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkRunning("worker-2", "token-2"));

            Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures a terminal step cannot be claimed again.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkRunning_Should_Throw_When_Step_Is_Terminal(AiStepExecutionStatus terminalStatus)
        {
            var step = CreateStep();
            step.Status = terminalStatus;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkRunning("worker-1", "token-1"));

            Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures completion is only legal from Running.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.None)]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkCompleted_Should_Throw_When_Step_Is_Not_Running(AiStepExecutionStatus invalidStatus)
        {
            var step = CreateStep();
            step.Status = invalidStatus;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkCompleted(new AiStepResult()));

            Assert.Contains("cannot complete", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures failure is only legal from Running or WaitingForRetry.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.None)]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkFailed_Should_Throw_When_Step_Is_Not_Running_Or_WaitingForRetry(AiStepExecutionStatus invalidStatus)
        {
            var step = CreateStep();
            step.Status = invalidStatus;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkFailed("boom"));

            Assert.Contains("cannot fail", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures retry waiting is only legal from Running.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.None)]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkWaitingForRetry_Should_Throw_When_Step_Is_Not_Running(AiStepExecutionStatus invalidStatus)
        {
            var step = CreateStep();
            step.Status = invalidStatus;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkWaitingForRetry("retry later", DateTime.UtcNow.AddSeconds(10)));

            Assert.Contains("WaitingForRetry", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures waiting for retry stores the retry eligibility timestamp in RetryState.
        /// </summary>
        [Fact]
        public void MarkWaitingForRetry_Should_Set_RetryState_NextRetryAtUtc()
        {
            var step = CreateRunningStep();
            var nextRetryAtUtc = DateTime.UtcNow.AddSeconds(10);

            step.MarkWaitingForRetry("retry later", nextRetryAtUtc);

            Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
            Assert.NotNull(step.RetryState);
            Assert.Equal(nextRetryAtUtc, step.RetryState!.NextRetryAtUtc);
            Assert.Null(step.ClaimedBy);
            Assert.Null(step.ClaimToken);
            Assert.Null(step.ClaimedAtUtc);
            Assert.Null(step.LeaseExpiresAtUtc);
        }

        /// <summary>
        /// Ensures voluntary external suspension clears claim ownership without consuming retry or recovery budget.
        /// </summary>
        [Fact]
        public void MarkWaitingForExternal_Should_Clear_Claim_Without_Changing_Retry_Recovery_Or_Terminal_State()
        {
            var step = CreateRunningStep();
            step.RetryState = new AiStepRetryState
            {
                RetryCount = 2,
                NextRetryAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            step.RecoveryCount = 3;
            var retryCount = step.RetryState.RetryCount;
            var nextRetryAtUtc = step.RetryState.NextRetryAtUtc;

            step.MarkWaitingForExternal();

            Assert.Equal(AiStepExecutionStatus.WaitingForExternal, step.Status);
            Assert.True(step.IsWaitingForExternal);
            Assert.Null(step.ClaimedBy);
            Assert.Null(step.ClaimToken);
            Assert.Null(step.ClaimedAtUtc);
            Assert.Null(step.LeaseExpiresAtUtc);
            Assert.Equal(retryCount, step.RetryState.RetryCount);
            Assert.Equal(nextRetryAtUtc, step.RetryState.NextRetryAtUtc);
            Assert.Equal(3, step.RecoveryCount);
            Assert.Null(step.CompletedAtUtc);
            Assert.Null(step.Duration);
            Assert.False(step.IsTerminal);
            Assert.False(step.IsSchedulable);
        }

        /// <summary>
        /// Ensures voluntary external suspension is only legal from Running.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.None)]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkWaitingForExternal_Should_Throw_When_Step_Is_Not_Running(
            AiStepExecutionStatus invalidStatus)
        {
            var step = CreateStep();
            step.Status = invalidStatus;

            var exception = Assert.Throws<InvalidOperationException>(
                step.MarkWaitingForExternal);

            Assert.Contains(
                "WaitingForExternal",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures timeout recovery is only legal from Running.
        /// </summary>
        [Theory]
        [InlineData(AiStepExecutionStatus.None)]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.WaitingForExternal)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void MarkRequeuedAfterTimeout_Should_Throw_When_Step_Is_Not_Running(AiStepExecutionStatus invalidStatus)
        {
            var step = CreateStep();
            step.Status = invalidStatus;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                step.MarkRequeuedAfterTimeout());

            Assert.Contains("cannot be recovered", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensures MarkCompleted clears all claim metadata.
        /// </summary>
        [Fact]
        public void MarkCompleted_Should_Clear_Claim_Metadata()
        {
            var step = CreateRunningStep();

            step.MarkCompleted(new AiStepResult());

            Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
            Assert.Null(step.ClaimedBy);
            Assert.Null(step.ClaimToken);
            Assert.Null(step.ClaimedAtUtc);
            Assert.Null(step.LeaseExpiresAtUtc);
        }

        /// <summary>
        /// Ensures MarkCompleted clears pending retry eligibility when retry state exists.
        /// </summary>
        [Fact]
        public void MarkCompleted_Should_Clear_RetryState_NextRetryAtUtc_When_RetryState_Exists()
        {
            var step = CreateRunningStep();
            step.RetryState = new AiStepRetryState
            {
                RetryCount = 1,
                NextRetryAtUtc = DateTime.UtcNow.AddSeconds(10)
            };

            step.MarkCompleted(new AiStepResult());

            Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
            Assert.Null(step.RetryState.NextRetryAtUtc);
        }

        /// <summary>
        /// Ensures MarkFailed clears all claim metadata.
        /// </summary>
        [Fact]
        public void MarkFailed_Should_Clear_Claim_Metadata()
        {
            var step = CreateRunningStep();

            step.MarkFailed("boom");

            Assert.Equal(AiStepExecutionStatus.Failed, step.Status);
            Assert.Null(step.ClaimedBy);
            Assert.Null(step.ClaimToken);
            Assert.Null(step.ClaimedAtUtc);
            Assert.Null(step.LeaseExpiresAtUtc);
        }

        /// <summary>
        /// Ensures MarkFailed clears pending retry eligibility when retry state exists.
        /// </summary>
        [Fact]
        public void MarkFailed_Should_Clear_RetryState_NextRetryAtUtc_When_RetryState_Exists()
        {
            var step = CreateRunningStep();
            step.RetryState = new AiStepRetryState
            {
                RetryCount = 1,
                NextRetryAtUtc = DateTime.UtcNow.AddSeconds(10)
            };

            step.MarkFailed("boom");

            Assert.Equal(AiStepExecutionStatus.Failed, step.Status);
            Assert.Null(step.RetryState.NextRetryAtUtc);
        }

        /// <summary>
        /// Ensures timeout recovery does not modify business retry state.
        /// </summary>
        [Fact]
        public void MarkRequeuedAfterTimeout_Should_Not_Modify_RetryState()
        {
            var step = CreateRunningStep();
            step.RetryState = new AiStepRetryState { RetryCount = 2 };
            step.RecoveryCount = 0;

            step.MarkRequeuedAfterTimeout();

            Assert.Equal(AiStepExecutionStatus.Ready, step.Status);
            Assert.Equal(2, step.RetryState.RetryCount);
            Assert.Equal(1, step.RecoveryCount);
        }

        /// <summary>
        /// Ensures a running claim computes lease expiration when ClaimTimeoutSeconds is configured.
        /// </summary>
        [Fact]
        public void MarkRunning_Should_Set_Lease_When_Timeout_Is_Configured()
        {
            var step = CreateStep();
            step.ClaimTimeoutSeconds = 30;

            step.MarkRunning("worker-1", "token-1");

            Assert.Equal(AiStepExecutionStatus.Running, step.Status);
            Assert.Equal("worker-1", step.ClaimedBy);
            Assert.Equal("token-1", step.ClaimToken);
            Assert.NotNull(step.ClaimedAtUtc);
            Assert.NotNull(step.LeaseExpiresAtUtc);
            Assert.True(step.LeaseExpiresAtUtc > step.ClaimedAtUtc);
        }

        /// <summary>
        /// Ensures lease expiration is left null when no timeout is configured.
        /// </summary>
        [Fact]
        public void MarkRunning_Should_Not_Set_Lease_When_Timeout_Is_Not_Configured()
        {
            var step = CreateStep();
            step.ClaimTimeoutSeconds = null;

            step.MarkRunning("worker-1", "token-1");

            Assert.Equal(AiStepExecutionStatus.Running, step.Status);
            Assert.Null(step.LeaseExpiresAtUtc);
        }

        /// <summary>
        /// Ensures retry-waiting steps are promoted to Ready when the retry window is due.
        /// </summary>
        [Fact]
        public void PromoteRetryToReadyIfDue_Should_Promote_When_Retry_Window_Is_Due()
        {
            var now = DateTime.UtcNow;
            var step = CreateStep();
            step.Status = AiStepExecutionStatus.WaitingForRetry;
            step.RetryState = new AiStepRetryState
            {
                NextRetryAtUtc = now.AddMilliseconds(-1)
            };

            step.PromoteRetryToReadyIfDue(now);

            Assert.Equal(AiStepExecutionStatus.Ready, step.Status);
            Assert.Null(step.RetryState.NextRetryAtUtc);
        }

        /// <summary>
        /// Ensures retry-waiting steps are not promoted before the retry window opens.
        /// </summary>
        [Fact]
        public void PromoteRetryToReadyIfDue_Should_Not_Promote_When_Retry_Window_Is_Not_Due()
        {
            var now = DateTime.UtcNow;
            var nextRetryAtUtc = now.AddSeconds(10);
            var step = CreateStep();
            step.Status = AiStepExecutionStatus.WaitingForRetry;
            step.RetryState = new AiStepRetryState
            {
                NextRetryAtUtc = nextRetryAtUtc
            };

            step.PromoteRetryToReadyIfDue(now);

            Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
            Assert.Equal(nextRetryAtUtc, step.RetryState.NextRetryAtUtc);
        }

        // ---------------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------------

        private static AiStepState CreateStep()
        {
            return new AiStepState
            {
                StepName = "step-1",
                Status = AiStepExecutionStatus.Ready
            };
        }

        private static AiStepState CreateRunningStep()
        {
            var step = CreateStep();
            step.ClaimTimeoutSeconds = 30;
            step.MarkRunning("worker-1", "token-1");
            return step;
        }
    }
}
