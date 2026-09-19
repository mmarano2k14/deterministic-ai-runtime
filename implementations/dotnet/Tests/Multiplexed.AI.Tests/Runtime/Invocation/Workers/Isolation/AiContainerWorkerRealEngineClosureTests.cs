using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Real Docker-compatible engine tests are explicit opt-in and never count as evidence when skipped.</summary>
    public sealed class RealContainerEngineFactAttribute : FactAttribute
    {
        public RealContainerEngineFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_IMAGE")))
            {
                Skip = "Set MULTIPLEXED_AI_TEST_CONTAINER_ENGINE and MULTIPLEXED_AI_TEST_CONTAINER_IMAGE to run real Linux container enforcement tests.";
            }
        }
    }

    public sealed class RealHostedContainerWorkerFactAttribute : FactAttribute
    {
        public RealHostedContainerWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_IMAGE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION")))
            {
                Skip = "Run Prepare-RealEngineTestImage.ps1 to prepare the exact production Python worker image and runtime identity.";
            }
        }
    }

    /// <summary>Real-engine evidence for Linux/amd64 isolation and production hosted-worker execution using a preloaded exact image.</summary>
    [Trait("Category", "ContainerRealEngine")]
    public sealed class AiContainerWorkerRealEngineClosureTests
    {
        [RealContainerEngineFact]
        public async Task Real_Engine_Applies_The_Selected_Isolation_Boundary()
        {
            var profile = RealProfile();
            var local = await RunEngineAsync("image", "inspect", profile.ImageReference);
            Assert.True(local.ExitCode == 0,
                "The exact image must already be present locally before the test; runtime execution never pulls it. " + local.Stderr);

            var name = ContainerName();
            var started = false;
            try
            {
                var launch = await StartDetachedAsync(profile, name, "while :; do sleep 5; done");
                Assert.True(launch.ExitCode == 0, launch.Stderr);
                started = true;

                var inspect = await RunEngineAsync("inspect", "--type", "container", name);
                Assert.True(inspect.ExitCode == 0, inspect.Stderr);
                AiContainerWorkerIsolationAttestation.Validate(inspect.Stdout, profile);

                const string script = """
                    uid=$(id -u)
                    caps=$(awk '/^CapEff:/{print $2}' /proc/self/status)
                    nnp=$(awk '/^NoNewPrivs:/{print $2}' /proc/self/status)
                    touch /multiplexed-rootfs-probe >/dev/null 2>&1; rootwrite=$?
                    touch /tmp/multiplexed-tmp-probe >/dev/null 2>&1; tmpwrite=$?
                    if command -v wget >/dev/null 2>&1; then
                      wget -q -T 1 -O /dev/null http://1.1.1.1 >/dev/null 2>&1; outbound=$?
                    else
                      outbound=127
                    fi
                    interfaces=$(ls /sys/class/net | tr '\n' ',')
                    if [ -f /sys/fs/cgroup/memory.max ]; then
                      cgroup=v2
                      memory=$(cat /sys/fs/cgroup/memory.max)
                      pids=$(cat /sys/fs/cgroup/pids.max)
                      cpu=$(cat /sys/fs/cgroup/cpu.max)
                    else
                      cgroup=v1
                      memory=$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes)
                      pids=$(cat /sys/fs/cgroup/pids/pids.max)
                      quota=$(cat /sys/fs/cgroup/cpu/cpu.cfs_quota_us)
                      period=$(cat /sys/fs/cgroup/cpu/cpu.cfs_period_us)
                      cpu="$quota $period"
                    fi
                    printf 'uid=%s\ncaps=%s\nnnp=%s\nrootwrite=%s\ntmpwrite=%s\noutbound=%s\ninterfaces=%s\ncgroup=%s\nmemory=%s\npids=%s\ncpu=%s\n' \
                      "$uid" "$caps" "$nnp" "$rootwrite" "$tmpwrite" "$outbound" "$interfaces" "$cgroup" "$memory" "$pids" "$cpu"
                    """;
                var probe = await RunEngineAsync("exec", name, "/bin/sh", "-c", script.ReplaceLineEndings("\n"));
                Assert.True(probe.ExitCode == 0, probe.Stderr);
                var values = ParseValues(probe.Stdout);

                Assert.Equal(profile.ContainerUser.Split(':')[0], values["uid"]);
                Assert.Equal("0000000000000000", values["caps"]);
                Assert.Equal("1", values["nnp"]);
                Assert.NotEqual("0", values["rootwrite"]);
                Assert.Equal("0", values["tmpwrite"]);
                Assert.NotEqual("127", values["outbound"]);
                Assert.NotEqual("0", values["outbound"]);
                Assert.Equal(new[] { "lo" }, values["interfaces"].Split(',', StringSplitOptions.RemoveEmptyEntries));
                Assert.Equal(profile.ResourceLimits.MemoryBytes.ToString(CultureInfo.InvariantCulture), values["memory"]);
                Assert.Equal(profile.ResourceLimits.PidsLimit.ToString(CultureInfo.InvariantCulture), values["pids"]);
                AssertCpuLimit(values["cpu"], profile.ResourceLimits.CpuMilliCores);
            }
            finally
            {
                if (started) _ = await RunEngineAsync("rm", "--force", name);
            }
        }

        [RealHostedContainerWorkerFact]
        public async Task Real_Transport_Executes_The_Production_Python_Worker_In_The_Exact_Isolated_Image()
        {
            var profile = RealWorkerProfile();
            var local = await RunEngineAsync("image", "inspect", profile.ImageReference);
            Assert.True(local.ExitCode == 0,
                "The exact worker image must already be present locally before the test; runtime execution never pulls it. " + local.Stderr);

            var request = RealWorkerRequest(
                profile,
                "def run(inputs, context):\n    return {\"success\": True, \"payload\": {\"value\": inputs[\"amount\"] * 2}}\n",
                new { amount = 21 });
            var transport = RealWorkerTransport(profile);
            var heartbeats = 0;

            var result = await transport.InvokeAsync(request, _ =>
            {
                Interlocked.Increment(ref heartbeats);
                return Task.CompletedTask;
            });

            Assert.True(result.Success);
            Assert.True(heartbeats >= 1);
            using var payload = JsonDocument.Parse(result.PayloadJson);
            Assert.Equal(42, payload.RootElement.GetProperty("value").GetInt32());
            await AssertNoManagedContainersAsync(profile);
        }

        [RealHostedContainerWorkerFact]
        public async Task Real_Cancellation_Stops_The_Active_Production_Worker_And_Removes_Its_Container()
        {
            var profile = RealWorkerProfile();
            var local = await RunEngineAsync("image", "inspect", profile.ImageReference);
            Assert.True(local.ExitCode == 0,
                "The exact worker image must already be present locally before the test; runtime execution never pulls it. " + local.Stderr);

            var request = RealWorkerRequest(
                profile,
                "import time\ndef run(inputs, context):\n    time.sleep(300)\n    return {\"success\": True, \"payload\": {\"unexpected\": True}}\n",
                new { amount = 21 });
            var transport = RealWorkerTransport(profile);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var heartbeats = 0;

            var operation = transport.InvokeAsync(request, _ =>
            {
                if (Interlocked.Increment(ref heartbeats) >= 2) cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(heartbeats >= 2);
            await AssertNoManagedContainersAsync(profile);
        }

        [RealContainerEngineFact]
        public async Task Force_Removal_Contains_And_Removes_A_Running_Descendant_Process()
        {
            var profile = RealProfile();
            var local = await RunEngineAsync("image", "inspect", profile.ImageReference);
            Assert.True(local.ExitCode == 0,
                "The exact image must already be present locally before the test; runtime execution never pulls it. " + local.Stderr);

            var name = ContainerName();
            var removed = false;
            try
            {
                var launch = await StartDetachedAsync(profile, name,
                    "sleep 300 & child=$!; echo $child >/tmp/child.pid; wait $child");
                Assert.True(launch.ExitCode == 0, launch.Stderr);

                string? childPid = null;
                for (var attempt = 0; attempt < 50 && childPid is null; attempt++)
                {
                    var child = await RunEngineAsync("exec", name, "/bin/sh", "-c", "cat /tmp/child.pid 2>/dev/null");
                    if (child.ExitCode == 0 && !string.IsNullOrWhiteSpace(child.Stdout)) childPid = child.Stdout.Trim();
                    else await Task.Delay(50);
                }
                Assert.False(string.IsNullOrWhiteSpace(childPid));

                var present = await RunEngineAsync("exec", name, "/bin/sh", "-c", "test -d /proc/" + childPid);
                Assert.Equal(0, present.ExitCode);

                var cleanup = await RunEngineAsync("rm", "--force", name);
                Assert.Equal(0, cleanup.ExitCode);
                removed = true;

                var inspect = await RunEngineAsync("inspect", "--type", "container", name);
                Assert.NotEqual(0, inspect.ExitCode);
            }
            finally
            {
                if (!removed) _ = await RunEngineAsync("rm", "--force", name);
            }
        }

        private static AiWorkerInvocationRequest RealWorkerRequest(
            AiContainerWorkerProfile profile,
            string sourceText,
            object inputs)
        {
            var source = Encoding.UTF8.GetBytes(sourceText);
            var original = WorkerTestSupport.Request();
            var file = new AiWorkerFile(
                "main.py",
                Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
                source.LongLength,
                Convert.ToBase64String(source).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            var target = original.Code.Target with
            {
                ExecutionLanguage = "python",
                EnvironmentRef = profile.Runtime.Reference,
                EnvironmentSha256 = profile.Runtime.RuntimeSha256
            };
            var code = new AiWorkerCodeBundle(
                target, profile.Runtime, "main.py", "run", new[] { file }, Array.Empty<AiWorkerDependency>())
            {
                ExecutionDescriptor = profile.ExecutionDescriptor
            };
            return original with
            {
                Inputs = JsonSerializer.SerializeToElement(inputs),
                Code = code,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(2)
            };
        }

        private static AiContainerWorkerTransport RealWorkerTransport(AiContainerWorkerProfile profile) => new(
            new AiConfiguredContainerWorkerCatalog(new[] { profile }),
            new AiWorkerProcessTransportOptions(
                startupTimeout: TimeSpan.FromSeconds(30),
                heartbeatTimeout: TimeSpan.FromSeconds(15),
                shutdownTimeout: TimeSpan.FromSeconds(10)));

        private static async Task AssertNoManagedContainersAsync(AiContainerWorkerProfile profile)
        {
            var remaining = await RunEngineAsync(
                "ps", "--all",
                "--filter", "label=multiplexed.ai.hosted-worker=1",
                "--filter", "label=multiplexed.ai.owner-scope=" + profile.ContainerOwnerScope,
                "--format", "{{.Names}}");
            Assert.Equal(0, remaining.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(remaining.Stdout));
        }

        private static async Task<CommandResult> StartDetachedAsync(
            AiContainerWorkerProfile profile,
            string name,
            string shellScript)
        {
            var plan = AiContainerWorkerLaunchPlan.Create(profile, name);
            var imageIndex = plan.Arguments.ToList().IndexOf(plan.ImageReference);
            Assert.True(imageIndex > 0);
            var arguments = plan.Arguments.Take(imageIndex).ToList();
            arguments.Add("--detach");
            arguments.Add("--entrypoint");
            arguments.Add("/bin/sh");
            arguments.Add(plan.ImageReference);
            arguments.Add("-c");
            arguments.Add(shellScript);
            return await RunEngineAsync(arguments.ToArray());
        }

        private static AiContainerWorkerProfile RealWorkerProfile()
        {
            var engine = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE")!;
            var image = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_IMAGE")!;
            var runtimeVersion = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION");
            if (string.IsNullOrWhiteSpace(runtimeVersion))
                throw new InvalidOperationException(
                    "MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION must identify the CPython version embedded in the prepared worker image.");
            if (!Path.IsPathFullyQualified(engine) || !File.Exists(engine))
                throw new FileNotFoundException(
                    "MULTIPLEXED_AI_TEST_CONTAINER_ENGINE must identify an absolute Docker-compatible engine executable.", engine);

            var (repository, digest) = SplitImageReference(image);
            var runtime = new AiPublicationEnvironment(
                "container-python-" + digest[7..19], "python", runtimeVersion, digest[7..]);
            var descriptor = new AiPublicationExecutionDescriptor
            {
                OperatingSystem = "linux",
                Architecture = "amd64",
                Artifact = new(
                    AiPublicationEnvironmentArtifactKind.OciImage,
                    digest,
                    AiPublicationExecutionDescriptors.OciImageMediaType),
                Requirements = new()
                {
                    IsolationTier = AiWorkerIsolationTier.SandboxedContainer,
                    NetworkEgress = AiWorkerNetworkEgress.DenyAll,
                    PathProtection = AiWorkerPathProtection.SealedClosure
                }
            };
            var root = Path.GetDirectoryName(Path.GetFullPath(engine))
                ?? throw new InvalidOperationException("Container engine directory is required.");
            return new(
                runtime,
                descriptor,
                Path.GetFullPath(engine),
                FileHash(engine),
                root,
                repository,
                "real-worker-" + Guid.NewGuid().ToString("N")[..16],
                new AiContainerWorkerResourceLimits
                {
                    CpuMilliCores = 750,
                    MemoryBytes = 268435456,
                    PidsLimit = 32,
                    WritableWorkspaceBytes = 33554432
                },
                "65532:65532",
                approvedLaunchRoots: new[] { root },
                heartbeatMilliseconds: 100);
        }

        private static (string Repository, string Digest) SplitImageReference(string image)
        {
            var separator = image.LastIndexOf('@');
            if (separator <= 0 || separator == image.Length - 1)
                throw new InvalidOperationException(
                    "MULTIPLEXED_AI_TEST_CONTAINER_IMAGE must be repository@sha256:<manifest-digest>.");
            var repository = image[..separator];
            var digest = image[(separator + 1)..];
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                digest[7..].Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
                throw new InvalidOperationException(
                    "MULTIPLEXED_AI_TEST_CONTAINER_IMAGE must use a lowercase sha256 manifest digest.");
            return (repository, digest);
        }

        private static AiContainerWorkerProfile RealProfile()
        {
            var engine = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE")!;
            var image = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_IMAGE")!;
            if (!Path.IsPathFullyQualified(engine) || !File.Exists(engine))
                throw new FileNotFoundException("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE must identify an absolute Docker-compatible engine executable.", engine);

            var (repository, digest) = SplitImageReference(image);

            var runtime = PublicationTestSupport.Environment("python");
            var descriptor = new AiPublicationExecutionDescriptor
            {
                OperatingSystem = "linux",
                Architecture = "amd64",
                Artifact = new(
                    AiPublicationEnvironmentArtifactKind.OciImage,
                    digest,
                    AiPublicationExecutionDescriptors.OciImageMediaType),
                Requirements = new()
            };
            var root = Path.GetDirectoryName(Path.GetFullPath(engine))
                ?? throw new InvalidOperationException("Container engine directory is required.");
            return new(
                runtime,
                descriptor,
                Path.GetFullPath(engine),
                FileHash(engine),
                root,
                repository,
                "real-engine-closure",
                new AiContainerWorkerResourceLimits
                {
                    CpuMilliCores = 750,
                    MemoryBytes = 134217728,
                    PidsLimit = 32,
                    WritableWorkspaceBytes = 16777216
                },
                "65532:65532",
                approvedLaunchRoots: new[] { root });
        }

        private static async Task<CommandResult> RunEngineAsync(params string[] arguments)
        {
            var executable = Environment.GetEnvironmentVariable("MULTIPLEXED_AI_TEST_CONTAINER_ENGINE")!;
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The configured real container engine did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            return new(process.ExitCode, await stdout, await stderr);
        }

        private static IReadOnlyDictionary<string, string> ParseValues(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..]))
                    throw new InvalidOperationException("Unexpected real-container probe output.");
            }
            return values;
        }

        private static void AssertCpuLimit(string value, int expectedMilliCores)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, parts.Length);
            Assert.True(long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota));
            Assert.True(long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period));
            Assert.True(quota > 0 && period > 0);
            Assert.Equal((long)expectedMilliCores * period, quota * 1000L);
        }

        private static string ContainerName() =>
            "multiplexed-ai-real-" + Guid.NewGuid().ToString("N")[..20];

        private static string FileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
    }
}
