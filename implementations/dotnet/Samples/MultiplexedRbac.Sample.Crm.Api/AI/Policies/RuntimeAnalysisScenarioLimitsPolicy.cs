using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Policies
{
    public sealed class RuntimeAnalysisScenarioLimitsPolicy :
        AiPolicyBase<RuntimeAnalysisScenarioPolicyContext>
    {
        public override string Key =>
            RuntimeAnalysisScenarioPolicyKeys.Limits;

        public override AiPolicyKind Kind =>
            AiPolicyKind.Validation;

        public override Task<AiPolicyResult> ExecuteAsync(
            RuntimeAnalysisScenarioPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            cancellationToken.ThrowIfCancellationRequested();

            var minimumTotalRequests =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "minTotalRequests");

            var maximumTotalRequests =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxTotalRequests");

            var minimumMaxInFlight =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "minMaxInFlight");

            var maximumMaxInFlight =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxMaxInFlight");

            var maximumConcurrency =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxConcurrency");

            var maximumBatchSize =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxBatchSize");

            var maximumDelayMs =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxDelayMs");

            var maximumWavePauseMs =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxWavePauseMs");

            var maximumRotationOverlapMs =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxRotationOverlapMs");

            var maximumDurationSeconds =
                context.GetRequiredPolicyConfig<int>(
                    Key,
                    "maxDurationSeconds");

            var scenario = context.Scenario;

            if (scenario.TotalRequests < minimumTotalRequests
                || scenario.TotalRequests > maximumTotalRequests)
            {
                return Block(
                    $"TotalRequests must be between {minimumTotalRequests} and {maximumTotalRequests}.");
            }

            if (scenario.MaxInFlight < minimumMaxInFlight
                || scenario.MaxInFlight > maximumMaxInFlight)
            {
                return Block(
                    $"MaxInFlight must be between {minimumMaxInFlight} and {maximumMaxInFlight}.");
            }

            if (scenario.Concurrency is < 0
                || scenario.Concurrency > maximumConcurrency)
            {
                return Block(
                    $"Concurrency cannot exceed {maximumConcurrency}.");
            }

            if (scenario.BatchSize is < 0
                || scenario.BatchSize > maximumBatchSize)
            {
                return Block(
                    $"BatchSize cannot exceed {maximumBatchSize}.");
            }

            if (scenario.DelayMs < 0
                || scenario.DelayMs > maximumDelayMs)
            {
                return Block(
                    $"DelayMs must be between 0 and {maximumDelayMs}.");
            }

            if (scenario.WavePauseMs is < 0
                || scenario.WavePauseMs > maximumWavePauseMs)
            {
                return Block(
                    $"WavePauseMs cannot exceed {maximumWavePauseMs}.");
            }

            if (scenario.RotationOverlapMs < 0
                || scenario.RotationOverlapMs > maximumRotationOverlapMs)
            {
                return Block(
                    $"RotationOverlapMs must be between 0 and {maximumRotationOverlapMs}.");
            }

            if (scenario.DurationSeconds is < 0
                || scenario.DurationSeconds > maximumDurationSeconds)
            {
                return Block(
                    $"DurationSeconds cannot exceed {maximumDurationSeconds}.");
            }

            return Task.FromResult(
                AiPolicyResult.Success(
                    $"Suggested scenario is inside the pipeline-configured execution limits (requests <= {maximumTotalRequests}, max-in-flight <= {maximumMaxInFlight})."));
        }

        private static Task<AiPolicyResult> Block(
            string message)
        {
            return Task.FromResult(
                AiPolicyResult.Block(
                    message));
        }
    }
}
