using Multiplexed.Abstractions.Core.ExecutionContext;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    /// <summary>
    /// Application-owned durable approval state. This state is committed before
    /// the runtime step returns AiStepResult.Park().
    /// </summary>
    public sealed class RuntimeAnalysisHumanApprovalRecord
    {
        public required string ExecutionId { get; init; }

        public required string StepName { get; init; }

        public required string ContinuationId { get; init; }

        public string? InitialRunId { get; init; }

        public required string Status { get; init; }

        public required RuntimeAnalysisScenarioPolicyValidationResult
            PolicyValidation { get; init; }

        public required ExecutionContextSnapshot ExecutionContextSnapshot
        {
            get;
            init;
        }

        public required DateTimeOffset RequestedAtUtc { get; init; }

        public DateTimeOffset? DecidedAtUtc { get; init; }

        public string? DecidedBy { get; init; }
    }
}
