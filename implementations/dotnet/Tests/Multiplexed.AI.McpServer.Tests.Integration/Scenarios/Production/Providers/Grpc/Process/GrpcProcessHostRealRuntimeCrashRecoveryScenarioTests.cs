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

        /// <inheritdoc />
        protected override TimeSpan? ParallelHarnessUnsafeTimeoutOverride =>
            TimeSpan.FromMinutes(3);

        /// <inheritdoc />
        protected override TimeSpan? DirectScenarioUnsafeTimeoutOverride =>
            TimeSpan.FromMinutes(3);

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
        /// Verifies that multiple multi-tenant crash-recovery scenarios can execute concurrently.
        /// </summary>
        /// <param name="parallelism">The number of scenarios executed concurrently.</param>
        /// <returns>A task that completes when all concurrent scenarios have finished.</returns>
        [Theory]
        [InlineData(20)]
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
        [InlineData(5)]
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

        /// <summary>
        /// Verifies that multiple multi-tenant crash-recovery scenarios can execute
        /// under progressively increasing parallel load without cross-scenario interference.
        /// </summary>
        /// <param name="maximumParallelism">
        /// The maximum parallelism reached by the progressive load test.
        /// The test increases parallelism in increments of five.
        /// </param>
        /// <returns>
        /// A task that completes when every progressive parallelism level has been validated.
        /// </returns>
        [Theory]
        [InlineData(50)]
        public async Task Grpc_ProcessHost_Should_Progressively_Increase_MultiTenant_Crash_Recovery_Parallelism(
            int maximumParallelism)
        {
            const int parallelismIncrement = 10;
            const int maximumRetriesPerLevel = 3;

            var retryCooldown =
                TimeSpan.FromSeconds(10);

            var levelCooldown =
                TimeSpan.FromSeconds(10);

            ArgumentOutOfRangeException.ThrowIfLessThan(
                maximumParallelism,
                parallelismIncrement);

            if (maximumParallelism % parallelismIncrement != 0)
            {
                throw new ArgumentException(
                    $"Maximum parallelism '{maximumParallelism}' must be divisible by " +
                    $"the parallelism increment '{parallelismIncrement}'.",
                    nameof(maximumParallelism));
            }

            var overallStopwatch =
                Stopwatch.StartNew();

            var levelCount =
                maximumParallelism / parallelismIncrement;

            var completedLevels = 0;
            var totalAttempts = 0;
            var totalExecutedScenarios = 0;
            var highestValidatedParallelism = 0;

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                "# PROGRESSIVE PARALLEL CRASH-RECOVERY LOAD TEST - START");

            _output.WriteLine(
                $"[PROGRESSIVE LOAD SUMMARY] " +
                $"MinimumParallelism='{parallelismIncrement}', " +
                $"MaximumParallelism='{maximumParallelism}', " +
                $"ParallelismIncrement='{parallelismIncrement}', " +
                $"LevelCount='{levelCount}', " +
                $"MaximumRetriesPerLevel='{maximumRetriesPerLevel}', " +
                $"MaximumAttemptsPerLevel='{maximumRetriesPerLevel + 1}'.");

            for (var currentParallelism = parallelismIncrement;
                 currentParallelism <= maximumParallelism;
                 currentParallelism += parallelismIncrement)
            {
                var levelStopwatch =
                    Stopwatch.StartNew();

                var levelFailures =
                    new List<Exception>();

                var levelPassed = false;

                _output.WriteLine(string.Empty);
                _output.WriteLine(
                    $"# PROGRESSIVE LOAD LEVEL - PARALLELISM {currentParallelism}");

                _output.WriteLine(
                    $"[PROGRESSIVE LOAD LEVEL START] " +
                    $"Parallelism='{currentParallelism}', " +
                    $"Level='{completedLevels + 1}/{levelCount}', " +
                    $"ExpectedTenantsPerAttempt='{currentParallelism * 3}', " +
                    $"ExpectedSubmittedRunsPerAttempt='{currentParallelism * 9}', " +
                    $"ExpectedImpactedTenantsPerAttempt='{currentParallelism * 2}', " +
                    $"ExpectedSafeTenantsPerAttempt='{currentParallelism}'.");

                for (var retry = 0;
                     retry <= maximumRetriesPerLevel;
                     retry++)
                {
                    var attemptNumber =
                        retry + 1;

                    var maximumAttempts =
                        maximumRetriesPerLevel + 1;

                    var attemptStopwatch =
                        Stopwatch.StartNew();

                    totalAttempts++;
                    totalExecutedScenarios += currentParallelism;

                    _output.WriteLine(string.Empty);
                    _output.WriteLine(
                        $"[PROGRESSIVE LOAD ATTEMPT START] " +
                        $"Parallelism='{currentParallelism}', " +
                        $"Attempt='{attemptNumber}/{maximumAttempts}', " +
                        $"Retry='{retry}/{maximumRetriesPerLevel}'.");

                    try
                    {
                        await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                                currentParallelism)
                            .ConfigureAwait(false);

                        attemptStopwatch.Stop();
                        levelStopwatch.Stop();

                        levelPassed = true;
                        completedLevels++;
                        highestValidatedParallelism = currentParallelism;

                        _output.WriteLine(
                            $"[PROGRESSIVE LOAD ATTEMPT PASS] " +
                            $"Parallelism='{currentParallelism}', " +
                            $"Attempt='{attemptNumber}/{maximumAttempts}', " +
                            $"Retry='{retry}/{maximumRetriesPerLevel}', " +
                            $"AttemptDuration='{attemptStopwatch.Elapsed}', " +
                            $"LevelDuration='{levelStopwatch.Elapsed}'.");

                        break;
                    }
                    catch (Exception exception)
                    {
                        attemptStopwatch.Stop();

                        var wrappedException =
                            new InvalidOperationException(
                                $"Progressive gRPC process-host crash-recovery attempt " +
                                $"'{attemptNumber}/{maximumAttempts}' failed at parallelism " +
                                $"'{currentParallelism}' after '{attemptStopwatch.Elapsed}'.",
                                exception);

                        levelFailures.Add(
                            wrappedException);

                        _output.WriteLine(
                            $"[PROGRESSIVE LOAD ATTEMPT FAIL] " +
                            $"Parallelism='{currentParallelism}', " +
                            $"Attempt='{attemptNumber}/{maximumAttempts}', " +
                            $"Retry='{retry}/{maximumRetriesPerLevel}', " +
                            $"Duration='{attemptStopwatch.Elapsed}', " +
                            $"ExceptionType='{exception.GetType().FullName}', " +
                            $"Message='{exception.Message}'.");

                        _output.WriteLine(
                            exception.ToString());

                        if (retry < maximumRetriesPerLevel)
                        {
                            _output.WriteLine(
                                $"[PROGRESSIVE LOAD RETRY COOLDOWN] " +
                                $"Parallelism='{currentParallelism}', " +
                                $"FailedAttempt='{attemptNumber}/{maximumAttempts}', " +
                                $"NextAttempt='{attemptNumber + 1}/{maximumAttempts}', " +
                                $"Duration='{retryCooldown}'.");

                            await Task
                                .Delay(retryCooldown)
                                .ConfigureAwait(false);
                        }
                    }
                }

                if (!levelPassed)
                {
                    levelStopwatch.Stop();
                    overallStopwatch.Stop();

                    _output.WriteLine(string.Empty);
                    _output.WriteLine(
                        "# PROGRESSIVE PARALLEL CRASH-RECOVERY LOAD TEST - STOPPED");

                    _output.WriteLine(
                        $"[PROGRESSIVE LOAD TERMINAL FAILURE] " +
                        $"FailedParallelism='{currentParallelism}', " +
                        $"Attempts='{maximumRetriesPerLevel + 1}', " +
                        $"CompletedLevels='{completedLevels}/{levelCount}', " +
                        $"HighestValidatedParallelism='{highestValidatedParallelism}', " +
                        $"TotalAttempts='{totalAttempts}', " +
                        $"TotalExecutedScenarios='{totalExecutedScenarios}', " +
                        $"LevelDuration='{levelStopwatch.Elapsed}', " +
                        $"TotalDuration='{overallStopwatch.Elapsed}'.");

                    throw new AggregateException(
                        $"Progressive gRPC process-host crash-recovery load test stopped at " +
                        $"parallelism '{currentParallelism}'. The level failed after one initial " +
                        $"attempt and '{maximumRetriesPerLevel}' retries. The highest validated " +
                        $"parallelism was '{highestValidatedParallelism}'.",
                        levelFailures);
                }

                _output.WriteLine(
                    $"[PROGRESSIVE LOAD LEVEL PASS] " +
                    $"Parallelism='{currentParallelism}', " +
                    $"CompletedLevels='{completedLevels}/{levelCount}', " +
                    $"AttemptsUsed='{levelFailures.Count + 1}', " +
                    $"FailuresBeforePass='{levelFailures.Count}', " +
                    $"Duration='{levelStopwatch.Elapsed}'.");

                if (currentParallelism < maximumParallelism)
                {
                    _output.WriteLine(
                        $"[PROGRESSIVE LOAD LEVEL COOLDOWN] " +
                        $"CompletedParallelism='{currentParallelism}', " +
                        $"NextParallelism='{currentParallelism + parallelismIncrement}', " +
                        $"Duration='{levelCooldown}'.");

                    await Task
                        .Delay(levelCooldown)
                        .ConfigureAwait(false);
                }
            }

            overallStopwatch.Stop();

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                "# PROGRESSIVE PARALLEL CRASH-RECOVERY LOAD TEST - FINAL SUMMARY");

            _output.WriteLine(
                $"[PROGRESSIVE LOAD FINAL SUMMARY] " +
                $"MinimumParallelism='{parallelismIncrement}', " +
                $"MaximumParallelism='{maximumParallelism}', " +
                $"HighestValidatedParallelism='{highestValidatedParallelism}', " +
                $"CompletedLevels='{completedLevels}/{levelCount}', " +
                $"TotalAttempts='{totalAttempts}', " +
                $"TotalExecutedScenarios='{totalExecutedScenarios}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");
        }
    }
}