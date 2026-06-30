using System;
using System.Collections.Generic;
using System.Linq;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios
{
    /// <summary>
    /// Provides reusable production scenario definitions.
    /// </summary>
    public static class ProductionScenarioDefinitions
    {
        private const int DefaultConcurrentRecoveryStepCount = 100;
        private const int DefaultRealRuntimeCrashRecoveryStepCount = 5;
        private const int DefaultRuntimeStepDelayMs = 750;

        /// <summary>
        /// Creates a concurrent multi-instance recovery scenario.
        /// </summary>
        /// <param name="stepCount">The DAG step count.</param>
        /// <param name="delayMs">The per-step delay in milliseconds.</param>
        /// <param name="includeSafeTenant">Whether a safe witness tenant should be included.</param>
        /// <param name="completionTimeout">The optional completion timeout.</param>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateConcurrentMultiInstanceRecoveryScenario(
            int stepCount = DefaultConcurrentRecoveryStepCount,
            int delayMs = DefaultRuntimeStepDelayMs,
            bool includeSafeTenant = false,
            TimeSpan? completionTimeout = null)
        {
            return CreateConcurrentMultiInstanceRecoveryScenarioCore(
                stepCount,
                delayMs,
                includeSafeTenant) with
            {
                CompletionTimeout = completionTimeout ?? TimeSpan.FromMinutes(5)
            };
        }

        /// <summary>
        /// Creates a real runtime crash recovery scenario.
        /// </summary>
        /// <param name="stepCount">The DAG step count.</param>
        /// <param name="delayMs">The per-step delay in milliseconds.</param>
        /// <param name="includeSafeTenant">Whether a safe witness tenant should be included.</param>
        /// <param name="completionTimeout">The optional completion timeout.</param>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryScenario(
            int stepCount = DefaultRealRuntimeCrashRecoveryStepCount,
            int delayMs = DefaultRuntimeStepDelayMs,
            bool includeSafeTenant = false,
            TimeSpan? completionTimeout = null)
        {
            return CreateConcurrentMultiInstanceRecoveryScenarioCore(
                stepCount,
                delayMs,
                includeSafeTenant) with
            {
                Name = includeSafeTenant
                    ? "http-process-host-real-runtime-crash-recovery-safe-tenant"
                    : "http-process-host-real-runtime-crash-recovery",
                ControlPlaneIdPrefix = includeSafeTenant
                    ? "http-process-host-real-runtime-crash-recovery-safe-tenant"
                    : "http-process-host-real-runtime-crash-recovery",
                CompletionTimeout = completionTimeout ?? TimeSpan.FromMinutes(3)
            };
        }

        /// <summary>
        /// Creates a concurrent multi-instance recovery scenario with a safe witness tenant.
        /// </summary>
        /// <param name="stepCount">The DAG step count.</param>
        /// <param name="delayMs">The per-step delay in milliseconds.</param>
        /// <param name="completionTimeout">The optional completion timeout.</param>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateConcurrentMultiInstanceRecoveryWithSafeTenantScenario(
            int stepCount = DefaultConcurrentRecoveryStepCount,
            int delayMs = DefaultRuntimeStepDelayMs,
            TimeSpan? completionTimeout = null)
        {
            return CreateConcurrentMultiInstanceRecoveryScenario(
                stepCount,
                delayMs,
                includeSafeTenant: true,
                completionTimeout);
        }

        /// <summary>
        /// Creates the core concurrent multi-instance recovery scenario.
        /// </summary>
        /// <param name="stepCount">The DAG step count.</param>
        /// <param name="delayMs">The per-step delay in milliseconds.</param>
        /// <param name="includeSafeTenant">Whether a safe witness tenant should be included.</param>
        /// <returns>The production runtime scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateConcurrentMultiInstanceRecoveryScenarioCore(
            int stepCount,
            int delayMs,
            bool includeSafeTenant)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
            ArgumentOutOfRangeException.ThrowIfNegative(delayMs);

            var baseScenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var templateTenant =
                baseScenario.Tenants.Single();

            var tenantA =
                templateTenant with
                {
                    TenantId = "tenant-concurrent-a",
                    TenantGroupId = "tenant-concurrent-a-group",
                    RuntimeInstanceIdPrefix = "tenant-concurrent-a-runtime",
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = 1,
                        StepCount = stepCount,
                        DelayMs = delayMs,
                        FlakyStepInterval = 0,
                        EnableRetention = true
                    }
                };

            var tenantB =
                templateTenant with
                {
                    TenantId = "tenant-concurrent-b",
                    TenantGroupId = "tenant-concurrent-b-group",
                    RuntimeInstanceIdPrefix = "tenant-concurrent-b-runtime",
                    MaxRuntimeInstances = 2,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = 1,
                        StepCount = stepCount,
                        DelayMs = delayMs,
                        FlakyStepInterval = 0,
                        EnableRetention = true
                    }
                };

            var tenants =
                new List<ProductionTenantScenarioDefinition>
                {
                    tenantA,
                    tenantB
                };

            if (includeSafeTenant)
            {
                tenants.Add(
                    templateTenant with
                    {
                        TenantId = "tenant-concurrent-c",
                        TenantGroupId = "tenant-concurrent-c-group",
                        RuntimeInstanceIdPrefix = "tenant-concurrent-c-runtime",
                        MaxRuntimeInstances = 1,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 0,
                        Run = templateTenant.Run with
                        {
                            RunCount = 1,
                            StepCount = stepCount,
                            DelayMs = delayMs,
                            FlakyStepInterval = 0,
                            EnableRetention = true
                        }
                    });
            }

            return baseScenario with
            {
                Name = includeSafeTenant
                    ? "http-process-host-dag-resume-concurrent-runtime-recovery-safe-tenant"
                    : "http-process-host-dag-resume-concurrent-runtime-recovery",
                ControlPlaneIdPrefix = includeSafeTenant
                    ? "http-process-host-concurrent-runtime-recovery-safe-tenant"
                    : "http-process-host-concurrent-runtime-recovery",
                Tenants = tenants.ToArray(),
                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,
                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(3),
                CompletionTimeout = TimeSpan.FromMinutes(5),
                Assertions = new ProductionRuntimeScenarioAssertionOptions
                {
                    AssertAllRunsCompleted = true,
                    AssertTenantIsolation = true,
                    AssertScaleOut = true,
                    AssertMaxRuntimeInstances = true,
                    AssertLedger = true,
                    AssertTrace = true,
                    AssertReplayReport = false,
                    AssertReplayLedger = false,
                    AssertReplayTrace = false
                }
            };
        }
    }
}