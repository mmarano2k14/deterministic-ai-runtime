using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Provides the single authoritative mapping from canonical engine events to observability projections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This catalog is intentionally data-driven. Production components emit a canonical semantic event;
    /// the Event Manager consults this catalog to determine which projection surfaces apply and how their
    /// failures affect the emission contract.
    /// </para>
    ///
    /// <para>
    /// The initial mapping preserves the existing ownership boundaries discovered before migration:
    /// historical Decision Ledger events remain Ledger-owned, existing Recovery Forensics events remain
    /// Recovery-Forensics-owned, and existing Runtime Lifecycle Journal events remain Journal-owned.
    /// Additional cross-surface projections are introduced only by the corresponding domain migration
    /// after their existing semantics have been verified.
    /// </para>
    /// </remarks>
    public static class AiEngineEventProjectionCatalog
    {
        private static readonly IReadOnlyDictionary<string, AiEngineEventProjectionDescriptor> Descriptors =
            BuildDescriptors();

        /// <summary>
        /// Gets every canonical engine event projection descriptor.
        /// </summary>
        public static IReadOnlyDictionary<string, AiEngineEventProjectionDescriptor> All => Descriptors;

        /// <summary>
        /// Tries to resolve the projection descriptor for a canonical semantic event type.
        /// </summary>
        /// <param name="eventType">The canonical semantic event type.</param>
        /// <param name="descriptor">The resolved projection descriptor when found.</param>
        /// <returns><c>true</c> when the event type is registered; otherwise, <c>false</c>.</returns>
        public static bool TryGet(
            string eventType,
            out AiEngineEventProjectionDescriptor descriptor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

            return Descriptors.TryGetValue(eventType, out descriptor!);
        }

        /// <summary>
        /// Resolves the projection descriptor for a canonical semantic event type.
        /// </summary>
        /// <param name="eventType">The canonical semantic event type.</param>
        /// <returns>The projection descriptor.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the semantic event type is not registered in the central catalog.
        /// </exception>
        public static AiEngineEventProjectionDescriptor GetRequired(
            string eventType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

            if (Descriptors.TryGetValue(eventType, out var descriptor))
            {
                return descriptor;
            }

            throw new InvalidOperationException(
                $"Canonical engine event '{eventType}' is not registered in the central projection catalog.");
        }

        /// <summary>
        /// Builds the authoritative descriptor set from canonical event declarations only.
        /// </summary>
        /// <returns>The immutable canonical event projection catalog.</returns>
        private static IReadOnlyDictionary<string, AiEngineEventProjectionDescriptor> BuildDescriptors()
        {
            var descriptors = new Dictionary<string, AiEngineEventProjectionDescriptor>(StringComparer.Ordinal);

            AddLedgerEvents(
                descriptors,
                AiEngineEventDurability.DurableLifecycleFact,
                new[]
                {
                AiEngineEvents.Execution.Created,
                AiEngineEvents.Execution.Started,
                AiEngineEvents.Execution.Completed,
                AiEngineEvents.Execution.Failed,
                AiEngineEvents.Execution.Cancelled,
                AiEngineEvents.Execution.Finalized,
                AiEngineEvents.Run.Queued,
                AiEngineEvents.Run.Dequeued,
                AiEngineEvents.Run.Started,
                AiEngineEvents.Run.Completed,
                AiEngineEvents.Run.Suspended,
                AiEngineEvents.Run.Failed,
                AiEngineEvents.Run.Cancelled,
                AiEngineEvents.Queue.Paused,
                AiEngineEvents.Queue.Resumed,
                AiEngineEvents.Dag.StepBecameReady,
                AiEngineEvents.Dag.StepBlocked,
                AiEngineEvents.Dag.StepUnblocked,
                AiEngineEvents.Dag.StepSkipped,
                AiEngineEvents.Step.Started,
                AiEngineEvents.Step.Completed,
                AiEngineEvents.Step.Failed,
                AiEngineEvents.Step.Parked,
                AiEngineEvents.Step.TimedOut,
                AiEngineEvents.Control.PauseRequested,
                AiEngineEvents.Control.Paused,
                AiEngineEvents.Control.ResumeRequested,
                AiEngineEvents.Control.Resumed,
                AiEngineEvents.Control.CancelRequested,
                AiEngineEvents.Control.CancelObserved,
                AiEngineEvents.Control.StateChanged,
                AiEngineEvents.HumanInput.Requested,
                AiEngineEvents.HumanInput.Submitted,
                AiEngineEvents.HumanInput.Rejected,
                AiEngineEvents.HumanInput.Expired,
                AiEngineEvents.HumanInput.Waiting,
                AiEngineEvents.Finalization.Started,
                AiEngineEvents.Finalization.Completed,
                AiEngineEvents.Finalization.Failed,
                AiEngineEvents.Finalization.CancellationOverrideApplied,
                AiEngineEvents.Finalization.RaceLost
                });

            AddLedgerEvents(
                descriptors,
                AiEngineEventDurability.DurableDecisionFact,
                new[]
                {
                AiEngineEvents.Claim.Attempted,
                AiEngineEvents.Claim.Acquired,
                AiEngineEvents.Claim.Denied,
                AiEngineEvents.Claim.Expired,
                AiEngineEvents.Claim.Released,
                AiEngineEvents.Claim.LeaseRenewed,
                AiEngineEvents.Claim.LeaseExpired,
                AiEngineEvents.Retry.Evaluated,
                AiEngineEvents.Retry.Scheduled,
                AiEngineEvents.Retry.Denied,
                AiEngineEvents.Retry.AttemptStarted,
                AiEngineEvents.Retry.AttemptCompleted,
                AiEngineEvents.Retry.BudgetExhausted,
                AiEngineEvents.Concurrency.Evaluated,
                AiEngineEvents.Concurrency.Allowed,
                AiEngineEvents.Concurrency.Denied,
                AiEngineEvents.Concurrency.ThrottleApplied,
                AiEngineEvents.Concurrency.LeaseAcquired,
                AiEngineEvents.Concurrency.LeaseReleased,
                AiEngineEvents.Concurrency.LeaseExpired,
                AiEngineEvents.Retention.Evaluated,
                AiEngineEvents.Retention.Triggered,
                AiEngineEvents.Retention.Skipped,
                AiEngineEvents.Retention.Compacted,
                AiEngineEvents.Retention.Evicted,
                AiEngineEvents.Payload.Externalized,
                AiEngineEvents.Payload.Rehydrated,
                AiEngineEvents.Payload.ResolutionFailed,
                AiEngineEvents.Snapshot.Created,
                AiEngineEvents.Snapshot.Loaded,
                AiEngineEvents.Snapshot.RestoreRequested,
                AiEngineEvents.Snapshot.RestoreCompleted,
                AiEngineEvents.Storage.StatePersisted,
                AiEngineEvents.Storage.StatePersistenceFailed,
                AiEngineEvents.Replay.Requested,
                AiEngineEvents.Replay.Started,
                AiEngineEvents.Replay.Completed,
                AiEngineEvents.Replay.Failed,
                AiEngineEvents.Replay.ComparisonCompleted,
                AiEngineEvents.Replay.ConvergenceProofStarted,
                AiEngineEvents.Replay.ConvergenceProofCompleted,
                AiEngineEvents.Replay.ConvergenceProofFailed
                });

            AddPolicyEvents(descriptors);

            AddLedgerEvents(
                descriptors,
                AiEngineEventDurability.DurableRecoveryFact,
                new[]
                {
                AiEngineEvents.Recovery.Detected,
                AiEngineEvents.Recovery.Applied,
                AiEngineEvents.Recovery.StepRecovered,
                AiEngineEvents.Recovery.ExecutionRecovered
                });

            AddRecoveryForensicsEvents(
                descriptors,
                new[]
                {
                AiEngineEvents.Recovery.RuntimeFailureDetected,
                AiEngineEvents.Recovery.RuntimeHealthSuppressed,
                AiEngineEvents.Recovery.RuntimeCapacityRemoved,
                AiEngineEvents.Recovery.ExecutionRecoveryCandidateDetected,
                AiEngineEvents.Recovery.SharedRunRequeuedForResume,
                AiEngineEvents.Recovery.SharedRunRequeuedForLocalQueuedRecovery,
                AiEngineEvents.Recovery.FailedLocalRunMarkedRequeuedForRecovery,
                AiEngineEvents.Recovery.ReplacementRuntimeSelected,
                AiEngineEvents.Recovery.ReplacementLocalRunRegistered,
                AiEngineEvents.Recovery.ResumeContextSeeded,
                AiEngineEvents.Recovery.DagResumeStarted,
                AiEngineEvents.Recovery.DagResumeCompleted,
                AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                AiEngineEvents.Recovery.ExecutionRecoveryFailed
                });

            AddRuntimeLifecycleJournalEvents(
                descriptors,
                new[]
                {
                AiRuntimeLifecycleEvents.HostCreationRequested,
                AiRuntimeLifecycleEvents.HostCreationStarted,
                AiRuntimeLifecycleEvents.HostCreationSucceeded,
                AiRuntimeLifecycleEvents.HostCreationFailed,
                AiRuntimeLifecycleEvents.RuntimeRegistered,
                AiRuntimeLifecycleEvents.RuntimeReady,
                AiRuntimeLifecycleEvents.RuntimeDraining,
                AiRuntimeLifecycleEvents.RuntimeSuppressed,
                AiRuntimeLifecycleEvents.RuntimeUnhealthy,
                AiRuntimeLifecycleEvents.RuntimeStopped,
                AiRuntimeLifecycleEvents.HostDeletionRequested,
                AiRuntimeLifecycleEvents.HostDeleted,
                AiRuntimeLifecycleEvents.HostDisappeared,
                AiRuntimeLifecycleEvents.RuntimeReplacementRequested,
                AiRuntimeLifecycleEvents.RuntimeReplacementRegistered,
                AiRuntimeLifecycleEvents.WorkAssigned,
                AiRuntimeLifecycleEvents.WorkReassigned,
                AiRuntimeLifecycleEvents.WorkReleased
                });

            AddChildDagEvents(
                descriptors,
                new[]
                {
                AiEngineEvents.ChildDag.ExecutionCreated,
                AiEngineEvents.ChildDag.ExecutionStarted,
                AiEngineEvents.ChildDag.ExecutionCompleted,
                AiEngineEvents.ChildDag.ExecutionFailed,
                AiEngineEvents.ChildDag.ContinuationScheduled,
                AiEngineEvents.ChildDag.ContinuationDelivered,
                AiEngineEvents.ChildDag.ContinuationConsumed,
                AiEngineEvents.ChildDag.ParentContinuationResumed
                });

            return new ReadOnlyDictionary<string, AiEngineEventProjectionDescriptor>(descriptors);
        }

        /// <summary>
        /// Adds events whose currently proven durable owner is the existing Decision Ledger.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        /// <param name="durability">The semantic durability class for the event family.</param>
        /// <param name="eventTypes">The canonical event types.</param>
        private static void AddLedgerEvents(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors,
            AiEngineEventDurability durability,
            IEnumerable<string> eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                Add(
                    descriptors,
                    new AiEngineEventProjectionDescriptor
                    {
                        EventType = eventType,
                        Durability = durability,
                        Ledger = AiEngineEventProjectionRequirement.RequiredDurable,
                        Logging = AiEngineEventProjectionRequirement.BestEffort
                    });
            }
        }

        /// <summary>
        /// Adds events whose currently proven durable owner is the existing Recovery Forensics recorder.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        /// <param name="eventTypes">The canonical recovery event types.</param>
        private static void AddRecoveryForensicsEvents(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors,
            IEnumerable<string> eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                Add(
                    descriptors,
                    new AiEngineEventProjectionDescriptor
                    {
                        EventType = eventType,
                        Durability = AiEngineEventDurability.DurableRecoveryFact,
                        RecoveryForensics = AiEngineEventProjectionRequirement.RequiredDurable,
                        Logging = AiEngineEventProjectionRequirement.BestEffort,
                        Realtime = AiEngineEventProjectionRequirement.BestEffort
                    });
            }
        }

        /// <summary>
        /// Adds events whose currently proven durable owner is the existing Runtime Lifecycle Journal.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        /// <param name="eventTypes">The canonical runtime lifecycle event types.</param>
        private static void AddRuntimeLifecycleJournalEvents(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors,
            IEnumerable<string> eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                Add(
                    descriptors,
                    new AiEngineEventProjectionDescriptor
                    {
                        EventType = eventType,
                        Durability = AiEngineEventDurability.RuntimeJournalFact,
                        LifecycleJournal = AiEngineEventProjectionRequirement.RequiredDurable,
                        Metrics = AiEngineEventProjectionRequirement.None,
                        Logging = AiEngineEventProjectionRequirement.BestEffort,
                        Realtime = AiEngineEventProjectionRequirement.BestEffort
                    });
            }
        }

        /// <summary>
        /// Adds recursive Child DAG facts. The existing Decision Ledger remains the proven durable
        /// projection where applicable. Realtime remains a centrally declared best-effort surface;
        /// Metrics stays disabled until an exact adapter over existing Child DAG metric contracts is proven.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        /// <param name="eventTypes">The canonical Child DAG event types.</param>
        private static void AddChildDagEvents(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors,
            IEnumerable<string> eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                var isPhysicalDeliveryObservation = string.Equals(
                    eventType,
                    AiEngineEvents.ChildDag.ContinuationDelivered,
                    StringComparison.Ordinal);

                Add(
                    descriptors,
                    new AiEngineEventProjectionDescriptor
                    {
                        EventType = eventType,
                        Durability = isPhysicalDeliveryObservation
                            ? AiEngineEventDurability.TransientObservation
                            : AiEngineEventDurability.DurableLifecycleFact,
                        Ledger = isPhysicalDeliveryObservation
                            ? AiEngineEventProjectionRequirement.None
                            : AiEngineEventProjectionRequirement.BestEffort,
                        Metrics = AiEngineEventProjectionRequirement.None,
                        Logging = AiEngineEventProjectionRequirement.BestEffort,
                        Realtime = AiEngineEventProjectionRequirement.BestEffort
                    });
            }
        }

        /// <summary>
        /// Adds canonical policy events using the existing Ledger and Policy Metrics implementations.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        private static void AddPolicyEvents(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors)
        {
            Add(
                descriptors,
                new AiEngineEventProjectionDescriptor
                {
                    EventType = AiEngineEvents.Policy.Evaluated,
                    Durability = AiEngineEventDurability.DurableDecisionFact,
                    Ledger = AiEngineEventProjectionRequirement.RequiredDurable,
                    Logging = AiEngineEventProjectionRequirement.BestEffort
                });

            foreach (var eventType in new[]
            {
                AiEngineEvents.Policy.Allowed,
                AiEngineEvents.Policy.Denied,
                AiEngineEvents.Policy.Failed
            })
            {
                Add(
                    descriptors,
                    new AiEngineEventProjectionDescriptor
                    {
                        EventType = eventType,
                        Durability = AiEngineEventDurability.DurableDecisionFact,
                        Ledger = AiEngineEventProjectionRequirement.RequiredDurable,
                        Metrics = AiEngineEventProjectionRequirement.BestEffort,
                        Logging = AiEngineEventProjectionRequirement.BestEffort
                    });
            }

            Add(
                descriptors,
                new AiEngineEventProjectionDescriptor
                {
                    EventType = AiEngineEvents.Policy.Skipped,
                    Durability = AiEngineEventDurability.DurableDecisionFact,
                    Ledger = AiEngineEventProjectionRequirement.RequiredDurable,
                    Logging = AiEngineEventProjectionRequirement.BestEffort
                });
        }

        /// <summary>
        /// Adds one canonical event descriptor while enforcing one descriptor per semantic value.
        /// </summary>
        /// <param name="descriptors">The descriptor map being built.</param>
        /// <param name="descriptor">The descriptor to add.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the canonical event type already has a descriptor.
        /// </exception>
        private static void Add(
            IDictionary<string, AiEngineEventProjectionDescriptor> descriptors,
            AiEngineEventProjectionDescriptor descriptor)
        {
            if (descriptors.ContainsKey(descriptor.EventType))
            {
                throw new InvalidOperationException(
                    $"Canonical engine event '{descriptor.EventType}' has more than one projection descriptor.");
            }

            descriptors.Add(descriptor.EventType, descriptor);
        }
    }
}
