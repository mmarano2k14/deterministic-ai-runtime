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