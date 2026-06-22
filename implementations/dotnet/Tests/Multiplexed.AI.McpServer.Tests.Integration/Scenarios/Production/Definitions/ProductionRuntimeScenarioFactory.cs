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
                AssertReplayLedgerTrace = true,
                AssertRetention = true,
                AssertMaxRuntimeInstances = true,
                AssertTenantIsolation = true,
                Tenants =
                [
                    new ProductionTenantScenarioDefinition
                    {
                        TenantId = "tenant-a",
                        TenantGroupId = "tenant-group-a",
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
    }
}