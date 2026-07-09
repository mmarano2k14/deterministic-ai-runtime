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
        /// Verifies that the HTTP provider with process-based host creation can execute
        /// the reusable production multi-tenant capacity, replay, ledger, and trace scenario.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_MultiTenant_Capacity_Replay_Ledger_Production_Scenario()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateMultiTenantCapacityReplayLedgerScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
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

            AssertScenarioResult(scenario, result);
        }

        /// <summary>
        /// Verifies that the HTTP process-host provider can run a full mixed-tenant production scenario
        /// with Dedicated, Shared, and Hybrid tenants while retention, ledger, trace, and replay assertions are enabled.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_MixedTenant_Full_Production_Validation_Scenario()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateMixedTenantFullProductionValidationScenario();
            var runner = new HttpProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            AssertScenarioResult(scenario, result);
        }

        /// <summary>
        /// Asserts a production runtime scenario result according to the scenario assertion options.
        /// </summary>
        /// <param name="scenario">The scenario definition.</param>
        /// <param name="result">The scenario result.</param>
        private static void AssertScenarioResult(
            ProductionRuntimeScenarioDefinition scenario,
            Results.ProductionRuntimeScenarioResult result)
        {
            ProductionRuntimeScenarioAssertions.AssertScenarioShape(scenario, result);

            if (scenario.Assertions.AssertAllRunsCompleted)
            {
                ProductionRuntimeScenarioAssertions.AssertAllRunsCompleted(scenario, result);
            }

            if (scenario.Assertions.AssertMaxRuntimeInstances)
            {
                ProductionCapacityAssertions.AssertMaxRuntimeInstancesWereRespected(scenario, result);
            }

            if (scenario.Assertions.AssertScaleOut)
            {
                ProductionCapacityAssertions.AssertFulfilledScaleOutRequestsHaveRuntimeInstanceIds(result);
                ProductionTenantRuntimeModeAssertions.AssertTenantRuntimeModesWerePropagated(scenario, result);
            }

            if (scenario.Assertions.AssertTenantIsolation)
            {
                ProductionTenantIsolationAssertions.AssertTenantRuntimePrefixesWereRespected(scenario, result);
                ProductionTenantIsolationAssertions.AssertNoCrossTenantRuntimePrefixUsage(scenario, result);
            }

            ProductionReplayLedgerAssertions.AssertReplayLedgerTraceAvailable(scenario, result);
        }
    }
}