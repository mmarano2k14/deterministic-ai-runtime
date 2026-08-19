using System.Text.Json;
using Multiplexed.Abstractions.AI.Steps;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Execution
{
    /// <summary>
    /// Validates explicit step execution outcomes and legacy compatibility mapping.
    /// </summary>
    public sealed class AiStepResultOutcomeTests
    {
        [Fact]
        public void Park_Should_Produce_NonTerminal_Explicit_Park_Outcome()
        {
            var result = AiStepResult.Park("waiting");

            Assert.False(result.Success);
            Assert.Equal(AiStepExecutionOutcome.Park, result.Outcome);
            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            Assert.Null(result.Error);
        }

        [Theory]
        [InlineData(true, AiStepExecutionOutcome.Complete)]
        [InlineData(false, AiStepExecutionOutcome.Fail)]
        public void EffectiveOutcome_Should_Map_Legacy_Result_When_Outcome_Is_Absent(
            bool success,
            AiStepExecutionOutcome expectedOutcome)
        {
            var json = $"{{\"Success\":{success.ToString().ToLowerInvariant()}}}";
            var result = JsonSerializer.Deserialize<AiStepResult>(json);

            Assert.NotNull(result);
            Assert.Null(result!.Outcome);
            Assert.Equal(expectedOutcome, result.EffectiveOutcome);
        }

        [Fact]
        public void Legacy_Result_With_Absent_Outcome_Should_Not_Add_Null_Outcome_When_Reserialized()
        {
            const string json = "{\"Success\":true}";
            var result = JsonSerializer.Deserialize<AiStepResult>(json);

            Assert.NotNull(result);

            var roundTrip = JsonSerializer.Serialize(result);

            Assert.DoesNotContain("\"Outcome\"", roundTrip);
        }
    }
}
