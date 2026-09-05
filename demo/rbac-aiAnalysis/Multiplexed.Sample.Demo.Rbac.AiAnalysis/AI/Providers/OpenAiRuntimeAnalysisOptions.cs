namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers
{
    public sealed class OpenAiRuntimeAnalysisOptions
    {
        public const string SectionName = "AI:OpenAI";

        public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        public string? ApiKey { get; set; }

        public string Model { get; set; } = "gpt-5.6-sol";

        public string Endpoint { get; set; } =
            "https://api.openai.com/v1/responses";

        public int MaxOutputTokens { get; set; } = 1800;
    }
}
