using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process
{
    /// <summary>
    /// Runs repeated stability iterations for the HTTP process-host DAG resume recovery scenario tests.
    /// </summary>
    public sealed class HttpProcessHostDagResumeRecoveryScenarioStabilityLoopTests
    {
        private readonly HttpProcessHostDagResumeRecoveryScenarioTests scenarioTests;
        private readonly HttpProcessHostConcurrentRuntimeRecoveryScenarioTests concurrentRuntimeRecoveryScenarioTests;
        private readonly HttpProcessHostRealRuntimeCrashRecoveryScenarioTests realRuntimeCrashRecoveryScenarioTests;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostDagResumeRecoveryScenarioStabilityLoopTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostDagResumeRecoveryScenarioStabilityLoopTests(
            ITestOutputHelper output)
        {
            this.Output = output ?? throw new ArgumentNullException(nameof(output));
            this.scenarioTests = new HttpProcessHostDagResumeRecoveryScenarioTests(output);
            this.concurrentRuntimeRecoveryScenarioTests = new HttpProcessHostConcurrentRuntimeRecoveryScenarioTests(output);
            this.realRuntimeCrashRecoveryScenarioTests = new HttpProcessHostRealRuntimeCrashRecoveryScenarioTests(output);
        }

        /// <summary>
        /// Gets the test output helper.
        /// </summary>
        protected ITestOutputHelper Output { get; }

        /// <summary>
        /// Repeatedly verifies the HTTP process-host DAG resume recovery forensics MCP timeline scenario.
        /// </summary>
        /// <param name="iteration">The stability loop iteration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        public async Task Http_ProcessHost_Should_Expose_Dag_Resume_Recovery_Forensics_Timeline_Through_Mcp_StabilityLoop(
            int iteration)
        {
            this.Output.WriteLine(
                $"[FORENSICS TIMELINE STABILITY LOOP] Iteration='{iteration}'.");

            await this.scenarioTests
                .Http_ProcessHost_Should_Expose_Dag_Resume_Recovery_Forensics_Timeline_Through_Mcp()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Repeatedly verifies that the real HTTP runtime creation path persists the RBAC ContextKey
        /// on the durable DAG execution record.
        /// </summary>
        /// <param name="iteration">The stability loop iteration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        public async Task Http_ProcessHost_Should_Persist_Dag_Record_ContextKey_On_Real_Create_StabilityLoop(
            int iteration)
        {
            this.Output.WriteLine(
                $"[HTTP DAG CONTEXTKEY STABILITY LOOP] Iteration='{iteration}'.");

            await this.scenarioTests
                .Http_ProcessHost_Should_Persist_Dag_Record_ContextKey_On_Real_Create()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Repeatedly verifies that a failed HTTP runtime instance can expose and recover
        /// a durable assigned-work inventory containing queued and in-flight work.
        /// </summary>
        /// <param name="iteration">The stability loop iteration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task Http_ProcessHost_Should_Recover_Durable_Assigned_Work_Inventory_From_Failed_Runtime_StabilityLoop(
            int iteration)
        {
            this.Output.WriteLine(
                $"[HTTP RUNTIME INVENTORY STABILITY LOOP] Iteration='{iteration}'.");

            await this.scenarioTests
                .Http_ProcessHost_Should_Recover_Durable_Assigned_Work_Inventory_From_Failed_Runtime()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Repeatedly verifies that two failed tenants can recover concurrently while a third tenant remains untouched.
        /// </summary>
        /// <param name="iteration">The stability loop iteration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task Http_ProcessHost_Should_Recover_Two_Failed_Tenants_While_Leaving_Third_Tenant_Untouched_StabilityLoop(
            int iteration)
        {
            this.Output.WriteLine(
                $"[MULTI-TENANT SAFE RECOVERY STABILITY LOOP] Iteration='{iteration}'.");

            await this.concurrentRuntimeRecoveryScenarioTests
                .Http_ProcessHost_Should_Recover_Two_Failed_Tenants_While_Leaving_Third_Tenant_Untouched()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Repeatedly verifies that a real HTTP runtime process crash automatically resumes
        /// the original durable DAG execution on replacement runtime capacity.
        /// </summary>
        /// <param name="iteration">The stability loop iteration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        public async Task Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill_StabilityLoop(
            int iteration)
        {
            this.Output.WriteLine(
                $"[REAL RUNTIME CRASH RECOVERY STABILITY LOOP] Iteration='{iteration}'.");

            await this.realRuntimeCrashRecoveryScenarioTests
                .Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
                .ConfigureAwait(false);
        }
    }
}