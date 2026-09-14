using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DotNet;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet
{
    /// <summary>Profile construction is deterministic and does not discover installed runtimes.</summary>
    public sealed class AiDotNetWorkerProfileTests
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "dotnet-profile-fixture"));
        private static AiPublicationEnvironment Runtime(string version = "10.0.4", string language = "dotnet") =>
            new("dotnet-fixed", language, version, new string('a', 64));
        private static AiWorkerProcessProfile Profile(AiPublicationEnvironment? runtime = null, int heartbeat = 1000) =>
            AiDotNetWorkerProcessProfile.Create(runtime ?? Runtime(), Path.Combine(Root, "dotnet.exe"), new string('b', 64),
                Path.Combine(Root, "worker.dll"), new string('c', 64), Path.Combine(Root, "worker.deps.json"), new string('d', 64),
                Path.Combine(Root, "worker.runtimeconfig.json"), new string('e', 64), Root, heartbeat);

        [Theory]
        [InlineData("10.0.0")]
        [InlineData("10.0.4")]
        [InlineData("10.0.12")]
        public void Profile_Uses_Exact_DotNet10_Runtime_And_Worker_Launch_Files(string version)
        {
            var profile = Profile(Runtime(version));
            Assert.Equal(version, profile.Runtime.RuntimeVersion);
            Assert.Equal(5, profile.Arguments.Count);
            Assert.EndsWith("worker.dll", profile.Arguments[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("--runtime-reference=dotnet-fixed", profile.Arguments[1]);
            Assert.Equal("--runtime-version=" + version, profile.Arguments[2]);
            Assert.Equal("--runtime-sha256=" + new string('a', 64), profile.Arguments[3]);
            Assert.Equal("--heartbeat-ms=1000", profile.Arguments[4]);
            Assert.Equal(3, profile.VerifiedHostFiles.Count);
        }

        [Theory]
        [InlineData("9.0.9")]
        [InlineData("11.0.0")]
        [InlineData("10.1.0")]
        [InlineData("10.0")]
        [InlineData("10.0.1.2")]
        public void Unsupported_Runtime_Version_Is_Refused(string version) =>
            Assert.Throws<ArgumentException>(() => Profile(Runtime(version)));

        [Fact]
        public void Another_Language_Is_Refused() => Assert.Throws<NotSupportedException>(() => Profile(Runtime(language: "python")));

        [Theory]
        [InlineData(49)]
        [InlineData(5001)]
        public void Invalid_Heartbeat_Is_Refused(int heartbeat) => Assert.Throws<ArgumentOutOfRangeException>(() => Profile(heartbeat: heartbeat));

        [Fact]
        public void Worker_Files_Are_Verified_And_Not_Tenant_Arguments()
        {
            var profile = Profile();
            Assert.Contains(profile.VerifiedHostFiles.Keys, path => path.EndsWith("worker.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(profile.Arguments, value => value.Contains("functions.dll", StringComparison.OrdinalIgnoreCase));
        }
    }
}
