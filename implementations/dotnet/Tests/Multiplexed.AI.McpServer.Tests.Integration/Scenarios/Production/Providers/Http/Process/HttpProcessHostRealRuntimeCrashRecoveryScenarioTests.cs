using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process
{
    /// <summary>
    /// Proves real HTTP process-host runtime crash recovery without synthetic DAG reseeding.
    /// </summary>
    public sealed class HttpProcessHostRealRuntimeCrashRecoveryScenarioTests
        : ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="HttpProcessHostRealRuntimeCrashRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new HttpProcessHostScenarioRuntimeProfile())
        {
            _output = output
                ?? throw new ArgumentNullException(nameof(output));
        }

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessUnsafeTimeoutOverride =>
            TimeSpan.FromMinutes(3);

        /// <inheritdoc />
        protected override TimeSpan? DirectScenarioUnsafeTimeoutOverride =>
            TimeSpan.FromMinutes(3);

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight
        /// DAG execution resumes on a replacement runtime.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }

        /// <summary>
        /// Verifies that two tenants can recover real process-host runtime crashes
        /// with strict DAG resume, forensics, replay, ledger, trace, inventory proof,
        /// and no cross-tenant recovery leak.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies that two impacted tenants recover real process-host runtime crashes
        /// while a third safe tenant continues normal execution without recovery,
        /// forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies that multiple isolated HTTP process-host multi-tenant crash-recovery
        /// scenarios can execute concurrently without cross-scenario or cross-tenant leakage.
        /// </summary>
        /// <param name="parallelism">
        /// The number of complete multi-tenant crash-recovery scenarios to execute concurrently.
        /// </param>
        /// <returns>A task that completes when every parallel scenario has finished.</returns>
        [Theory]
        [InlineData(10)]
        public Task Http_ProcessHost_Should_Execute_MultiTenant_Crash_Recovery_Scenarios_In_Parallel(
            int parallelism)
        {
            return ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                parallelism);
        }

        /// <summary>
        /// Verifies repeatedly that multiple HTTP process-host multi-tenant crash-recovery
        /// scenarios can execute concurrently without cross-scenario interference.
        /// </summary>
        /// <param name="parallelism">
        /// The number of crash-recovery scenarios executed concurrently during each iteration.
        /// </param>
        /// <returns>
        /// A task that completes when all parallel stability iterations have finished.
        /// </returns>
        [Theory]
        [InlineData(10, 5)]
        public async Task Http_ProcessHost_Should_Execute_MultiTenant_Crash_Recovery_Scenarios_In_Parallel_Loop(
            int parallelism, int iterationCount)
        {

            ArgumentOutOfRangeException.ThrowIfLessThan(
                parallelism,
                1);

            var overallStopwatch =
                Stopwatch.StartNew();

            var failures =
                new List<Exception>();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"# HTTP PARALLEL CRASH-RECOVERY STABILITY LOOP - STARTING {iterationCount} ITERATIONS");

            _output.WriteLine(
                $"[HTTP PARALLEL STABILITY SUMMARY] " +
                $"Iterations='{iterationCount}', " +
                $"ParallelismPerIteration='{parallelism}', " +
                $"ExpectedScenarios='{iterationCount * parallelism}', " +
                $"ExpectedTenants='{iterationCount * parallelism * 3}', " +
                $"ExpectedSubmittedRuns='{iterationCount * parallelism * 9}', " +
                $"ExpectedImpactedTenants='{iterationCount * parallelism * 2}', " +
                $"ExpectedSafeTenants='{iterationCount * parallelism}'.");

            for (var iteration = 1;
                 iteration <= iterationCount;
                 iteration++)
            {
                var iterationStopwatch =
                    Stopwatch.StartNew();

                _output.WriteLine(string.Empty);
                _output.WriteLine(
                    $"# HTTP PARALLEL CRASH-RECOVERY STABILITY ITERATION " +
                    $"{iteration}/{iterationCount}");

                try
                {
                    await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                            parallelism)
                        .ConfigureAwait(false);

                    iterationStopwatch.Stop();

                    _output.WriteLine(
                        $"[HTTP PARALLEL STABILITY PASS] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Parallelism='{parallelism}', " +
                        $"Duration='{iterationStopwatch.Elapsed}'.");
                }
                catch (Exception exception)
                {
                    iterationStopwatch.Stop();

                    var wrappedException =
                        new InvalidOperationException(
                            $"Parallel HTTP process-host crash-recovery stability iteration " +
                            $"{iteration}/{iterationCount} with parallelism '{parallelism}' " +
                            $"failed after '{iterationStopwatch.Elapsed}'.",
                            exception);

                    failures.Add(
                        wrappedException);

                    _output.WriteLine(
                        $"[HTTP PARALLEL STABILITY FAIL] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Parallelism='{parallelism}', " +
                        $"Duration='{iterationStopwatch.Elapsed}', " +
                        $"ExceptionType='{exception.GetType().FullName}', " +
                        $"Message='{exception.Message}'.");

                    _output.WriteLine(
                        exception.ToString());
                }
                finally
                {
                    if (iteration < iterationCount)
                    {
                        var cooldown = TimeSpan.FromSeconds(10);

                        _output.WriteLine(
                            $"[HTTP PARALLEL STABILITY COOLDOWN] " +
                            $"CompletedIteration='{iteration}', " +
                            $"NextIteration='{iteration + 1}', " +
                            $"Duration='{cooldown}'.");

                        await Task
                            .Delay(cooldown)
                            .ConfigureAwait(false);
                    }
                }
            }

            overallStopwatch.Stop();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                "# HTTP PARALLEL CRASH-RECOVERY STABILITY LOOP - FINAL SUMMARY");

            _output.WriteLine(
                $"[HTTP PARALLEL STABILITY FINAL SUMMARY] " +
                $"Iterations='{iterationCount}', " +
                $"ParallelismPerIteration='{parallelism}', " +
                $"TotalScenarios='{iterationCount * parallelism}', " +
                $"PassedIterations='{iterationCount - failures.Count}', " +
                $"FailedIterations='{failures.Count}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    $"{failures.Count} of {iterationCount} parallel HTTP process-host " +
                    $"crash-recovery stability iterations failed with parallelism " +
                    $"'{parallelism}'.",
                    failures);
            }
        }
    }
}