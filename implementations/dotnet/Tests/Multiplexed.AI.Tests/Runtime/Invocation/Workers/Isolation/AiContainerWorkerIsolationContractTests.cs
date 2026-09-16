using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Freezes the isolated-provider capability and immutable-profile boundary without claiming
    /// container enforcement before the transport implementation exists.
    /// </summary>
    public sealed class AiContainerWorkerIsolationContractTests
    {
        [Fact]
        public void Initial_Provider_Capabilities_Are_Explicit_And_Do_Not_Alter_Process_Capabilities()
        {
            Assert.Equal(AiPublicationEnvironmentArtifactKind.OciImage, AiContainerWorkerExecutionAdmission.Capabilities.ArtifactKind);
            Assert.Equal(AiWorkerIsolationTier.SandboxedContainer, AiContainerWorkerExecutionAdmission.Capabilities.IsolationTier);
            Assert.Equal(AiWorkerNetworkEgress.DenyAll, AiContainerWorkerExecutionAdmission.Capabilities.NetworkEgress);
            Assert.Equal(AiWorkerPathProtection.SealedClosure, AiContainerWorkerExecutionAdmission.Capabilities.PathProtection);

            Assert.Equal(AiPublicationEnvironmentArtifactKind.HostRuntime, AiWorkerExecutionAdmission.ProcessCapabilities.ArtifactKind);
            Assert.Equal(AiWorkerIsolationTier.TrustedProcess, AiWorkerExecutionAdmission.ProcessCapabilities.IsolationTier);
            Assert.Equal(AiWorkerNetworkEgress.HostNetwork, AiWorkerExecutionAdmission.ProcessCapabilities.NetworkEgress);
            Assert.Equal(AiWorkerPathProtection.ValidatedPaths, AiWorkerExecutionAdmission.ProcessCapabilities.PathProtection);
        }

        [Fact]
        public void Exact_Linux_Amd64_Oci_Profile_Is_Admitted()
        {
            var profile = Profile();
            AiContainerWorkerExecutionAdmission.Require(Code(profile), profile, new AiWorkerExecutionAdmissionPolicy());
        }

        [Fact]
        public void Image_Reference_Uses_The_Publication_Manifest_Digest_Not_RuntimeSha256()
        {
            var profile = Profile();
            Assert.Equal(profile.ImageRepository + "@" + profile.ExecutionDescriptor.Artifact.Digest, profile.ImageReference);
            Assert.NotEqual("sha256:" + profile.Runtime.RuntimeSha256, profile.ExecutionDescriptor.Artifact.Digest);
        }

        [Theory]
        [InlineData("isolation")]
        [InlineData("egress")]
        [InlineData("pinned-egress")]
        [InlineData("paths")]
        public void Unsupported_Requirement_Combinations_Fail_Closed(string variant)
        {
            var requirements = variant switch
            {
                "isolation" => new AiPublicationExecutionRequirements
                {
                    IsolationTier = AiWorkerIsolationTier.TrustedProcess,
                    NetworkEgress = AiWorkerNetworkEgress.DenyAll,
                    PathProtection = AiWorkerPathProtection.SealedClosure
                },
                "egress" => new AiPublicationExecutionRequirements
                {
                    IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
                    NetworkEgress = AiWorkerNetworkEgress.HostNetwork,
                    PathProtection = AiWorkerPathProtection.SealedClosure
                },
                "pinned-egress" => new AiPublicationExecutionRequirements
                {
                    IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
                    NetworkEgress = AiWorkerNetworkEgress.PinnedPolicy,
                    PathProtection = AiWorkerPathProtection.SealedClosure,
                    EgressPolicyDigest = "sha256:" + new string('c', 64)
                },
                _ => new AiPublicationExecutionRequirements
                {
                    IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
                    NetworkEgress = AiWorkerNetworkEgress.DenyAll,
                    PathProtection = AiWorkerPathProtection.ValidatedPaths
                }
            };
            var profile = Profile(descriptor: Descriptor(requirements: requirements));
            Assert.Throws<NotSupportedException>(() =>
                AiContainerWorkerExecutionAdmission.Require(Code(profile), profile, new AiWorkerExecutionAdmissionPolicy()));
        }

        [Theory]
        [InlineData("windows", "amd64", null)]
        [InlineData("linux", "arm64", null)]
        [InlineData("linux", "amd64", "v2")]
        public void Initial_Provider_Refuses_Unselected_Platforms(string operatingSystem, string architecture, string? variant)
        {
            var descriptor = Descriptor() with
            {
                OperatingSystem = operatingSystem,
                Architecture = architecture,
                PlatformVariant = variant
            };
            var profile = Profile(descriptor: descriptor);
            Assert.Throws<NotSupportedException>(() =>
                AiContainerWorkerExecutionAdmission.Require(Code(profile), profile, new AiWorkerExecutionAdmissionPolicy()));
        }

        [Fact]
        public void HostRuntime_Descriptor_Cannot_Be_Configured_As_Container_Profile()
        {
            var runtime = Runtime();
            var descriptor = new AiPublicationExecutionDescriptor
            {
                OperatingSystem = "linux",
                Architecture = "amd64",
                Artifact = new(AiPublicationEnvironmentArtifactKind.HostRuntime,
                    "sha256:" + runtime.RuntimeSha256, AiPublicationExecutionDescriptors.HostRuntimeMediaType),
                Requirements = new()
                {
                    IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
                    NetworkEgress = AiWorkerNetworkEgress.DenyAll,
                    PathProtection = AiWorkerPathProtection.SealedClosure
                }
            };
            Assert.Throws<ArgumentException>(() => Profile(runtime, descriptor));
        }

        [Theory]
        [InlineData("registry.example.com/multiplexed/python:latest")]
        [InlineData("registry.example.com/multiplexed/python@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("Registry.Example.com/multiplexed/python")]
        [InlineData("registry.example.com/multiplexed/python worker")]
        public void Mutable_Or_Ambiguous_Image_Repository_Is_Refused(string repository)
        {
            Assert.Throws<ArgumentException>(() => Profile(imageRepository: repository));
        }

        [Theory]
        [InlineData("0:65532")]
        [InlineData("65532:0")]
        [InlineData("root:root")]
        [InlineData("65532")]
        public void Container_User_Must_Be_Numeric_And_NonRoot(string user)
        {
            Assert.Throws<ArgumentException>(() => Profile(containerUser: user));
        }

        [Theory]
        [InlineData(0, 268435456L, 64, 67108864L)]
        [InlineData(1000, 1024L, 64, 67108864L)]
        [InlineData(1000, 268435456L, 1, 67108864L)]
        [InlineData(1000, 268435456L, 64, 536870912L)]
        public void Resource_Limits_Are_Bounded(int cpu, long memory, int pids, long workspace)
        {
            var limits = new AiContainerWorkerResourceLimits
            {
                CpuMilliCores = cpu,
                MemoryBytes = memory,
                PidsLimit = pids,
                WritableWorkspaceBytes = workspace
            };
            Assert.Throws<ArgumentOutOfRangeException>(() => Profile(limits: limits));
        }

        [Fact]
        public void Profile_Requires_Approved_Host_Launch_Roots()
        {
            Assert.Throws<ArgumentException>(() => Profile(approvedRoots: Array.Empty<string>()));
        }

        [Fact]
        public void Engine_Path_Must_Remain_Inside_Approved_Host_Root()
        {
            var root = Root();
            var outside = Path.Combine(Path.GetTempPath(), "outside-container-engine", EngineName());
            Assert.Throws<ArgumentException>(() => Profile(enginePath: outside, approvedRoots: new[] { root }));
        }

        [Fact]
        public void Engine_Environment_Is_Explicit_And_Defensively_Copied()
        {
            var environment = new Dictionary<string, string> { ["DOCKER_HOST"] = "unix:///run/docker.sock" };
            var profile = Profile(environment: environment);
            environment["DOCKER_HOST"] = "tcp://untrusted";
            Assert.Equal("unix:///run/docker.sock", profile.EngineEnvironment["DOCKER_HOST"]);
        }

        [Fact]
        public void Invalid_Engine_Environment_Is_Refused()
        {
            Assert.Throws<ArgumentException>(() => Profile(
                environment: new Dictionary<string, string> { ["BAD=KEY"] = "value" }));
        }

        [Fact]
        public void Catalog_Requires_Exact_Pinned_Runtime()
        {
            var profile = Profile();
            var catalog = new AiConfiguredContainerWorkerCatalog(new[] { profile });
            Assert.Same(profile, catalog.Resolve(profile.Runtime));
            Assert.Throws<NotSupportedException>(() =>
                catalog.Resolve(profile.Runtime with { RuntimeVersion = profile.Runtime.RuntimeVersion + ".1" }));
        }

        [Fact]
        public void Catalog_Rejects_Duplicate_Runtime_Profiles()
        {
            var profile = Profile();
            Assert.Throws<ArgumentException>(() =>
                new AiConfiguredContainerWorkerCatalog(new[] { profile, profile }));
        }

        [Fact]
        public void Mismatched_Code_Descriptor_Is_Refused_Before_Provider_Selection()
        {
            var profile = Profile();
            var code = Code(profile) with
            {
                ExecutionDescriptor = profile.ExecutionDescriptor with
                {
                    Artifact = profile.ExecutionDescriptor.Artifact with { Digest = "sha256:" + new string('d', 64) }
                }
            };
            Assert.Throws<InvalidOperationException>(() =>
                AiContainerWorkerExecutionAdmission.Require(code, profile, new AiWorkerExecutionAdmissionPolicy()));
        }

        [Fact]
        public void Legacy_Code_Without_Execution_Descriptor_Cannot_Enter_Isolated_Provider()
        {
            var profile = Profile();
            var code = Code(profile) with { ExecutionDescriptor = null };
            Assert.Throws<NotSupportedException>(() =>
                AiContainerWorkerExecutionAdmission.Require(code, profile, new AiWorkerExecutionAdmissionPolicy()));
        }

        [Fact]
        public void Server_Minimum_Cannot_Silently_Request_Trusted_Process_Compatibility()
        {
            var profile = Profile();
            Assert.Throws<NotSupportedException>(() =>
                AiContainerWorkerExecutionAdmission.Require(
                    Code(profile), profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible));
        }

        [Fact]
        public void Invalid_Execution_File_Closure_Is_Refused_At_Isolated_Admission()
        {
            var profile = Profile();
            var code = Code(profile) with
            {
                Sources = new[]
                {
                    new AiWorkerFile("../escape.py", new string('a', 64), 1, "eA")
                }
            };
            Assert.ThrowsAny<Exception>(() =>
                AiContainerWorkerExecutionAdmission.Require(code, profile, new AiWorkerExecutionAdmissionPolicy()));
        }

        private static AiPublicationEnvironment Runtime() =>
            PublicationTestSupport.Environment("python");

        private static AiPublicationExecutionDescriptor Descriptor(
            AiPublicationEnvironment? runtime = null,
            AiPublicationExecutionRequirements? requirements = null) => new()
            {
                OperatingSystem = "linux",
                Architecture = "amd64",
                Artifact = new(
                    AiPublicationEnvironmentArtifactKind.OciImage,
                    "sha256:" + new string('b', 64),
                    AiPublicationExecutionDescriptors.OciImageMediaType),
                Requirements = requirements ?? new AiPublicationExecutionRequirements()
            };

        private static AiContainerWorkerProfile Profile(
            AiPublicationEnvironment? runtime = null,
            AiPublicationExecutionDescriptor? descriptor = null,
            string imageRepository = "registry.example.com/multiplexed/python-worker",
            string containerUser = "65532:65532",
            AiContainerWorkerResourceLimits? limits = null,
            string? enginePath = null,
            IReadOnlyDictionary<string, string>? environment = null,
            IEnumerable<string>? approvedRoots = null)
        {
            runtime ??= Runtime();
            descriptor ??= Descriptor(runtime);
            var root = Root();
            return new(
                runtime,
                descriptor,
                enginePath ?? Path.Combine(root, EngineName()),
                new string('e', 64),
                root,
                imageRepository,
                limits,
                containerUser,
                environment,
                approvedRoots ?? new[] { root });
        }

        private static AiWorkerCodeBundle Code(AiContainerWorkerProfile profile) =>
            WorkerTestSupport.Bundle() with
            {
                Runtime = profile.Runtime,
                ExecutionDescriptor = profile.ExecutionDescriptor
            };

        private static string Root() =>
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "multiplexed-container-provider"));

        private static string EngineName() =>
            OperatingSystem.IsWindows() ? "docker.exe" : "docker";
    }
}
