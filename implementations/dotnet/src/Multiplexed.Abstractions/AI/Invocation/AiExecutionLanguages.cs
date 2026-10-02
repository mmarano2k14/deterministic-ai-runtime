namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>Canonical language tokens recognized by the publication and hosted-invocation contracts.</summary>
    public static class AiExecutionLanguages
    {
        public const string DotNet = "dotnet";
        public const string Python = "python";
        public const string TypeScript = "typescript";

        private static readonly IReadOnlyList<string> Supported =
            Array.AsReadOnly(new[] { DotNet, Python, TypeScript });

        /// <summary>All canonical execution-language tokens supported by the current hosted runtime.</summary>
        public static IReadOnlyList<string> All => Supported;

        /// <summary>Returns true only for an exact canonical execution-language token.</summary>
        public static bool IsSupported(string? language) =>
            language is DotNet or Python or TypeScript;
    }
}
