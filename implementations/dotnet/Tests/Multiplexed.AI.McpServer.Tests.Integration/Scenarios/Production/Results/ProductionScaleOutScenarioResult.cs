namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Contains the scale-out result observed for one shared run.
    /// </summary>
    public sealed record ProductionScaleOutScenarioResult
    {
        /// <summary>
        /// Gets the scale-out request id.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// Gets the shared run id linked to the scale-out request.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the tenant id linked to the scale-out request.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group id linked to the scale-out request.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the final scale-out request status.
        /// </summary>
        public required string Status { get; init; }

        /// <summary>
        /// Gets the runtime isolation mode requested for the scale-out operation.
        /// </summary>
        public string? IsolationMode { get; init; }

        /// <summary>
        /// Gets a value indicating whether dedicated runtime capacity was preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; init; }

        /// <summary>
        /// Gets a value indicating whether shared fallback capacity was allowed.
        /// </summary>
        public bool AllowSharedFallback { get; init; }

        /// <summary>
        /// Gets the runtime instance id prefix requested for the scale-out operation.
        /// </summary>
        public string? RuntimeInstanceIdPrefix { get; init; }

        /// <summary>
        /// Gets the requested worker count per runtime instance.
        /// </summary>
        public int? WorkerCountPerInstance { get; init; }

        /// <summary>
        /// Gets the requested maximum concurrent run count per runtime instance.
        /// </summary>
        public int? MaxConcurrentRunsPerInstance { get; init; }

        /// <summary>
        /// Gets the requested local queue capacity for the runtime instance.
        /// </summary>
        public int? LocalQueueCapacity { get; init; }

        /// <summary>
        /// Gets the fulfilled runtime instance id, when the request was fulfilled.
        /// </summary>
        public string? FulfilledRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the rejection reason, when the request was rejected.
        /// </summary>
        public string? RejectionReason { get; init; }

        /// <summary>
        /// Gets the fulfillment timestamp.
        /// </summary>
        public DateTimeOffset? FulfilledAtUtc { get; init; }

        /// <summary>
        /// Gets the rejection timestamp.
        /// </summary>
        public DateTimeOffset? RejectedAtUtc { get; init; }
    }
}