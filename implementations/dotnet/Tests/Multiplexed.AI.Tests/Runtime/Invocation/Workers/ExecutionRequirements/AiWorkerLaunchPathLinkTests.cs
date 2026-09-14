using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Real filesystem-link checks. Missing link-creation privileges are reported as skips, never passing coverage.</summary>
    public sealed class AiWorkerLaunchPathLinkTests
    {
        [ExecutionPathLinkFact]
        public void A_Linked_Launch_File_Is_Rejected()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var link = Path.Combine(fixture.Root, "host-link");
            File.CreateSymbolicLink(link, fixture.Executable);
            Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(fixture.Profile(executable: link)));
        }

        [ExecutionPathLinkFact]
        public void A_Linked_Ancestor_Cannot_Escape_The_Approved_Root()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            using var outside = new ExecutionRequirementsTestSupport.LaunchFixture();
            var link = Path.Combine(fixture.Root, "linked-directory");
            Directory.CreateSymbolicLink(link, outside.Root);
            try
            {
                var executable = Path.Combine(link, Path.GetFileName(outside.Executable));
                Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(fixture.Profile(executable: executable)));
            }
            finally { Directory.Delete(link); }
        }

        [ExecutionPathLinkFact]
        public void An_Approved_Root_Cannot_Itself_Be_A_Link()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var directory = Path.Combine(fixture.Root, "physical"); Directory.CreateDirectory(directory);
            var link = Path.Combine(fixture.Root, "alias"); Directory.CreateSymbolicLink(link, directory);
            try
            {
                Assert.Throws<InvalidOperationException>(() => AiWorkerLaunchPaths.ValidateProfile(fixture.Profile(roots: new[] { link })));
            }
            finally { Directory.Delete(link); }
        }
    }

    public sealed class ExecutionPathLinkFactAttribute : FactAttribute
    {
        public ExecutionPathLinkFactAttribute()
        {
            var root = Path.Combine(Path.GetTempPath(), "execution-link-capability-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var target = Path.Combine(root, "target"); File.WriteAllText(target, "probe");
                var link = Path.Combine(root, "link"); File.CreateSymbolicLink(link, target); File.Delete(link);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Skip = "Filesystem-link creation is unavailable: " + error.GetType().Name +
                    ". Run this gate on a host with symbolic-link privileges.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
