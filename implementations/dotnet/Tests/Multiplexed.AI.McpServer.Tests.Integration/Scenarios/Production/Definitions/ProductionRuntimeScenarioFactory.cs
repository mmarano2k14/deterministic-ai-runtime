using System;
using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Creates reusable provider-agnostic production runtime scenarios.
    /// </summary>
    public static class ProductionRuntimeScenarioFactory
    {
        /// <summary>
        /// Creates a multi-tenant capacity, retention, replay, ledger, and trace scenario.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateMultiTenantCapacityReplayLedgerScenario()
        {
            return new ProductionRuntimeScenarioDefinition
            {
                Name = "multi-tenant-capacity-replay-ledger",
                ControlPlaneIdPrefix = "production-multi-tenant-capacity",

                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,

                AssertReplayLedgerTrace = true,
                AssertRetention = true,
                AssertMaxRuntimeInstances = true,
                AssertTenantIsolation = true,

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
                },

                Tenants =
                [
                    new ProductionTenantScenarioDefinition
                    {
                        TenantId = "tenant-a",
                        TenantGroupId = "tenant-group-a",
                        RuntimeMode = ProductionTenantRuntimeMode.Dedicated,
                        RuntimeInstanceIdPrefix = "tenant-a-runtime",
                        MaxRuntimeInstances = 2,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 20,
                        ExpectDedicatedRuntimePrefix = true,
                        ExpectCapacityOverflow = true,
                        Run = new ProductionRunScenarioDefinition
                        {
                            RunCount = 4,
                            StepCount = 30,
                            DelayMs = 150,
                            FlakyStepInterval = 0,
                            EnableRetention = true,
                            Input = new Dictionary<string, object?>
                            {
                                ["scenario"] = "multi-tenant-capacity-replay-ledger",
                                ["tenant"] = "tenant-a",
                                ["delayMs"] = 150
                            }
                        }
                    },
                    new ProductionTenantScenarioDefinition
                    {
                        TenantId = "tenant-b",
                        TenantGroupId = "tenant-group-b",
                        RuntimeMode = ProductionTenantRuntimeMode.Dedicated,
                        RuntimeInstanceIdPrefix = "tenant-b-runtime",
                        MaxRuntimeInstances = 2,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 20,
                        ExpectDedicatedRuntimePrefix = true,
                        ExpectCapacityOverflow = true,
                        Run = new ProductionRunScenarioDefinition
                        {
                            RunCount = 4,
                            StepCount = 30,
                            DelayMs = 150,
                            FlakyStepInterval = 0,
                            EnableRetention = true,
                            Input = new Dictionary<string, object?>
                            {
                                ["scenario"] = "multi-tenant-capacity-replay-ledger",
                                ["tenant"] = "tenant-b",
                                ["delayMs"] = 150
                            }
                        }
                    }
                ]
            };
        }

        /// <summary>
        /// Creates a lightweight scenario that validates Dedicated, Shared, and Hybrid tenant runtime modes.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        /// <remarks>
        /// This scenario focuses on tenant runtime mode propagation and routing behavior.
        /// It intentionally disables replay, ledger, and trace assertions because those
        /// are already validated by the process-boundary persistence scenario.
        /// </remarks>
        public static ProductionRuntimeScenarioDefinition CreateDedicatedSharedHybridRuntimeModeScenario()
        {
            return new ProductionRuntimeScenarioDefinition
            {
                Name = "dedicated-shared-hybrid-runtime-mode",
                ControlPlaneIdPrefix = "production-tenant-runtime-mode",

                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,

                AssertReplayLedgerTrace = false,
                AssertRetention = false,
                AssertMaxRuntimeInstances = true,
                AssertTenantIsolation = true,

                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(2),
                CompletionTimeout = TimeSpan.FromMinutes(3),

                Assertions = new ProductionRuntimeScenarioAssertionOptions
                {
                    AssertAllRunsCompleted = true,
                    AssertTenantIsolation = true,
                    AssertScaleOut = true,
                    AssertMaxRuntimeInstances = true,
                    AssertLedger = false,
                    AssertTrace = false,
                    AssertReplayReport = false,
                    AssertReplayLedger = false,
                    AssertReplayTrace = false
                },

                Tenants =
                [
                    CreateRuntimeModeTenant(
                        tenantId: "tenant-dedicated",
                        tenantGroupId: "tenant-mode-group-dedicated",
                        runtimeMode: ProductionTenantRuntimeMode.Dedicated,
                        runtimeInstanceIdPrefix: "tenant-dedicated-runtime",
                        expectDedicatedRuntimePrefix: true),

                    CreateRuntimeModeTenant(
                        tenantId: "tenant-shared",
                        tenantGroupId: "tenant-mode-group-shared",
                        runtimeMode: ProductionTenantRuntimeMode.Shared,
                        runtimeInstanceIdPrefix: "tenant-shared-runtime",
                        expectDedicatedRuntimePrefix: false),

                    CreateRuntimeModeTenant(
                        tenantId: "tenant-hybrid",
                        tenantGroupId: "tenant-mode-group-hybrid",
                        runtimeMode: ProductionTenantRuntimeMode.Hybrid,
                        runtimeInstanceIdPrefix: "tenant-hybrid-runtime",
                        expectDedicatedRuntimePrefix: true)
                ]
            };
        }

        /// <summary>
        /// Creates a focused single-tenant Dedicated runtime mode scenario.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateSingleTenantDedicatedRuntimeModeScenario()
        {
            return CreateFocusedRuntimeModeScenario(
                name: "single-tenant-dedicated-runtime-mode",
                controlPlaneIdPrefix: "production-single-tenant-dedicated",
                tenants:
                [
                    CreateRuntimeModeTenant(
                tenantId: "tenant-dedicated-single",
                tenantGroupId: "tenant-mode-group-dedicated-single",
                runtimeMode: ProductionTenantRuntimeMode.Dedicated,
                runtimeInstanceIdPrefix: "tenant-dedicated-single-runtime",
                expectDedicatedRuntimePrefix: true)
                ]);
        }

        /// <summary>
        /// Creates a focused single-tenant Shared runtime mode scenario.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateSingleTenantSharedRuntimeModeScenario()
        {
            return CreateFocusedRuntimeModeScenario(
                name: "single-tenant-shared-runtime-mode",
                controlPlaneIdPrefix: "production-single-tenant-shared",
                tenants:
                [
                    CreateRuntimeModeTenant(
                tenantId: "tenant-shared-single",
                tenantGroupId: "tenant-mode-group-shared-single",
                runtimeMode: ProductionTenantRuntimeMode.Shared,
                runtimeInstanceIdPrefix: "tenant-shared-single-runtime",
                expectDedicatedRuntimePrefix: false)
                ]);
        }

        /// <summary>
        /// Creates a focused single-tenant Hybrid runtime mode scenario.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateSingleTenantHybridRuntimeModeScenario()
        {
            return CreateFocusedRuntimeModeScenario(
                name: "single-tenant-hybrid-runtime-mode",
                controlPlaneIdPrefix: "production-single-tenant-hybrid",
                tenants:
                [
                    CreateRuntimeModeTenant(
                tenantId: "tenant-hybrid-single",
                tenantGroupId: "tenant-mode-group-hybrid-single",
                runtimeMode: ProductionTenantRuntimeMode.Hybrid,
                runtimeInstanceIdPrefix: "tenant-hybrid-single-runtime",
                expectDedicatedRuntimePrefix: true)
                ]);
        }

        /// <summary>
        /// Creates a focused multi-tenant Dedicated isolation scenario.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        /// <remarks>
        /// The scenario executes tenants sequentially so that the first dedicated tenant
        /// creates runtime capacity before the second dedicated tenant submits work.
        /// This makes the scenario adversarial: the second tenant must not reuse the first
        /// tenant's dedicated runtime capacity.
        /// </remarks>
        public static ProductionRuntimeScenarioDefinition CreateMultiTenantDedicatedIsolationScenario()
        {
            return CreateFocusedRuntimeModeScenario(
                name: "multi-tenant-dedicated-isolation",
                controlPlaneIdPrefix: "production-multi-tenant-dedicated-isolation",
                tenants:
                [
                    CreateRuntimeModeTenant(
                        tenantId: "tenant-dedicated-a",
                        tenantGroupId: "tenant-mode-group-dedicated-a",
                        runtimeMode: ProductionTenantRuntimeMode.Dedicated,
                        runtimeInstanceIdPrefix: "tenant-dedicated-a-runtime",
                        expectDedicatedRuntimePrefix: true),

                    CreateRuntimeModeTenant(
                        tenantId: "tenant-dedicated-b",
                        tenantGroupId: "tenant-mode-group-dedicated-b",
                        runtimeMode: ProductionTenantRuntimeMode.Dedicated,
                        runtimeInstanceIdPrefix: "tenant-dedicated-b-runtime",
                        expectDedicatedRuntimePrefix: true)
                ])
                with
                {
                    RunTenantsSequentially = true
                };
        }

        /// <summary>
        /// Creates a focused runtime mode scenario.
        /// </summary>
        /// <param name="name">The scenario name.</param>
        /// <param name="controlPlaneIdPrefix">The control-plane id prefix.</param>
        /// <param name="tenants">The tenant definitions.</param>
        /// <returns>The production runtime scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateFocusedRuntimeModeScenario(
            string name,
            string controlPlaneIdPrefix,
            IReadOnlyList<ProductionTenantScenarioDefinition> tenants)
        {
            return new ProductionRuntimeScenarioDefinition
            {
                Name = name,
                ControlPlaneIdPrefix = controlPlaneIdPrefix,

                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,

                AssertReplayLedgerTrace = false,
                AssertRetention = false,
                AssertMaxRuntimeInstances = true,
                AssertTenantIsolation = true,

                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(2),
                CompletionTimeout = TimeSpan.FromMinutes(3),

                Assertions = new ProductionRuntimeScenarioAssertionOptions
                {
                    AssertAllRunsCompleted = true,
                    AssertTenantIsolation = true,
                    AssertScaleOut = true,
                    AssertMaxRuntimeInstances = true,
                    AssertLedger = false,
                    AssertTrace = false,
                    AssertReplayReport = false,
                    AssertReplayLedger = false,
                    AssertReplayTrace = false
                },

                Tenants = tenants
            };
        }

        /// <summary>
        /// Creates one tenant definition for the runtime mode scenario.
        /// </summary>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        /// <param name="runtimeMode">The expected runtime mode.</param>
        /// <param name="runtimeInstanceIdPrefix">The expected runtime instance id prefix.</param>
        /// <param name="expectDedicatedRuntimePrefix">Whether the tenant must execute on its own runtime prefix.</param>
        /// <returns>The tenant scenario definition.</returns>
        private static ProductionTenantScenarioDefinition CreateRuntimeModeTenant(
            string tenantId,
            string tenantGroupId,
            ProductionTenantRuntimeMode runtimeMode,
            string runtimeInstanceIdPrefix,
            bool expectDedicatedRuntimePrefix)
        {
            return new ProductionTenantScenarioDefinition
            {
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                RuntimeMode = runtimeMode,
                RuntimeInstanceIdPrefix = runtimeInstanceIdPrefix,
                MaxRuntimeInstances = 1,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 10,
                ExpectDedicatedRuntimePrefix = expectDedicatedRuntimePrefix,
                ExpectCapacityOverflow = false,
                Run = new ProductionRunScenarioDefinition
                {
                    RunCount = 1,
                    StepCount = 5,
                    DelayMs = 1,
                    FlakyStepInterval = 0,
                    EnableRetention = false,
                    Input = new Dictionary<string, object?>
                    {
                        ["scenario"] = "dedicated-shared-hybrid-runtime-mode",
                        ["tenant"] = tenantId,
                        ["tenantRuntimeMode"] = runtimeMode.ToString(),
                        ["delayMs"] = 1
                    }
                }
            };
        }
    }
}