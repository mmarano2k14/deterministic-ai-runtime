using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Guards the centralized canonical-event projection catalog.
    /// </summary>
    public sealed class AiEngineEventProjectionCatalogTests
    {
        private const string CanonicalNamespace = "Multiplexed.Abstractions.AI.Observability.Events";

        /// <summary>
        /// Verifies that every canonical engine event declaration has exactly one projection descriptor.
        /// </summary>
        [Fact]
        public void Catalog_Should_Cover_Every_Canonical_Engine_Event()
        {
            var canonicalEventTypes = GetCanonicalEventTypes();
            var catalogEventTypes = AiEngineEventProjectionCatalog.All.Keys.ToArray();

            Assert.Equal(canonicalEventTypes.Count, catalogEventTypes.Length);
            Assert.Empty(canonicalEventTypes.Except(catalogEventTypes, StringComparer.Ordinal));
            Assert.Empty(catalogEventTypes.Except(canonicalEventTypes, StringComparer.Ordinal));
        }

        /// <summary>
        /// Verifies centralized Ledger, Metrics, and Logging projection for a policy decision.
        /// </summary>
        [Fact]
        public void PolicyAllowed_Should_Project_To_Ledger_Metrics_And_Logging()
        {
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(AiEngineEvents.Policy.Allowed);

            Assert.Equal(AiEngineEventDurability.DurableDecisionFact, descriptor.Durability);
            Assert.Equal(AiEngineEventProjectionRequirement.RequiredDurable, descriptor.Ledger);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Logging);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.RecoveryForensics);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Metrics);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Realtime);
        }

        /// <summary>
        /// Verifies the preserved Recovery Forensics ownership contract for an existing recovery event.
        /// </summary>
        [Fact]
        public void ExecutionRecoveryCompleted_Should_Project_To_RecoveryForensics_Logging_And_Realtime()
        {
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(
                AiEngineEvents.Recovery.ExecutionRecoveryCompleted);

            Assert.Equal(AiEngineEventDurability.DurableRecoveryFact, descriptor.Durability);
            Assert.Equal(AiEngineEventProjectionRequirement.RequiredDurable, descriptor.RecoveryForensics);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Logging);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Realtime);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Ledger);
        }

        /// <summary>
        /// Verifies the centralized Runtime Lifecycle Journal ownership contract.
        /// </summary>
        [Fact]
        public void RuntimeRegistered_Should_Project_To_LifecycleJournal_Logging_And_Realtime()
        {
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(
                AiRuntimeLifecycleEvents.RuntimeRegistered);

            Assert.Equal(AiEngineEventDurability.RuntimeJournalFact, descriptor.Durability);
            Assert.Equal(AiEngineEventProjectionRequirement.RequiredDurable, descriptor.LifecycleJournal);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Metrics);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Logging);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Realtime);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Ledger);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.RecoveryForensics);
        }

        /// <summary>
        /// Verifies the durable Child DAG projection contract after relation state has committed.
        /// </summary>
        [Fact]
        public void ChildExecutionCompleted_Should_Use_Durable_Child_Projection_Contract()
        {
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(
                AiEngineEvents.ChildDag.ExecutionCompleted);

            Assert.Equal(AiEngineEventDurability.DurableLifecycleFact, descriptor.Durability);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Ledger);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Metrics);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Logging);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Realtime);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.RecoveryForensics);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.LifecycleJournal);
        }

        /// <summary>
        /// Verifies that physical continuation delivery remains a transient observation rather than durable truth.
        /// </summary>
        [Fact]
        public void ContinuationDelivered_Should_Remain_Transient_And_Not_Write_Ledger()
        {
            var descriptor = AiEngineEventProjectionCatalog.GetRequired(
                AiEngineEvents.ChildDag.ContinuationDelivered);

            Assert.Equal(AiEngineEventDurability.TransientObservation, descriptor.Durability);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Ledger);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.Metrics);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Logging);
            Assert.Equal(AiEngineEventProjectionRequirement.BestEffort, descriptor.Realtime);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.RecoveryForensics);
            Assert.Equal(AiEngineEventProjectionRequirement.None, descriptor.LifecycleJournal);
        }

        /// <summary>
        /// Guards against inventing a parallel execution-forensics projection before an existing implementation exists.
        /// </summary>
        [Fact]
        public void Catalog_Should_Not_Invent_ExecutionForensics_Projection_Ownership()
        {
            Assert.All(
                AiEngineEventProjectionCatalog.All.Values,
                descriptor => Assert.Equal(
                    AiEngineEventProjectionRequirement.None,
                    descriptor.ExecutionForensics));
        }

        private static IReadOnlyList<string> GetCanonicalEventTypes()
        {
            var assembly = typeof(AiEngineEvents).Assembly;

            return assembly
                .GetTypes()
                .Where(type => string.Equals(type.Namespace, CanonicalNamespace, StringComparison.Ordinal))
                .SelectMany(
                    type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => field.GetRawConstantValue())
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
