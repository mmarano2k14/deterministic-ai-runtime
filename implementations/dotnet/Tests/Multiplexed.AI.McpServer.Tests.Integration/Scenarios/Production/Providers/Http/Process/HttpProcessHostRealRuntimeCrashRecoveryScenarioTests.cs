using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
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
        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostRealRuntimeCrashRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new HttpProcessHostScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight DAG execution resumes on a replacement runtime.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }

        /// <summary>
        /// Verifies that two tenants can recover real process-host runtime crashes with strict DAG resume,
        /// forensics, replay, ledger, trace, inventory proof, and no cross-tenant recovery leak.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }

        /// <summary>
        /// Verifies that two impacted tenants recover real process-host runtime crashes while a third safe tenant
        /// continues normal execution without recovery, forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            return ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace();
        }
    }
}