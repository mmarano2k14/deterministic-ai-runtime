using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Rejects ambiguous identities and unknown requirements instead of interpreting them as legacy execution.</summary>
    public sealed class AiExecutionDescriptorValidationTests
    {
        [Fact]
        public void New_Requirements_Default_To_Denied_Egress_And_Sealed_Sandbox_Execution()
        {
            var requirements = new AiPublicationExecutionRequirements();
            Assert.Equal(AiWorkerNetworkEgress.DenyAll, requirements.NetworkEgress);
            Assert.Equal(AiWorkerIsolationTier.SandboxedContainer, requirements.IsolationTier);
            Assert.Equal(AiWorkerPathProtection.SealedClosure, requirements.PathProtection);
            Assert.False(new AiWorkerExecutionAdmissionPolicy().AllowLegacyTrustedProcess);
            Assert.Equal(requirements, new AiWorkerExecutionAdmissionPolicy().MinimumRequirements);
        }

        [Theory]
        [InlineData("hash-only")]
        [InlineData("upper-algorithm")]
        [InlineData("upper-hash")]
        [InlineData("short")]
        [InlineData("tag")]
        [InlineData("empty")]
        [InlineData("null")]
        public void Artifact_Digest_Has_A_Closed_Algorithm_Qualified_Form(string variant)
        {
            var digest = variant switch
            {
                "hash-only" => new string('a', 64), "upper-algorithm" => "SHA256:" + new string('a', 64),
                "upper-hash" => "sha256:" + new string('A', 64), "short" => "sha256:abcd", "tag" => "image:latest",
                "null" => null, _ => string.Empty
            };
            Assert.Throws<InvalidOperationException>(() => AiPublicationExecutionDescriptors.ValidateDigest(digest!));
        }

        [Theory]
        [InlineData("schema")]
        [InlineData("os")]
        [InlineData("architecture")]
        [InlineData("variant")]
        [InlineData("artifact-kind")]
        [InlineData("artifact-media-type")]
        [InlineData("runtime-digest")]
        [InlineData("isolation")]
        [InlineData("egress")]
        [InlineData("paths")]
        [InlineData("legacy-paths")]
        [InlineData("extra-policy-digest")]
        [InlineData("missing-policy-digest")]
        public void Invalid_Descriptor_Fields_Are_Refused(string field)
        {
            var runtime = PublicationTestSupport.Environment("python");
            var value = ExecutionRequirementsTestSupport.Descriptor(runtime);
            value = field switch
            {
                "schema" => value with { SchemaVersion = 99 }, "os" => value with { OperatingSystem = "Linux" },
                "architecture" => value with { Architecture = "x64" }, "variant" => value with { PlatformVariant = "../any" },
                "artifact-kind" => value with { Artifact = value.Artifact with { Kind = (AiPublicationEnvironmentArtifactKind)99 } },
                "artifact-media-type" => value with { Artifact = value.Artifact with { MediaType = "unknown" } },
                "runtime-digest" => value with { Artifact = value.Artifact with { Digest = "sha256:" + new string('e', 64) } },
                "isolation" => value with { Requirements = value.Requirements with { IsolationTier = (AiWorkerIsolationTier)99 } },
                "egress" => value with { Requirements = value.Requirements with { NetworkEgress = (AiWorkerNetworkEgress)99 } },
                "paths" => value with { Requirements = value.Requirements with { PathProtection = (AiWorkerPathProtection)99 } },
                "legacy-paths" => value with { Requirements = value.Requirements with { PathProtection = AiWorkerPathProtection.DeploymentControlled } },
                "extra-policy-digest" => value with { Requirements = value.Requirements with { EgressPolicyDigest = "sha256:" + new string('d', 64) } },
                _ => value with { Requirements = value.Requirements with { NetworkEgress = AiWorkerNetworkEgress.PinnedPolicy } }
            };
            Assert.Throws<InvalidOperationException>(() => AiPublicationExecutionDescriptors.Validate(value, runtime));
        }

        [Fact]
        public void Configured_Catalog_Copies_Descriptors_And_Keeps_Unversioned_Entries_Explicitly_Legacy()
        {
            var python = PublicationTestSupport.Environment("python");
            var node = PublicationTestSupport.Environment("typescript");
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(python);
            var settings = new Dictionary<string, AiPublicationExecutionDescriptor> { [python.Reference] = descriptor };
            var catalog = new AiConfiguredPublicationEnvironmentCatalog(new[] { python, node }, settings);
            settings[python.Reference] = descriptor with { Requirements = new() };
            Assert.Same(descriptor, catalog.FindExecutionDescriptor(python.Reference));
            Assert.Null(catalog.FindExecutionDescriptor(node.Reference));
            Assert.Null(catalog.FindExecutionDescriptor("missing"));
            Assert.Equal(node, catalog.Find(node.Reference));
        }

        [Fact]
        public void Descriptor_For_An_Unregistered_Runtime_Is_Refused()
        {
            var runtime = PublicationTestSupport.Environment("python");
            Assert.Throws<ArgumentException>(() => new AiConfiguredPublicationEnvironmentCatalog(new[] { runtime },
                new Dictionary<string, AiPublicationExecutionDescriptor>
                    { ["unknown-runtime"] = ExecutionRequirementsTestSupport.Descriptor(runtime) }));
        }

        [Fact]
        public void Historical_Configured_Catalog_Constructor_Does_Not_Invent_Security_Requirements()
        {
            var runtime = PublicationTestSupport.Environment("python");
            var catalog = new AiConfiguredPublicationEnvironmentCatalog(new[] { runtime });
            Assert.Equal(runtime, catalog.Find(runtime.Reference));
            Assert.Null(catalog.FindExecutionDescriptor(runtime.Reference));
        }

        [Fact]
        public void Oci_Artifact_And_Manifest_Digests_Are_Different_Identities()
        {
            var runtime = PublicationTestSupport.Environment("python");
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(runtime) with
            {
                Artifact = new(AiPublicationEnvironmentArtifactKind.OciImage, "sha256:" + new string('b', 64),
                    AiPublicationExecutionDescriptors.OciImageMediaType)
            };
            AiPublicationExecutionDescriptors.Validate(descriptor, runtime);
            var document = new AiPublicationEnvironmentSnapshot(2, runtime, Array.Empty<AiPublicationDependency>())
                { ExecutionDescriptor = descriptor };
            var json = JsonSerializer.Serialize(document); var restored = JsonSerializer.Deserialize<AiPublicationEnvironmentSnapshot>(json)!;
            Assert.Equal(descriptor, restored.ExecutionDescriptor);
            Assert.NotEqual("sha256:" + PublicationTestSupport.Hash(json), descriptor.Artifact.Digest);
        }

        [Fact]
        public void Network_Allow_List_Uses_An_Immutable_Policy_Digest()
        {
            var runtime = PublicationTestSupport.Environment("python");
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(runtime) with
            {
                Requirements = new() { NetworkEgress = AiWorkerNetworkEgress.PinnedPolicy, EgressPolicyDigest = "sha256:" + new string('c', 64) }
            };
            AiPublicationExecutionDescriptors.Validate(descriptor, runtime);
            Assert.Equal(descriptor, JsonSerializer.Deserialize<AiPublicationExecutionDescriptor>(JsonSerializer.Serialize(descriptor)));
        }
    }
}
