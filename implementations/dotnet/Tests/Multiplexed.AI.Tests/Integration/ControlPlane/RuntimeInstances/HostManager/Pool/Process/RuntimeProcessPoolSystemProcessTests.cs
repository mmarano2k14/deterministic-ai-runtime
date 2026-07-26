using System.Collections.Concurrent;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates the process-host Runtime Pool Manager with real operating-system child processes.
    /// </summary>
    public sealed class RuntimeProcessPoolSystemProcessTests
    {
        /// <summary>
        /// Verifies that authoritative identities override optional environment configuration and
        /// cross the real process boundary.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Pass_Authoritative_Identity_Environment()
        {
            var options =
                CreateIdentityValidationOptions();

            options.EnvironmentVariables[
                AiRuntimeProcessPoolChildEnvironment.PoolId] =
                "metadata-pool";

            options.EnvironmentVariables[
                AiRuntimeProcessPoolChildEnvironment.HostId] =
                "metadata-host";

            options.EnvironmentVariables[
                AiRuntimeProcessPoolChildEnvironment.RuntimeInstanceId] =
                "metadata-runtime";

            options.EnvironmentVariables[
                AiRuntimeProcessPoolChildEnvironment.ProcessOrdinal] =
                "999";

            var factory =
                new SystemAiRuntimeProcessPoolChildFactory(options);

            var child =
                await factory.StartAsync(
                    CreateStartRequest());

            var completion =
                await child.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10));

            Assert.Equal(
                AiRuntimeProcessPoolChildExitKind.Unexpected,
                completion.Kind);

            Assert.Equal(0, completion.ExitCode);
            Assert.Equal(
                AiRuntimeProcessPoolChildStatus.Faulted,
                child.Status);
        }

        /// <summary>
        /// Verifies bounded requested termination of a real long-running child process.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Terminate_Real_Process_As_Requested()
        {
            var factory =
                new SystemAiRuntimeProcessPoolChildFactory(
                    CreateLongRunningOptions());

            var child =
                await factory.StartAsync(
                    CreateStartRequest());

            var systemChild =
                Assert.IsType<SystemAiRuntimeProcessPoolChild>(child);

            Assert.True(systemChild.ProcessId > 0);
            Assert.Equal(
                AiRuntimeProcessPoolChildStatus.Running,
                systemChild.Status);

            await systemChild.StopAsync();

            var completion =
                await systemChild.Completion.WaitAsync(
                    TimeSpan.FromSeconds(10));

            Assert.Equal(
                AiRuntimeProcessPoolChildExitKind.Requested,
                completion.Kind);

            Assert.Equal(
                AiRuntimeProcessPoolChildStatus.Stopped,
                systemChild.Status);
        }

        /// <summary>
        /// Verifies that stopping one real child causes exactly one replacement with a fresh
        /// operating-system process and runtime identity.
        /// </summary>
        [Fact]
        public async Task Manager_Should_Replace_Stopped_Real_Child_Without_Changing_Host()
        {
            var systemFactory =
                new SystemAiRuntimeProcessPoolChildFactory(
                    CreateLongRunningOptions());

            var trackingFactory =
                new TrackingChildFactory(systemFactory);

            var manager =
                new AiRuntimeProcessPoolManager(
                    new AiRuntimeProcessPoolOptions
                    {
                        Enabled = true,
                        PoolId = "pool-shared-01",
                        HostIdPrefix = "runtime-pool-host",
                        RuntimeInstanceIdPrefix = "runtime-pool",
                        InitialProcessCount = 1,
                        MinimumProcessCount = 1,
                        MaximumProcessCount = 1,
                        StartupParallelism = 1,
                        ShutdownTimeoutSeconds = 10
                    },
                    trackingFactory);

            try
            {
                var initial =
                    await manager.EnsureInitialCapacityAsync();

                var initialSnapshot =
                    Assert.Single(initial.Children);

                var firstChild =
                    Assert.IsType<SystemAiRuntimeProcessPoolChild>(
                        Assert.Single(trackingFactory.Children));

                await firstChild.StopAsync();

                var replaced =
                    await WaitForSnapshotAsync(
                        manager,
                        snapshot =>
                            snapshot.Status ==
                                AiRuntimeProcessPoolManagerStatus.Running &&
                            snapshot.Children.Count == 1 &&
                            !StringComparer.Ordinal.Equals(
                                snapshot.Children[0].RuntimeInstanceId,
                                initialSnapshot.RuntimeInstanceId));

                var replacementSnapshot =
                    Assert.Single(replaced.Children);

                var replacementChild =
                    Assert.IsType<SystemAiRuntimeProcessPoolChild>(
                        trackingFactory.Children.Last());

                Assert.Equal(2, trackingFactory.StartCount);
                Assert.Equal(
                    initial.PoolId,
                    replacementSnapshot.PoolId);

                Assert.Equal(
                    initial.HostId,
                    replacementSnapshot.HostId);

                Assert.NotEqual(
                    initialSnapshot.RuntimeInstanceId,
                    replacementSnapshot.RuntimeInstanceId);

                Assert.NotEqual(
                    firstChild.ProcessId,
                    replacementChild.ProcessId);
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        /// <summary>
        /// Creates the authoritative child-process start request used by focused real-process tests.
        /// </summary>
        /// <returns>The child-process start request.</returns>
        private static AiRuntimeProcessPoolChildStartRequest CreateStartRequest()
        {
            return new AiRuntimeProcessPoolChildStartRequest
            {
                PoolId = "pool-shared-01",
                HostId = "runtime-pool-host-01",
                RuntimeInstanceId = "runtime-a1",
                Ordinal = 1
            };
        }

        /// <summary>
        /// Creates a cross-platform process command that validates the authoritative identity
        /// environment and exits immediately.
        /// </summary>
        /// <returns>The child-process options.</returns>
        private static AiRuntimeProcessPoolChildProcessOptions
            CreateIdentityValidationOptions()
        {
            if (OperatingSystem.IsWindows())
            {
                var command =
                    $"if not \"%{AiRuntimeProcessPoolChildEnvironment.PoolId}%\"==\"pool-shared-01\" exit /b 21 & " +
                    $"if not \"%{AiRuntimeProcessPoolChildEnvironment.HostId}%\"==\"runtime-pool-host-01\" exit /b 22 & " +
                    $"if not \"%{AiRuntimeProcessPoolChildEnvironment.RuntimeInstanceId}%\"==\"runtime-a1\" exit /b 23 & " +
                    $"if not \"%{AiRuntimeProcessPoolChildEnvironment.ProcessOrdinal}%\"==\"1\" exit /b 24 & " +
                    "exit /b 0";

                return new AiRuntimeProcessPoolChildProcessOptions
                {
                    ExecutablePath =
                        Environment.GetEnvironmentVariable("ComSpec") ??
                        "cmd.exe",
                    Arguments =
                    {
                        "/d",
                        "/s",
                        "/c",
                        command
                    },
                    StopTimeoutSeconds = 10
                };
            }

            var unixCommand =
                $"test \"${AiRuntimeProcessPoolChildEnvironment.PoolId}\" = \"pool-shared-01\" && " +
                $"test \"${AiRuntimeProcessPoolChildEnvironment.HostId}\" = \"runtime-pool-host-01\" && " +
                $"test \"${AiRuntimeProcessPoolChildEnvironment.RuntimeInstanceId}\" = \"runtime-a1\" && " +
                $"test \"${AiRuntimeProcessPoolChildEnvironment.ProcessOrdinal}\" = \"1\"";

            return new AiRuntimeProcessPoolChildProcessOptions
            {
                ExecutablePath = "/bin/sh",
                Arguments =
                {
                    "-c",
                    unixCommand
                },
                StopTimeoutSeconds = 10
            };
        }

        /// <summary>
        /// Creates a cross-platform long-running child-process command.
        /// </summary>
        /// <returns>The child-process options.</returns>
        private static AiRuntimeProcessPoolChildProcessOptions
            CreateLongRunningOptions()
        {
            if (OperatingSystem.IsWindows())
            {
                return new AiRuntimeProcessPoolChildProcessOptions
                {
                    ExecutablePath =
                        Environment.GetEnvironmentVariable("ComSpec") ??
                        "cmd.exe",
                    Arguments =
                    {
                        "/d",
                        "/s",
                        "/c",
                        "ping 127.0.0.1 -n 31 > nul"
                    },
                    StopTimeoutSeconds = 10
                };
            }

            return new AiRuntimeProcessPoolChildProcessOptions
            {
                ExecutablePath = "/bin/sh",
                Arguments =
                {
                    "-c",
                    "sleep 30"
                },
                StopTimeoutSeconds = 10
            };
        }

        /// <summary>
        /// Waits for the focused process-pool snapshot condition.
        /// </summary>
        /// <param name="manager">The process pool manager.</param>
        /// <param name="predicate">The expected snapshot condition.</param>
        /// <returns>The first matching snapshot.</returns>
        /// <exception cref="TimeoutException">
        /// Thrown when the expected state is not observed.
        /// </exception>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForSnapshotAsync(
                IAiRuntimeProcessPoolManager manager,
                Func<AiRuntimeProcessPoolSnapshot, bool> predicate)
        {
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));

            try
            {
                while (true)
                {
                    var snapshot =
                        await manager.GetSnapshotAsync(
                            timeout.Token);

                    if (predicate(snapshot))
                    {
                        return snapshot;
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(20),
                        timeout.Token);
                }
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The expected real-process pool snapshot was not observed.");
            }
        }

        /// <summary>
        /// Tracks every real child created by the wrapped factory.
        /// </summary>
        private sealed class TrackingChildFactory :
            IAiRuntimeProcessPoolChildFactory
        {
            private readonly IAiRuntimeProcessPoolChildFactory inner;
            private readonly ConcurrentQueue<IAiRuntimeProcessPoolChild>
                children = new();
            private int startCount;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="TrackingChildFactory"/> class.
            /// </summary>
            /// <param name="inner">The wrapped child factory.</param>
            public TrackingChildFactory(
                IAiRuntimeProcessPoolChildFactory inner)
            {
                this.inner = inner;
            }

            /// <summary>
            /// Gets all created child handles.
            /// </summary>
            public IReadOnlyList<IAiRuntimeProcessPoolChild> Children =>
                this.children.ToArray();

            /// <summary>
            /// Gets the number of child starts.
            /// </summary>
            public int StartCount =>
                Volatile.Read(ref this.startCount);

            /// <inheritdoc />
            public async Task<IAiRuntimeProcessPoolChild> StartAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                CancellationToken cancellationToken = default)
            {
                var child =
                    await this.inner
                        .StartAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                this.children.Enqueue(child);
                Interlocked.Increment(ref this.startCount);
                return child;
            }
        }
    }
}
