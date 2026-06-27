using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake shared run ownership resolver used by reconciler tests.
    /// </summary>
    public sealed class FakeSharedRunOwnershipResolver : IAiSharedRunOwnershipResolver
    {
        /// <summary>
        /// Gets or sets the ownership resolution result.
        /// </summary>
        /// <summary>
        /// Gets or sets the ownership resolution result.
        /// </summary>
        public AiSharedRunOwnershipResolutionResult Result { get; set; } = new AiSharedRunOwnershipResolutionResult
        {
            Resolved = false,
            RuntimeInstanceId = "runtime-test",
            LocalRunId = null,
            ExecutionId = null,
            SharedRunId = null,
            TenantId = null,
            TenantGroupId = null,
            QueueStatus = null,
            ClaimToken = null,
            CanRecover = false,
            Reason = "not-configured"
        };

        /// <summary>
        /// Gets the number of resolve calls.
        /// </summary>
        public int ResolveCalls { get; private set; }

        /// <summary>
        /// Gets the last ownership resolution request.
        /// </summary>
        public AiSharedRunOwnershipResolutionRequest? LastRequest { get; private set; }

        /// <inheritdoc />
        public Task<AiSharedRunOwnershipResolutionResult> ResolveAsync(
            AiSharedRunOwnershipResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            LastRequest = request;

            return Task.FromResult(Result);
        }
    }
}
