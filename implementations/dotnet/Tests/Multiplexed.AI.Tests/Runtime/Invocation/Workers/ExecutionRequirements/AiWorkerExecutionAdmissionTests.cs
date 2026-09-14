using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Required security is checked against actual provider support before launch, including policy evaluation.</summary>
    public sealed class AiWorkerExecutionAdmissionTests
    {
        [Theory]
        [InlineData("sandbox")]
        [InlineData("restricted")]
        [InlineData("network")]
        [InlineData("network-policy")]
        [InlineData("sealed-paths")]
        [InlineData("oci")]
        [InlineData("platform")]
        [InlineData("variant")]
        public async Task Unsupported_Requirements_Fail_Before_Launch_File_Access_Or_Heartbeat(string requirement)
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(fixture.Runtime);
            descriptor = requirement switch
            {
                "sandbox" => descriptor with { Requirements = descriptor.Requirements with { IsolationTier = AiWorkerIsolationTier.SandboxedContainer } },
                "restricted" => descriptor with { Requirements = descriptor.Requirements with { IsolationTier = AiWorkerIsolationTier.RestrictedProcess } },
                "network" => descriptor with { Requirements = descriptor.Requirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll } },
                "network-policy" => descriptor with { Requirements = descriptor.Requirements with
                    { NetworkEgress = AiWorkerNetworkEgress.PinnedPolicy, EgressPolicyDigest = "sha256:" + new string('f', 64) } },
                "sealed-paths" => descriptor with { Requirements = descriptor.Requirements with { PathProtection = AiWorkerPathProtection.SealedClosure } },
                "oci" => descriptor with { Artifact = new(AiPublicationEnvironmentArtifactKind.OciImage,
                    "sha256:" + new string('e', 64), AiPublicationExecutionDescriptors.OciImageMediaType) },
                "platform" => descriptor with { OperatingSystem = descriptor.OperatingSystem == "linux" ? "windows" : "linux" },
                _ => descriptor with { PlatformVariant = "v9" }
            };
            var profile = fixture.Profile(descriptor, executable: Path.Combine(fixture.Root, "missing-host"));
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { profile }), new());
            var heartbeats = 0;
            await Assert.ThrowsAsync<NotSupportedException>(() => transport.InvokeAsync(fixture.Request(profile),
                _ => { heartbeats++; return Task.CompletedTask; }));
            Assert.Equal(0, heartbeats);
        }

        [Fact]
        public void Explicit_Trusted_Requirements_Can_Use_The_Validated_Process_Provider()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            AiWorkerExecutionAdmission.Require(fixture.Request(profile).Code, profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible);
            AiWorkerLaunchPaths.ValidateProfile(profile);
            Assert.Equal(AiWorkerNetworkEgress.HostNetwork, AiWorkerExecutionAdmission.ProcessCapabilities.NetworkEgress);
            Assert.Equal(AiWorkerPathProtection.ValidatedPaths, AiWorkerExecutionAdmission.ProcessCapabilities.PathProtection);
        }

        [Fact]
        public void A_Strict_Server_Minimum_Cannot_Be_Downgraded_By_A_Trusted_Process_Descriptor()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            Assert.Throws<NotSupportedException>(() => AiWorkerExecutionAdmission.Require(fixture.Request(profile).Code, profile, new()));
        }

        [Fact]
        public void Missing_Server_Metadata_Cannot_Downgrade_A_Versioned_Profile_To_Legacy()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            var code = fixture.Request(profile).Code with { ExecutionDescriptor = null };
            Assert.Throws<InvalidOperationException>(() => AiWorkerExecutionAdmission.Require(code, profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible));
        }

        [Fact]
        public void Changed_Descriptor_Cannot_Substitute_The_Pinned_Profile()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            var code = fixture.Request(profile).Code with { ExecutionDescriptor = profile.ExecutionDescriptor! with
                { Requirements = profile.ExecutionDescriptor.Requirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll } } };
            Assert.Throws<InvalidOperationException>(() => AiWorkerExecutionAdmission.Require(code, profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible));
        }

        [Fact]
        public void Legacy_Execution_Remains_Trusted_Only_And_Can_Be_Disabled_By_The_Host()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var profile = new AiWorkerProcessProfile(fixture.Runtime, fixture.Executable, WorkerTestSupport.FileHash(fixture.Executable),
                Array.Empty<string>(), fixture.Root);
            var code = fixture.Request(profile).Code;
            AiWorkerExecutionAdmission.Require(code, profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible);
            Assert.Throws<NotSupportedException>(() => AiWorkerExecutionAdmission.Require(code, profile, new()));
            Assert.Throws<NotSupportedException>(() => AiWorkerExecutionAdmission.Require(code, profile,
                AiWorkerExecutionAdmissionPolicy.LegacyCompatible with { AllowLegacyTrustedProcess = false }));
        }

        [Fact]
        public void Server_Descriptor_Does_Not_Change_Any_Existing_Worker_Wire_Byte()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture(); var profile = fixture.Profile();
            var request = fixture.Request(profile);
            var legacy = request with { Code = request.Code with { ExecutionDescriptor = null } };
            Assert.Equal(AiWorkerInvocationProtocol.EncodeRequest(legacy, 1048576), AiWorkerInvocationProtocol.EncodeRequest(request, 1048576));
            using var json = JsonDocument.Parse(AiWorkerInvocationProtocol.EncodeRequest(request, 1048576));
            Assert.False(json.RootElement.GetProperty("code").TryGetProperty("executionDescriptor", out _));
            Assert.Equal(new[] { "reference", "executionLanguage", "runtimeVersion", "runtimeSha256" },
                json.RootElement.GetProperty("code").GetProperty("runtime").EnumerateObject().Select(p => p.Name).ToArray());
        }

        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Policy_Path_Uses_The_Same_Provider_Gate_Without_Fabricating_A_Denial(string language)
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var runtime = fixture.Runtime with { ExecutionLanguage = language, Reference = language + "-fixed" };
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(runtime, new());
            var profile = new AiWorkerProcessProfile(runtime, Path.Combine(fixture.Root, "absent-host"),
                WorkerTestSupport.FileHash(fixture.Executable), Array.Empty<string>(), fixture.Root,
                executionDescriptor: descriptor, approvedLaunchRoots: new[] { fixture.Root });
            var code = WorkerTestSupport.Bundle() with
            {
                Runtime = runtime, ExecutionDescriptor = descriptor,
                Target = WorkerTestSupport.Bundle().Target with { ExecutionLanguage = language, ImplementationRef = "impl-policy" }
            };
            var policy = new AiHostedConcurrencyPolicyTransport(language, new PolicyPreparer(code),
                new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { profile }), new()));
            var request = new AiConcurrencyPolicyRequest("policy-a", "limit", "Pipeline", null, language, "impl-policy",
                DateTimeOffset.UtcNow.AddMinutes(1),
                new AiConcurrencyPolicyInput("tenant-a", "group-a", "run-a", "pipeline", "native", "native", "runtime-a", null, null, null),
                JsonSerializer.SerializeToElement(new { limit = 1 }));
            await Assert.ThrowsAsync<NotSupportedException>(() => policy.EvaluateAsync(request));
        }

        [Fact]
        public void Host_Artifact_Digest_Cannot_Differ_From_The_Configured_Executable_Digest()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            Assert.Throws<InvalidOperationException>(() => new AiWorkerProcessProfile(fixture.Runtime,
                fixture.Executable, new string('e', 64), Array.Empty<string>(), fixture.Root,
                executionDescriptor: ExecutionRequirementsTestSupport.Descriptor(fixture.Runtime),
                approvedLaunchRoots: new[] { fixture.Root }));
        }

        [Fact]
        public void Versioned_Process_Profile_Without_Approved_Roots_Is_Not_Admitted()
        {
            using var fixture = new ExecutionRequirementsTestSupport.LaunchFixture();
            var profile = fixture.Profile(roots: Array.Empty<string>());
            Assert.Throws<InvalidOperationException>(() => AiWorkerExecutionAdmission.Require(
                fixture.Request(profile).Code, profile, AiWorkerExecutionAdmissionPolicy.LegacyCompatible));
        }

        [Fact]
        public void Explicit_Admission_Policy_Is_Preserved_By_Service_Registration()
        {
            var services = new ServiceCollection(); var policy = new AiWorkerExecutionAdmissionPolicy();
            services.AddSingleton(policy);
            services.AddAiHostedInvocationWorkers(new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            Assert.Same(policy, Assert.Single(services.Where(s => s.ServiceType == typeof(AiWorkerExecutionAdmissionPolicy))).ImplementationInstance);
        }

        [Fact]
        public void Conflicting_Admission_Policy_Registration_Is_Not_Silently_Ignored()
        {
            var services = new ServiceCollection(); services.AddSingleton(new AiWorkerExecutionAdmissionPolicy());
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedInvocationWorkers(
                new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new(),
                executionPolicy: AiWorkerExecutionAdmissionPolicy.LegacyCompatible));
        }

        private sealed class PolicyPreparer(AiWorkerCodeBundle code) : IAiConcurrencyPolicyCodePreparer
        {
            public Task<AiWorkerCodeBundle> PrepareAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(code); }
        }
    }
}
