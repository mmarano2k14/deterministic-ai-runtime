using System;
using System.Linq;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process
{
    /// <summary>
    /// Contains production-grade HTTP process-host runtime scenario tests.
    /// </summary>
    public sealed class HttpProcessHostProductionScenarioTests
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostProductionScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostProductionScenarioTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Verifies that a small HTTP process-host scenario can persist ledger, trace,
        /// replay metadata, replay ledger, and replay timeline data across the process boundary.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Persist_Ledger_ReplayMetadata_And_Trace_Across_Process_Boundary()
        {
            var baseScenario = ProductionRuntimeScenarioFactory.CreateMultiTenantCapacityReplayLedgerScenario();
            var tenant = baseScenario.Tenants.First();

            var scenario =
                baseScenario with
                {
                    Name = "process-boundary-ledger-replay-trace",
                    ControlPlaneIdPrefix = "process-boundary-ledger-replay-trace",
                    Tenants = new[]
                    {
                        tenant with
                        {
                            Run = tenant.Run with
                            {
                                RunCount = 1,
                                StepCount = 5,
                                DelayMs = 1,
                                FlakyStepInterval = 0,
                                EnableRetention = true
                            }
                        }
                    },
                    PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                    ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                    HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                    SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,
                    ScaleOutTimeout = TimeSpan.FromMinutes(2),
                    DispatchTimeout = TimeSpan.FromMinutes(2),
                    CompletionTimeout = TimeSpan.FromMinutes(3),
                    Assertions = new ProductionRuntimeScenarioAssertionOptions
                    {
                        AssertAllRunsCompleted = true,
                        AssertTenantIsolation = true,
                        AssertScaleOut = true,
                        AssertMaxRuntimeInstances = true,
                        AssertLedger = true,
                        AssertTrace = true,
                        AssertReplayReport = true,
                        AssertReplayLedger = true,
                        AssertReplayTrace = true
                    }
                };

            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

        /// <summary>
        /// Verifies that the HTTP process-host provider respects Dedicated, Shared, and Hybrid tenant runtime modes.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Respect_Dedicated_Shared_Hybrid_Tenant_Runtime_Modes()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateDedicatedSharedHybridRuntimeModeScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

        /// <summary>
        /// Verifies that the HTTP process-host provider runs a single tenant in Dedicated runtime mode.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_SingleTenant_Dedicated_Runtime_Mode()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

        /// <summary>
        /// Verifies that the HTTP process-host provider runs a single tenant in Shared runtime mode.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_SingleTenant_Shared_Runtime_Mode()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

        /// <summary>
        /// Verifies that the existing single-tenant Shared process-host scenario can opt in to exactly one nested
        /// child DAG without changing the historical zero-depth scenario contract.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_One()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneScenario();

            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
        }

        /// <summary>
        /// Verifies that the existing single-tenant Shared process-host scenario composes exactly two nested child
        /// DAG levels and deterministically converges both durable continuations back to the submitted parent.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_Two()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthTwoScenario();

            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
        }

        /// <summary>
        /// Verifies that a depth-one child execution survives a real external process kill while its parent is
        /// durably parked, preserving the same ChildExecutionId and resuming on different physical runtime capacity.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Child_Dag_Depth_One_After_Real_Child_Runtime_Process_Kill()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneRuntimeCrashRecoveryScenario();

            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
            ProductionChildDagAssertions.AssertRuntimeFailureRecovery(scenario, result);
        }

        /// <summary>
        /// Verifies that a depth-two child chain survives a real process kill of the intermediate first-level child,
        /// preserves that child execution identity on replacement runtime capacity, then composes the second-level
        /// child and deterministically cascades both durable continuations back to the submitted parent.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Intermediate_Child_Dag_Depth_Two_After_Real_Runtime_Process_Kill()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthTwoIntermediateRuntimeCrashRecoveryScenario();

            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
            ProductionChildDagAssertions.AssertRuntimeFailureRecovery(scenario, result);
        }

        /// <summary>
        /// Verifies that the HTTP process-host provider runs a single tenant in Hybrid runtime mode.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_SingleTenant_Hybrid_Runtime_Mode()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateSingleTenantHybridRuntimeModeScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

        /// <summary>
        /// Verifies that multiple Dedicated tenants are isolated from each other's runtime capacity.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Isolate_Multiple_Dedicated_Tenants()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateMultiTenantDedicatedIsolationScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
        }

    }
}
