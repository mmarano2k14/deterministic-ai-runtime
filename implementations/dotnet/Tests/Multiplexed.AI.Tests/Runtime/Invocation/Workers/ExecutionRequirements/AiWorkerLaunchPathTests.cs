using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Portable closure names and approved host launch roots are validated without claiming OS-level sealing.</summary>
    public sealed class AiWorkerLaunchPathTests
    {
        [Theory]
        [InlineData("../escape.py")]
        [InlineData("/absolute.py")]
        [InlineData("C:/absolute.py")]
        [InlineData("a/../escape.py")]
        [InlineData("a\\escape.py")]
        [InlineData("./main.py")]
        [InlineData("CON.py")]
        [InlineData("a//main.py")]
        public void Untrusted_Code_Paths_Are_Rejected(string path)
        {
            var code = WorkerTestSupport.Bundle();
            code = code with { EntryPointPath = path, Sources = new[] { code.Sources[0] with { Path = path } } };
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateCodePaths(code));
        }

        [Theory]
        [InlineData("main.py", "MAIN.py")]
        [InlineData("main.py", "main.py")]
        [InlineData("Package/a.py", "package/b.py")]
        [InlineData("package", "package/main.py")]
        public void Case_Aliases_And_File_Directory_Collisions_Are_Rejected(string first, string second)
        {
            var code = WorkerTestSupport.Bundle();
            code = code with { EntryPointPath = first,
                Sources = new[] { code.Sources[0] with { Path = first }, code.Sources[0] with { Path = second } } };
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateCodePaths(code));
        }

        [Fact]
        public void Dependencies_Cannot_Escape_Their_Validated_File_Closure()
        {
            var code = WorkerTestSupport.Bundle();
            code = code with { Dependencies = new[] { new AiWorkerDependency("rules", "1.0.0",
                new[] { code.Sources[0] with { Path = "../escape" } }) } };
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateCodePaths(code));
        }

        [Fact]
        public void Entry_Point_Must_Belong_To_The_Exact_Source_List()
        {
            var code = WorkerTestSupport.Bundle() with { EntryPointPath = "other.txt" };
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateCodePaths(code));
        }

        [Fact]
        public void Approved_Roots_Are_Copied_From_Server_Configuration()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var roots = new[] { fixture.Root }; var profile = fixture.Profile(roots: roots);
            roots[0] = Path.GetPathRoot(fixture.Root)!;
            Assert.Equal(fixture.Root, Assert.Single(profile.ApprovedLaunchRoots));
            AiWorkerLaunchPaths.ValidateProfile(profile);
        }

        [Fact]
        public void A_Sibling_Directory_Is_Not_Inside_An_Approved_Root()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var approved = Path.Combine(fixture.Root, "approved"); var sibling = Path.Combine(fixture.Root, "approved-extra");
            Directory.CreateDirectory(approved); Directory.CreateDirectory(sibling);
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(
                fixture.Profile(directory: sibling, roots: new[] { approved })));
        }

        [Fact]
        public void Dot_Segments_In_Host_Paths_Are_Refused_Instead_Of_Normalized_Away()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var alias = Path.Combine(fixture.Root, ".", Path.GetFileName(fixture.Executable));
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(fixture.Profile(executable: alias)));
        }

        [Fact]
        public void A_Drive_Or_Filesystem_Root_Is_Not_An_Approved_Launch_Root()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(
                fixture.Profile(roots: new[] { Path.GetPathRoot(fixture.Root)! })));
        }

        [Fact]
        public async Task Changed_Approved_Launch_Bytes_Are_Refused_Before_Process_Start()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            File.AppendAllText(fixture.Loader, "changed");
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { profile }), new());
            var heartbeats = 0;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.InvokeAsync(fixture.Request(profile),
                _ => { heartbeats++; return Task.CompletedTask; }));
            Assert.Contains("approved digest", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, heartbeats);
        }
    }
}
