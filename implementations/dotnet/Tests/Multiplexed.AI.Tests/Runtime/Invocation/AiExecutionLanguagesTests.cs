using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiExecutionLanguagesTests
    {
        [Fact]
        public void All_Contains_The_Canonical_Languages_Exactly_Once()
        {
            Assert.Equal(
                new[]
                {
                    AiExecutionLanguages.DotNet,
                    AiExecutionLanguages.Python,
                    AiExecutionLanguages.TypeScript
                },
                AiExecutionLanguages.All);
            Assert.Equal(AiExecutionLanguages.All.Count, AiExecutionLanguages.All.Distinct(StringComparer.Ordinal).Count());
        }

        [Theory]
        [InlineData(AiExecutionLanguages.DotNet)]
        [InlineData(AiExecutionLanguages.Python)]
        [InlineData(AiExecutionLanguages.TypeScript)]
        public void IsSupported_Accepts_Only_Canonical_Tokens(string language)
        {
            Assert.True(AiExecutionLanguages.IsSupported(language));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Python")]
        [InlineData("python ")]
        [InlineData("mcp")]
        [InlineData("java")]
        public void IsSupported_Rejects_NonCanonical_Tokens(string? language)
        {
            Assert.False(AiExecutionLanguages.IsSupported(language));
        }

        [Fact]
        public void All_Is_ReadOnly()
        {
            var list = Assert.IsAssignableFrom<IList<string>>(AiExecutionLanguages.All);
            Assert.Throws<NotSupportedException>(() => list.Add("java"));
        }

        [Fact]
        public void Worker_Polling_Accepts_The_Canonical_Language_Set()
        {
            var options = new AiWorkerPollingOptions(
                new[] { new AiDurableInvocationScope("tenant-a", "group-a", "control-a") },
                AiExecutionLanguages.All);

            Assert.Equal(AiExecutionLanguages.All, options.ExecutionLanguages);
        }
    }
}
