using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Pipeline.Steps.Control
{
    /// <summary>
    /// Publishes one explicitly selected resolved runtime value into execution-level durable state.
    /// </summary>
    /// <remarks>
    /// The source is an explicit runtime path. The step fails closed when that path cannot be
    /// resolved so an unresolved binding string cannot become a public/business result.
    /// </remarks>
    [AiStep(StepKey)]
    public sealed class PublishExecutionResultStep : IAiStep
    {
        /// <summary>The canonical native step key.</summary>
        public const string StepKey = "execution.publish-result";

        /// <summary>The required strict runtime path configuration key.</summary>
        public const string SourceConfigKey = "source";

        /// <summary>The optional configuration key overriding the execution-state result key.</summary>
        public const string ResultKeyConfigKey = "resultKey";

        /// <inheritdoc />
        public string Name => StepKey;

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var source = ReadRequiredSourcePath(context);
            var resolver = context.GetRequiredService<IAiContextValueResolver>();
            var value = await resolver
                .ResolveRequiredPathAsync<object>(context, source, cancellationToken)
                .ConfigureAwait(false);

            var helper = context.GetHelper();
            var resultKey = await helper
                .GetConfigAsync<string>(ResultKeyConfigKey, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resultKey))
            {
                resultKey = AiExecutionKeys.Result;
            }

            var writer = context.GetRequiredService<IAiExecutionStateWriter>();
            writer.SetData(context.State, resultKey, value);

            return AiStepResult.Ok(
                value: value,
                output: "Execution result published.",
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["resultKey"] = resultKey,
                    ["source"] = source
                });
        }

        private static string ReadRequiredSourcePath(AiStepExecutionContext context)
        {
            if (!context.StepState.Config.TryGetValue(SourceConfigKey, out var rawSource))
            {
                throw new InvalidOperationException(
                    $"Required config '{SourceConfigKey}' is missing for step '{context.StepName}'.");
            }

            var source = rawSource switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException(
                    $"Required config '{SourceConfigKey}' must contain a non-empty runtime path for step '{context.StepName}'.");
            }

            return source;
        }
    }
}
