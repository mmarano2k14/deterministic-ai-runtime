using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Kubernetes
{
    /// <summary>
    /// Runs the gRPC Kubernetes SDK pod crash recovery scenario,
    /// parallel concurrency proofs, and repeated stability iterations.
    /// </summary>
    public sealed class GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests
        : ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesSdkPodCrashRecoveryScenarioRuntimeProfile())
        {
            this.output = output
                ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that a Kubernetes-hosted gRPC runtime pod failure is recovered
        /// with durable DAG resume.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_KubernetesSdk_Should_Requeue_Real_InFlight_Dag_After_Runtime_Pod_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }

        /// <summary>
        /// Verifies that two tenants can recover real Kubernetes-hosted gRPC runtime
        /// pod crashes with strict DAG resume, forensics, replay, ledger, trace,
        /// inventory proof, and no cross-tenant recovery leak.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_KubernetesSdk_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies that two impacted tenants recover real Kubernetes-hosted gRPC
        /// runtime pod crashes while a third safe tenant continues normal execution
        /// without recovery, forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_KubernetesSdk_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Runs ten sequential stability iterations and reports every failed iteration
        /// instead of stopping after the first failure.
        /// </summary>
        /// <returns>A task that completes when all stability iterations have finished.</returns>
        [Fact]
        public async Task Grpc_KubernetesSdk_Should_Requeue_Real_InFlight_Dag_After_Runtime_Pod_Kill_Stability_10x()
        {
            var failures =
                new List<string>();

            for (var iteration = 1;
                 iteration <= 10;
                 iteration++)
            {
                this.output.WriteLine(
                    $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] " +
                    $"Starting iteration {iteration}/10.");

                try
                {
                    await ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
                        .ConfigureAwait(false);

                    this.output.WriteLine(
                        $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] " +
                        $"Completed iteration {iteration}/10.");
                }
                catch (Exception exception)
                {
                    var failure =
                        $"Iteration {iteration}/10 failed. " +
                        $"ExceptionType='{exception.GetType().FullName}', " +
                        $"Message='{exception.Message}'.";

                    failures.Add(
                        failure);

                    this.output.WriteLine(
                        $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] " +
                        $"{failure}");
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"gRPC Kubernetes SDK pod crash recovery stability failed. " +
                    $"FailedIterations={failures.Count}/10. " +
                    $"{string.Join(" | ", failures)}");
            }
        }

        /// <summary>
        /// Verifies that multiple isolated gRPC Kubernetes SDK multi-tenant
        /// pod crash-recovery scenarios can execute concurrently without
        /// cross-scenario or cross-tenant leakage.
        /// </summary>
        /// <param name="parallelism">
        /// The number of complete multi-tenant pod crash-recovery scenarios
        /// to execute concurrently.
        /// </param>
        /// <returns>
        /// A task that completes when every parallel scenario has finished.
        /// </returns>
        [Theory]
        [InlineData(4)]
        public Task Grpc_KubernetesSdk_Should_Execute_MultiTenant_Pod_Crash_Recovery_Scenarios_In_Parallel(
            int parallelism)
        {
            return ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                parallelism);
        }

        /// <summary>
        /// Verifies repeatedly that multiple gRPC Kubernetes SDK multi-tenant
        /// pod crash-recovery scenarios can execute concurrently without
        /// cross-scenario interference.
        /// </summary>
        /// <param name="parallelism">
        /// The number of pod crash-recovery scenarios executed concurrently
        /// during each iteration.
        /// </param>
        /// <returns>
        /// A task that completes when all parallel stability iterations have finished.
        /// </returns>
        [Theory]
        [InlineData(10)]
        public async Task Grpc_KubernetesSdk_Should_Execute_MultiTenant_Pod_Crash_Recovery_Scenarios_In_Parallel_Loop(
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

            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# GRPC KUBERNETES SDK PARALLEL POD CRASH-RECOVERY STABILITY LOOP " +
                $"- STARTING {iterationCount} ITERATIONS");

            this.output.WriteLine(
                $"[GRPC K8S SDK PARALLEL STABILITY SUMMARY] " +
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

                this.output.WriteLine(string.Empty);
                this.output.WriteLine(
                    $"# GRPC KUBERNETES SDK PARALLEL POD CRASH-RECOVERY " +
                    $"STABILITY ITERATION {iteration}/{iterationCount}");

                try
                {
                    await ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
                            parallelism)
                        .ConfigureAwait(false);

                    iterationStopwatch.Stop();

                    this.output.WriteLine(
                        $"[GRPC K8S SDK PARALLEL STABILITY PASS] " +
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
                            $"Parallel gRPC Kubernetes SDK pod crash-recovery " +
                            $"stability iteration {iteration}/{iterationCount} " +
                            $"with parallelism '{parallelism}' failed after " +
                            $"'{iterationStopwatch.Elapsed}'.",
                            exception);

                    failures.Add(
                        wrappedException);

                    this.output.WriteLine(
                        $"[GRPC K8S SDK PARALLEL STABILITY FAIL] " +
                        $"Iteration='{iteration}', " +
                        $"IterationCount='{iterationCount}', " +
                        $"Parallelism='{parallelism}', " +
                        $"Duration='{iterationStopwatch.Elapsed}', " +
                        $"ExceptionType='{exception.GetType().FullName}', " +
                        $"Message='{exception.Message}'.");

                    this.output.WriteLine(
                        exception.ToString());
                }
            }

            overallStopwatch.Stop();

            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                "# GRPC KUBERNETES SDK PARALLEL POD CRASH-RECOVERY " +
                "STABILITY LOOP - FINAL SUMMARY");

            this.output.WriteLine(
                $"[GRPC K8S SDK PARALLEL STABILITY FINAL SUMMARY] " +
                $"Iterations='{iterationCount}', " +
                $"ParallelismPerIteration='{parallelism}', " +
                $"TotalScenarios='{iterationCount * parallelism}', " +
                $"PassedIterations='{iterationCount - failures.Count}', " +
                $"FailedIterations='{failures.Count}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    $"{failures.Count} of {iterationCount} parallel gRPC Kubernetes SDK " +
                    $"pod crash-recovery stability iterations failed with parallelism " +
                    $"'{parallelism}'.",
                    failures);
            }
        }
    }
}