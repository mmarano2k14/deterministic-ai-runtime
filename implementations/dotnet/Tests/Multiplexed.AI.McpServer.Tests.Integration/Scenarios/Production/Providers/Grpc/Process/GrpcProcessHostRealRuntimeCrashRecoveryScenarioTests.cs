using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Process
{
    /// <summary>
    /// Proves real gRPC process-host runtime crash recovery without synthetic DAG reseeding.
    /// </summary>
    public sealed class GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests
        : ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcProcessHostScenarioRuntimeProfile())
        {
            _output = output;
        }

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight DAG execution resumes
        /// on a replacement runtime through gRPC.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }

        /// <summary>
        /// Verifies that two tenants can recover real gRPC process-host runtime crashes with strict DAG resume,
        /// forensics, replay, ledger, trace, inventory proof, and no cross-tenant recovery leak.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies that two impacted tenants recover real gRPC process-host runtime crashes while a third safe tenant
        /// continues normal execution without recovery, forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies repeatedly that impacted tenants recover real gRPC process-host runtime crashes
        /// while safe tenants continue normal execution without recovery contamination.
        /// </summary>
        /// <returns>A task that completes when all validation iterations have finished.</returns>
        [Fact]
        public async Task Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace_Loop()
        {
            const int iterationCount = 5;

            var failures =
                new List<Exception>();

            for (var iteration = 1; iteration <= iterationCount; iteration++)
            {
                var stopwatch =
                    System.Diagnostics.Stopwatch.StartNew();

                _output.WriteLine(string.Empty);
                _output.WriteLine(
                    $"# GRPC PROCESS-HOST CRASH RECOVERY STABILITY ITERATION {iteration}/{iterationCount}");

                try
                {
                    await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
                        .ConfigureAwait(false);

                    stopwatch.Stop();

                    _output.WriteLine(
                        $"[GRPC PROCESS-HOST STABILITY PASS] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Duration='{stopwatch.Elapsed}'.");
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();

                    var wrappedException =
                        new InvalidOperationException(
                            $"gRPC process-host crash recovery stability iteration {iteration}/{iterationCount} failed after '{stopwatch.Elapsed}'.",
                            exception);

                    failures.Add(
                        wrappedException);

                    _output.WriteLine(
                        $"[GRPC PROCESS-HOST STABILITY FAIL] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Duration='{stopwatch.Elapsed}', " +
                        $"ExceptionType='{exception.GetType().FullName}', " +
                        $"Message='{exception.Message}'.");

                    _output.WriteLine(
                        exception.ToString());
                }
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine("# GRPC PROCESS-HOST CRASH RECOVERY STABILITY SUMMARY");
            _output.WriteLine($"Iterations='{iterationCount}'");
            _output.WriteLine($"Passed='{iterationCount - failures.Count}'");
            _output.WriteLine($"Failed='{failures.Count}'");

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    $"{failures.Count} of {iterationCount} gRPC process-host crash recovery stability iterations failed.",
                    failures);
            }
        }

        /// <summary>
        /// Verifies that multiple multi-tenant crash-recovery scenarios can execute concurrently.
        /// </summary>
        /// <param name="parallelism">The number of scenarios executed concurrently.</param>
        /// <returns>A task that completes when all concurrent scenarios have finished.</returns>
        [Theory]
        [InlineData(10)]
        public Task Grpc_ProcessHost_Should_Execute_MultiTenant_Crash_Recovery_Scenarios_In_Parallel(
            int parallelism)
        {
            return ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                parallelism);
        }

        /// <summary>
        /// Verifies repeatedly that multiple multi-tenant crash-recovery scenarios
        /// can execute concurrently without cross-scenario interference.
        /// </summary>
        /// <param name="parallelism">
        /// The number of crash-recovery scenarios executed concurrently during each iteration.
        /// </param>
        /// <returns>
        /// A task that completes when all parallel stability iterations have finished.
        /// </returns>
        [Theory]
        [InlineData(3)]
        public async Task Grpc_ProcessHost_Should_Execute_MultiTenant_Crash_Recovery_Scenarios_In_Parallel_Loop(
            int parallelism)
        {
            const int iterationCount = 5;

            ArgumentOutOfRangeException.ThrowIfLessThan(
                parallelism,
                1);

            var overallStopwatch =
                Stopwatch.StartNew();

            var failures =
                new List<Exception>();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"# PARALLEL CRASH-RECOVERY STABILITY LOOP - STARTING {iterationCount} ITERATIONS");

            _output.WriteLine(
                $"[PARALLEL STABILITY SUMMARY] " +
                $"Iterations='{iterationCount}', " +
                $"ParallelismPerIteration='{parallelism}', " +
                $"ExpectedScenarios='{iterationCount * parallelism}', " +
                $"ExpectedTenants='{iterationCount * parallelism * 3}', " +
                $"ExpectedSubmittedRuns='{iterationCount * parallelism * 9}', " +
                $"ExpectedImpactedTenants='{iterationCount * parallelism * 2}', " +
                $"ExpectedSafeTenants='{iterationCount * parallelism}'.");

            for (var iteration = 1; iteration <= iterationCount; iteration++)
            {
                var iterationStopwatch =
                    Stopwatch.StartNew();

                _output.WriteLine(string.Empty);
                _output.WriteLine(
                    $"# PARALLEL CRASH-RECOVERY STABILITY ITERATION {iteration}/{iterationCount}");

                try
                {
                    await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                            parallelism)
                        .ConfigureAwait(false);

                    iterationStopwatch.Stop();

                    _output.WriteLine(
                        $"[PARALLEL STABILITY PASS] " +
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
                            $"Parallel gRPC process-host crash-recovery stability iteration " +
                            $"{iteration}/{iterationCount} with parallelism '{parallelism}' " +
                            $"failed after '{iterationStopwatch.Elapsed}'.",
                            exception);

                    failures.Add(
                        wrappedException);

                    _output.WriteLine(
                        $"[PARALLEL STABILITY FAIL] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Parallelism='{parallelism}', " +
                        $"Duration='{iterationStopwatch.Elapsed}', " +
                        $"ExceptionType='{exception.GetType().FullName}', " +
                        $"Message='{exception.Message}'.");

                    _output.WriteLine(
                        exception.ToString());
                }
            }

            overallStopwatch.Stop();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                "# PARALLEL CRASH-RECOVERY STABILITY LOOP - FINAL SUMMARY");

            _output.WriteLine(
                $"[PARALLEL STABILITY FINAL SUMMARY] " +
                $"Iterations='{iterationCount}', " +
                $"ParallelismPerIteration='{parallelism}', " +
                $"TotalScenarios='{iterationCount * parallelism}', " +
                $"PassedIterations='{iterationCount - failures.Count}', " +
                $"FailedIterations='{failures.Count}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    $"{failures.Count} of {iterationCount} parallel gRPC process-host " +
                    $"crash-recovery stability iterations failed with parallelism '{parallelism}'.",
                    failures);
            }
        }

        private async Task ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
            int parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                parallelism,
                1);

            var overallStopwatch = Stopwatch.StartNew();

            _output.WriteLine(
                $"# PARALLEL CRASH-RECOVERY PROOF - STARTING {parallelism} SCENARIOS");

            _output.WriteLine(
                $"[PARALLEL SUMMARY] Parallelism='{parallelism}', " +
                $"ExpectedTenants='{parallelism * 3}', " +
                $"ExpectedSubmittedRuns='{parallelism * 9}', " +
                $"ExpectedImpactedTenants='{parallelism * 2}', " +
                $"ExpectedSafeTenants='{parallelism}'.");

            var scenarioTasks = Enumerable
                .Range(1, parallelism)
                .Select(scenarioNumber =>
                {
                    var scenarioId = Guid.NewGuid()
                        .ToString("N")[..8];

                    return ExecuteScenarioWithDiagnosticsAsync(
                        scenarioNumber,
                        parallelism,
                        scenarioId);
                })
                .ToArray();

            var results = await Task.WhenAll(scenarioTasks);

            overallStopwatch.Stop();

            _output.WriteLine(string.Empty);
            _output.WriteLine("# PARALLEL CRASH-RECOVERY PROOF - RESULTS");

            foreach (var result in results.OrderBy(result => result.ScenarioNumber))
            {
                _output.WriteLine(
                    $"[PARALLEL SCENARIO {result.ScenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{result.ScenarioId}', " +
                    $"Outcome='{(result.Exception is null ? "PASSED" : "FAILED")}', " +
                    $"Duration='{result.Duration}'.");

                if (result.Exception is null)
                {
                    continue;
                }

                _output.WriteLine(
                    $"[PARALLEL SCENARIO {result.ScenarioNumber}/{parallelism} FAILURE] " +
                    $"ScenarioId='{result.ScenarioId}', " +
                    $"ExceptionType='{result.Exception.GetType().FullName}', " +
                    $"Message='{result.Exception.Message}'.");

                _output.WriteLine(result.Exception.ToString());
            }

            var failures = results
                .Where(result => result.Exception is not null)
                .ToArray();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"[PARALLEL SUMMARY] " +
                $"Parallelism='{parallelism}', " +
                $"Passed='{results.Length - failures.Length}', " +
                $"Failed='{failures.Length}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");

            if (failures.Length > 0)
            {
                throw new AggregateException(
                    $"{failures.Length} of {parallelism} parallel crash-recovery scenarios failed.",
                    failures.Select(result => result.Exception!));
            }
        }

        private async Task<ParallelScenarioResult> ExecuteScenarioWithDiagnosticsAsync(
            int scenarioNumber,
            int parallelism,
            string scenarioId)
        {
            var stopwatch = Stopwatch.StartNew();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"# PARALLEL SCENARIO {scenarioNumber}/{parallelism} - START");

            _output.WriteLine(
                $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                $"ScenarioId='{scenarioId}', " +
                $"StartedAtUtc='{DateTimeOffset.UtcNow:O}'.");

            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();

                stopwatch.Stop();

                _output.WriteLine(
                    $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{scenarioId}', " +
                    $"Outcome='PASSED', " +
                    $"Duration='{stopwatch.Elapsed}'.");

                return new ParallelScenarioResult(
                    scenarioNumber,
                    scenarioId,
                    stopwatch.Elapsed,
                    Exception: null);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();

                _output.WriteLine(
                    $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{scenarioId}', " +
                    $"Outcome='FAILED', " +
                    $"Duration='{stopwatch.Elapsed}', " +
                    $"ExceptionType='{exception.GetType().FullName}', " +
                    $"Message='{exception.Message}'.");

                return new ParallelScenarioResult(
                    scenarioNumber,
                    scenarioId,
                    stopwatch.Elapsed,
                    exception);
            }
        }

        private sealed record ParallelScenarioResult(
            int ScenarioNumber,
            string ScenarioId,
            TimeSpan Duration,
            Exception? Exception);
    }
}