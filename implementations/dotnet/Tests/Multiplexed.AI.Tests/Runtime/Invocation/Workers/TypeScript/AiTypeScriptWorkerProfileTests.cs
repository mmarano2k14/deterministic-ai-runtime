using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.TypeScript;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>Host profile construction without requiring an installed Node.js runtime.</summary>
    public sealed class AiTypeScriptWorkerProfileTests
    {
        private static AiPublicationEnvironment Runtime(string version = "22.16.0", string language = "typescript") =>
            new("typescript-node-fixed", language, version, new string('a', 64));
        private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "typescript-profile-fixture"));
        private static string Script => Path.Combine(Root, "worker.mjs");
        private static AiWorkerProcessProfile Profile(AiPublicationEnvironment? runtime = null,
            int heartbeat = 1000, IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? files = null) => AiTypeScriptWorkerProcessProfile.Create(
                runtime ?? Runtime(), Path.Combine(Root, "node.exe"), new string('b', 64),
                Script, new string('c', 64), Root, heartbeat, environment, files);

        [Theory]
        [InlineData("22.13.0")]
        [InlineData("22.16.0")]
        [InlineData("24.0.0")]
        [InlineData("24.9.1")]
        public void Profile_Uses_Exact_Runtime_And_Host_Controlled_Arguments(string version)
        {
            var runtime = Runtime(version);
            var profile = Profile(runtime);
            Assert.Equal(runtime, profile.Runtime);
            Assert.Equal(new[] { "--no-warnings", "--experimental-strip-types", "--experimental-transform-types", Script,
                "--runtime-reference=typescript-node-fixed", "--runtime-version=" + version,
                "--runtime-sha256=" + new string('a', 64), "--heartbeat-ms=1000" }, profile.Arguments);
            Assert.Equal(new string('c', 64), profile.VerifiedHostFiles[Script]);
            Assert.Empty(profile.Environment);
        }

        [Theory]
        [InlineData("22.12.0")]
        [InlineData("23.11.0")]
        [InlineData("25.0.0")]
        [InlineData("22.16")]
        [InlineData("22.16.0.0")]
        [InlineData("latest")]
        public void Unsupported_Or_Nonexact_Runtime_Versions_Are_Refused(string version) =>
            Assert.Throws<ArgumentException>(() => Profile(Runtime(version)));

        [Theory]
        [InlineData("dotnet")]
        [InlineData("python")]
        [InlineData("TypeScript")]
        public void Other_Languages_Do_Not_Fall_Back_To_TypeScript(string language) =>
            Assert.Throws<NotSupportedException>(() => Profile(Runtime(language: language)));

        [Theory]
        [InlineData(49)]
        [InlineData(5001)]
        public void Heartbeat_Interval_Is_Bounded(int milliseconds) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => Profile(heartbeat: milliseconds));

        [Fact]
        public void Configuration_Is_Copied_And_Inherited_Environment_Is_Not_Added()
        {
            var environment = new Dictionary<string, string> { ["TEST_EXPLICIT"] = "original" };
            var extra = Path.Combine(Root, "runtime.manifest");
            var files = new Dictionary<string, string> { [extra] = new string('d', 64) };
            var profile = Profile(environment: environment, files: files);
            environment["TEST_EXPLICIT"] = "changed";
            files.Clear();
            Assert.Equal("original", profile.Environment["TEST_EXPLICIT"]);
            Assert.Equal(new string('d', 64), profile.VerifiedHostFiles[extra]);
            var start = AiWorkerProcessTransport.CreateStartInfo(profile);
            Assert.False(start.UseShellExecute);
            Assert.Single(start.Environment);
            Assert.Equal("original", start.Environment["TEST_EXPLICIT"]);
        }

        [Fact]
        public void Conflicting_Loader_Digest_Is_Refused() => Assert.Throws<ArgumentException>(() =>
            Profile(files: new Dictionary<string, string> { [Script] = new string('d', 64) }));

        [Theory]
        [InlineData("worker.mjs")]
        [InlineData("")]
        public void Relative_Or_Missing_Loader_Path_Is_Refused(string path) => Assert.Throws<ArgumentException>(() =>
            AiTypeScriptWorkerProcessProfile.Create(Runtime(), Path.Combine(Root, "node.exe"), new string('b', 64),
                path, new string('c', 64), Root));

        [Fact]
        public void Loader_Digest_Must_Be_A_Real_Lowercase_Hash() => Assert.Throws<InvalidOperationException>(() =>
            AiTypeScriptWorkerProcessProfile.Create(Runtime(), Path.Combine(Root, "node.exe"), new string('b', 64),
                Script, "invalid", Root));

        [Fact]
        public void Profile_Construction_Performs_No_Process_Startup_Or_File_Read()
        {
            var profile = Profile();
            Assert.Equal(Script, profile.Arguments[3]);
            Assert.Equal(Root, profile.WorkingDirectory);
        }
    }
}
