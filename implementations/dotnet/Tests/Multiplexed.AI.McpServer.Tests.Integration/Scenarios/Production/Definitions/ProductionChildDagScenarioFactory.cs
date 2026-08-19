using System;
using System.Linq;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Creates reusable provider-agnostic production scenarios for deterministic child DAG composition.
    /// </summary>
    public static class ProductionChildDagScenarioFactory
    {
        /// <summary>
        /// Creates the focused single-tenant Shared runtime scenario with exactly one nested child DAG level.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateDepthOneScenario()
        {
            return CreateScenario(
                name: "single-tenant-shared-runtime-mode-child-depth-one",
                controlPlaneIdPrefix: "production-single-tenant-shared-child-depth-one",
                childDepth: 1,
                completionTimeout: TimeSpan.FromMinutes(5),
                childRuntimeFailure: null);
        }

        /// <summary>
        /// Creates the focused single-tenant Shared runtime scenario with exactly two nested child DAG levels.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateDepthTwoScenario()
        {
            return CreateScenario(
                name: "single-tenant-shared-runtime-mode-child-depth-two",
                controlPlaneIdPrefix: "production-single-tenant-shared-child-depth-two",
                childDepth: 2,
                completionTimeout: TimeSpan.FromMinutes(7),
                childRuntimeFailure: null);
        }

        /// <summary>
        /// Creates the focused depth-one scenario that kills the first-level child while it owns physical runtime
        /// capacity and requires recovery with the same child execution identity on replacement capacity.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateDepthOneRuntimeCrashRecoveryScenario()
        {
            return CreateScenario(
                name: "single-tenant-shared-child-depth-one-real-runtime-crash-recovery",
                controlPlaneIdPrefix: "production-single-tenant-shared-child-depth-one-crash",
                childDepth: 1,
                completionTimeout: TimeSpan.FromMinutes(7),
                childRuntimeFailure: new ProductionChildDagFailureInjectionDefinition
                {
                    TargetDepth = 1,
                    CrashCheckpointStepIndex = 2
                });
        }

        /// <summary>
        /// Creates the focused depth-two scenario that kills the intermediate first-level child before it composes
        /// the second-level child, then requires deterministic recovery and cascading continuation convergence.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateDepthTwoIntermediateRuntimeCrashRecoveryScenario()
        {
            return CreateScenario(
                name: "single-tenant-shared-child-depth-two-intermediate-real-runtime-crash-recovery",
                controlPlaneIdPrefix: "production-single-tenant-shared-child-depth-two-intermediate-crash",
                childDepth: 2,
                completionTimeout: TimeSpan.FromMinutes(7),
                childRuntimeFailure: new ProductionChildDagFailureInjectionDefinition
                {
                    TargetDepth = 1,
                    CrashCheckpointStepIndex = 2
                });
        }

        /// <summary>
        /// Creates the canonical parent-failure proof: the parent starts on the first capacity slot, dispatches C1
        /// while that slot is still occupied, parks durably, then its original runtime boundary can be destroyed
        /// while C1 remains active on the second capacity slot.
        /// </summary>
        /// <returns>The production runtime scenario definition.</returns>
        public static ProductionRuntimeScenarioDefinition CreateParentRuntimeCrashWhileChildRunsScenario()
        {
            var scenario = CreateScenario(
                name: "single-tenant-shared-parent-runtime-crash-while-child-runs",
                controlPlaneIdPrefix: "production-single-tenant-shared-parent-crash-child-running",
                childDepth: 1,
                completionTimeout: TimeSpan.FromMinutes(7),
                childRuntimeFailure: new ProductionChildDagFailureInjectionDefinition
                {
                    Target = ProductionChildDagFailureTarget.ParentRuntimeAfterPark,
                    TargetDepth = 1,
                    CrashCheckpointStepIndex = 2
                },
                maxRuntimeInstances: 2,
                localQueueCapacity: 0);

            return scenario with
            {
                AssertReplayLedgerTrace = true,
                Assertions = new ProductionRuntimeScenarioAssertionOptions()
            };
        }

        /// <summary>
        /// Creates one focused child DAG scenario by extending the historical zero-depth Shared runtime scenario.
        /// </summary>
        /// <param name="name">The scenario name.</param>
        /// <param name="controlPlaneIdPrefix">The control-plane identifier prefix.</param>
        /// <param name="childDepth">The requested nested child depth.</param>
        /// <param name="completionTimeout">The completion timeout.</param>
        /// <param name="childRuntimeFailure">The optional typed Child DAG runtime failure injection.</param>
        /// <param name="maxRuntimeInstances">Optional tenant runtime-instance limit override.</param>
        /// <param name="localQueueCapacity">Optional tenant local-queue capacity override.</param>
        /// <returns>The production runtime scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateScenario(
            string name,
            string controlPlaneIdPrefix,
            int childDepth,
            TimeSpan completionTimeout,
            ProductionChildDagFailureInjectionDefinition? childRuntimeFailure,
            int? maxRuntimeInstances = null,
            int? localQueueCapacity = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneIdPrefix);
            ArgumentOutOfRangeException.ThrowIfLessThan(childDepth, 1);

            var baseScenario = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();
            var tenant = baseScenario.Tenants.Single();

            return baseScenario with
            {
                Name = name,
                ControlPlaneIdPrefix = controlPlaneIdPrefix,
                CompletionTimeout = completionTimeout,
                Tenants = new[]
                {
                    tenant with
                    {
                        MaxRuntimeInstances = maxRuntimeInstances ?? tenant.MaxRuntimeInstances,
                        LocalQueueCapacity = localQueueCapacity ?? tenant.LocalQueueCapacity,
                        Run = tenant.Run with
                        {
                            ChildDepth = childDepth,
                            ChildRuntimeFailure = childRuntimeFailure
                        }
                    }
                }
            };
        }
    }
}
