using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Kubernetes
{
    /// <summary>
    /// Runs the gRPC Kubernetes SDK pod crash recovery scenario and repeated stability iterations.
    /// </summary>
    public sealed class GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests
        : ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesPodCrashRecoveryScenarioStabilityLoopTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesSdkPodCrashRecoveryScenarioRuntimeProfile())
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that a Kubernetes-hosted gRPC runtime failure is recovered with durable DAG resume.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_KubernetesSdk_Should_Requeue_Real_InFlight_Dag_After_Runtime_Pod_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }

        /// <summary>
        /// Runs ten stability iterations and reports all failed iterations instead of stopping at the first failure.
        /// </summary>
        /// <returns>A task that completes when all stability iterations have finished.</returns>
        [Fact]
        public async Task Grpc_KubernetesSdk_Should_Requeue_Real_InFlight_Dag_After_Runtime_Pod_Kill_Stability_10x()
        {
            var failures = new List<string>();

            for (var iteration = 1; iteration <= 10; iteration++)
            {
                this.output.WriteLine(
                    $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] Starting iteration {iteration}/10.");

                try
                {
                    await ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
                        .ConfigureAwait(false);

                    this.output.WriteLine(
                        $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] Completed iteration {iteration}/10.");
                }
                catch (Exception exception)
                {
                    var failure =
                        $"Iteration {iteration}/10 failed. ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.";

                    failures.Add(failure);

                    this.output.WriteLine(
                        $"[GRPC K8S SDK POD CRASH RECOVERY STABILITY] {failure}");
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"gRPC Kubernetes SDK pod crash recovery stability failed. FailedIterations={failures.Count}/10. {string.Join(" | ", failures)}");
            }
        }
    }
}