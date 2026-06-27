using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime execution recovery transition service used by reconciler tests.
    /// </summary>
    public sealed class FakeRuntimeExecutionRecoveryTransitionService : IAiRuntimeExecutionRecoveryTransitionService
    {
        /// <summary>
        /// Gets or sets the transition result.
        /// </summary>
        /// <summary>
        /// Gets or sets the transition result.
        /// </summary>
        public AiRuntimeExecutionRecoveryTransitionResult Result { get; set; } = new AiRuntimeExecutionRecoveryTransitionResult
        {
            Accepted = false,
            Changed = false,
            RuntimeInstanceId = "runtime-test",
            LocalRunId = null,
            ExecutionId = null,
            SharedRunId = null,
            Action = "none",
            Reason = "not-configured"
        };

        /// <summary>
        /// Gets the number of apply calls.
        /// </summary>
        public int ApplyCalls { get; private set; }

        /// <summary>
        /// Gets the last transition request.
        /// </summary>
        public AiRuntimeExecutionRecoveryTransitionRequest? LastRequest { get; private set; }

        /// <inheritdoc />
        public Task<AiRuntimeExecutionRecoveryTransitionResult> ApplyAsync(
            AiRuntimeExecutionRecoveryTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            LastRequest = request;

            return Task.FromResult(Result);
        }
    }
}
