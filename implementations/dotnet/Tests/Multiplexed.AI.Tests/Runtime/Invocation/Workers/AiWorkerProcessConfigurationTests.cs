using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Trusted launch profiles are independent of submitted code and cannot inherit the engine environment.</summary>
    public sealed class AiWorkerProcessConfigurationTests
    {
        private static AiWorkerProcessProfile Profile(AiPublicationEnvironment? runtime = null,
            IReadOnlyDictionary<string, string>? environment = null, IEnumerable<string>? arguments = null) =>
            new(runtime ?? PublicationTestSupport.Environment("python"), Path.GetFullPath("worker-host"), new string('a', 64),
                arguments ?? Array.Empty<string>(), Path.GetTempPath(), environment);
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public void Exact_Host_Runtime_Is_Resolved_Without_Consulting_A_Pipeline_Default(string language)
        {
            var profile = Profile(PublicationTestSupport.Environment(language));
            var catalog = new AiConfiguredWorkerProcessCatalog(new[] { profile });
            Assert.Same(profile, catalog.Resolve(profile.Runtime));
        }
        [Theory]
        [InlineData("reference")]
        [InlineData("language")]
        [InlineData("version")]
        [InlineData("hash")]
        public void Changed_Host_Runtime_Identity_Has_No_Fallback(string field)
        {
            var profile = Profile(); var catalog = new AiConfiguredWorkerProcessCatalog(new[] { profile });
            var changed = field switch
            {
                "reference" => profile.Runtime with { Reference = "other-runtime" },
                "language" => profile.Runtime with { ExecutionLanguage = "dotnet" },
                "version" => profile.Runtime with { RuntimeVersion = "2.0.0" },
                _ => profile.Runtime with { RuntimeSha256 = new string('b', 64) }
            };
            Assert.Throws<NotSupportedException>(() => catalog.Resolve(changed));
        }
        [Fact]
        public void Launch_Uses_Private_Pipes_No_Shell_And_Only_Explicit_Environment()
        {
            var info = AiWorkerProcessTransport.CreateStartInfo(Profile(environment: new Dictionary<string, string> { ["EXPLICIT"] = "yes" }));
            Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
            Assert.True(info.RedirectStandardInput); Assert.True(info.RedirectStandardOutput); Assert.True(info.RedirectStandardError);
            Assert.Single(info.Environment); Assert.Equal("yes", info.Environment["EXPLICIT"]);
            Assert.DoesNotContain("PATH", info.Environment.Keys);
        }
        [Fact]
        public void Arguments_Are_Literal_Entries_Not_Concatenated_Shell_Text()
        {
            var arguments = new[] { "a b", "$(unsafe)", "a; b", "\"quoted\"" };
            var info = AiWorkerProcessTransport.CreateStartInfo(Profile(arguments: arguments));
            Assert.Equal(arguments, info.ArgumentList.ToArray()); Assert.Equal(string.Empty, info.Arguments);
        }
        [Fact]
        public void Configuration_Collections_Are_Defensively_Copied()
        {
            var env = new Dictionary<string, string> { ["key"] = "initial" }; var args = new[] { "original" };
            var profile = Profile(environment: env, arguments: args); env["key"] = "changed"; args[0] = "changed";
            Assert.Equal("initial", profile.Environment["key"]); Assert.Equal("original", profile.Arguments[0]);
        }
        [Fact]
        public void Duplicate_Runtime_Registration_Is_Refused()
        { var profile = Profile(); Assert.Throws<ArgumentException>(() => new AiConfiguredWorkerProcessCatalog(new[] { profile, profile })); }
        [Theory]
        [InlineData("BAD=NAME")]
        [InlineData("")]
        [InlineData("BAD\0NAME")]
        public void Invalid_Environment_Names_Are_Refused(string key)
        { Assert.Throws<ArgumentException>(() => Profile(environment: new Dictionary<string, string> { [key] = "value" })); }
        [Fact]
        public void Relative_Executable_Is_Refused()
        {
            Assert.Throws<ArgumentException>(() => new AiWorkerProcessProfile(PublicationTestSupport.Environment("python"),
                "relative-worker", new string('a', 64), Array.Empty<string>(), Path.GetTempPath()));
        }
        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        public void Launch_Capacity_Is_Bounded(int count)
        { Assert.Throws<ArgumentOutOfRangeException>(() => new AiWorkerSupervisionOptions(maxConcurrentProcesses: count)); }
        [Fact]
        public void Expired_Reassignment_Is_Disabled_By_Default()
        { Assert.False(new AiWorkerSupervisionOptions().AllowExpiredLeaseReassignment); }
        [Fact]
        public void Unsafe_Renewal_Schedule_Is_Refused()
        { Assert.Throws<ArgumentException>(() => new AiWorkerSupervisionOptions(renewalInterval: TimeSpan.FromSeconds(29))); }
        [Fact]
        public void Quarantined_Slot_Is_Not_Returned_To_The_Launch_Pool()
        {
            using var capacity = new AiWorkerProcessCapacity(new(maxConcurrentProcesses: 1));
            using (var first = capacity.TryEnter()) { Assert.NotNull(first); first.Quarantine(); Assert.Null(capacity.TryEnter()); }
            Assert.Equal(0, capacity.Available); Assert.Equal(1, capacity.Quarantined); Assert.Null(capacity.TryEnter());
        }
        [Fact]
        public void Slot_Disposal_Is_Idempotent()
        {
            using var capacity = new AiWorkerProcessCapacity(new(maxConcurrentProcesses: 1));
            var slot = capacity.TryEnter()!; slot.Dispose(); slot.Dispose(); Assert.Equal(1, capacity.Available);
        }
    }
}
