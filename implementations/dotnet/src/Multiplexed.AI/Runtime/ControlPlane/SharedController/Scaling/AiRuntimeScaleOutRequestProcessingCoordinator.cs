namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Coordinates complete scale-out request-processing workflows across every
    /// control plane hosted in the current process.
    /// </summary>
    /// <remarks>
    /// Admission occurs before watcher observability, request-store transitions,
    /// provider-request materialization, provider invocation, and terminal
    /// fulfillment or rejection handling. This prevents a large number of watcher
    /// tasks from performing Redis and control-plane work while merely waiting for
    /// process-host provisioning capacity.
    ///
    /// The coordinator provides process-wide and per-control-plane concurrency
    /// limits, round-robin control-plane fairness, bounded recovery priority, and
    /// logical request-id single-flight. Selected asynchronous workflows are
    /// started directly; the coordinator does not use <c>Task.Run</c>.
    /// </remarks>
    public static class AiRuntimeScaleOutRequestProcessingCoordinator
    {
        private static readonly object CoordinatorsSync = new();
        private static readonly Dictionary<string, CoordinatorState> Coordinators =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Schedules one complete watcher request-processing workflow.
        /// </summary>
        /// <param name="coordinationKey">The process-wide coordination key.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="requestId">The persisted scale-out request identifier.</param>
        /// <param name="sharedRunId">The linked shared-run identifier.</param>
        /// <param name="isRecovery">Whether this request belongs to crash recovery.</param>
        /// <param name="maxConcurrentWorkflows">The global active workflow limit.</param>
        /// <param name="maxConcurrentWorkflowsPerControlPlane">The per-control-plane active workflow limit.</param>
        /// <param name="recoveryDispatchBurstLimit">The maximum consecutive recovery dispatches while normal work is waiting.</param>
        /// <param name="workflow">The complete watcher workflow to execute after admission.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task completing with the admitted workflow.</returns>
        public static Task ScheduleAsync(
            string coordinationKey,
            string controlPlaneId,
            string requestId,
            string? sharedRunId,
            bool isRecovery,
            int maxConcurrentWorkflows,
            int maxConcurrentWorkflowsPerControlPlane,
            int recoveryDispatchBurstLimit,
            Func<CancellationToken, Task> workflow,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(coordinationKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentNullException.ThrowIfNull(workflow);

            if (maxConcurrentWorkflows <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxConcurrentWorkflows),
                    maxConcurrentWorkflows,
                    "Process-wide scale-out workflow concurrency must be greater than zero.");
            }

            if (maxConcurrentWorkflowsPerControlPlane <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxConcurrentWorkflowsPerControlPlane),
                    maxConcurrentWorkflowsPerControlPlane,
                    "Per-control-plane scale-out workflow concurrency must be greater than zero.");
            }

            if (recoveryDispatchBurstLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryDispatchBurstLimit),
                    recoveryDispatchBurstLimit,
                    "Recovery dispatch burst limit must be greater than zero.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            CoordinatorState coordinator;

            lock (CoordinatorsSync)
            {
                if (!Coordinators.TryGetValue(coordinationKey, out coordinator!))
                {
                    coordinator =
                        new CoordinatorState(
                            coordinationKey,
                            maxConcurrentWorkflows,
                            maxConcurrentWorkflowsPerControlPlane,
                            recoveryDispatchBurstLimit);

                    Coordinators.Add(
                        coordinationKey,
                        coordinator);
                }
                else
                {
                    coordinator.ValidateConfiguration(
                        maxConcurrentWorkflows,
                        maxConcurrentWorkflowsPerControlPlane,
                        recoveryDispatchBurstLimit);
                }
            }

            return coordinator.ScheduleAsync(
                controlPlaneId,
                requestId,
                sharedRunId,
                isRecovery,
                workflow,
                cancellationToken);
        }

        /// <summary>
        /// Owns one named process-wide coordinator state.
        /// </summary>
        private sealed class CoordinatorState
        {
            private readonly object sync = new();
            private readonly string coordinationKey;
            private readonly int maxConcurrentWorkflows;
            private readonly int maxConcurrentWorkflowsPerControlPlane;
            private readonly int recoveryDispatchBurstLimit;
            private readonly Dictionary<string, ControlPlaneLane> lanes =
                new(StringComparer.Ordinal);
            private readonly LinkedList<string> controlPlaneRotation = new();
            private readonly Dictionary<string, WorkItem> requests =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> activeByControlPlane =
                new(StringComparer.Ordinal);
            private int activeWorkflowCount;
            private int consecutiveRecoveryDispatchCount;

            /// <summary>
            /// Initializes a new coordinator state.
            /// </summary>
            public CoordinatorState(
                string coordinationKey,
                int maxConcurrentWorkflows,
                int maxConcurrentWorkflowsPerControlPlane,
                int recoveryDispatchBurstLimit)
            {
                this.coordinationKey = coordinationKey;
                this.maxConcurrentWorkflows = maxConcurrentWorkflows;
                this.maxConcurrentWorkflowsPerControlPlane = maxConcurrentWorkflowsPerControlPlane;
                this.recoveryDispatchBurstLimit = recoveryDispatchBurstLimit;
            }

            /// <summary>
            /// Validates that every participant uses the same process-wide limits.
            /// </summary>
            public void ValidateConfiguration(
                int requestedMaxConcurrentWorkflows,
                int requestedMaxConcurrentWorkflowsPerControlPlane,
                int requestedRecoveryDispatchBurstLimit)
            {
                if (requestedMaxConcurrentWorkflows != this.maxConcurrentWorkflows ||
                    requestedMaxConcurrentWorkflowsPerControlPlane != this.maxConcurrentWorkflowsPerControlPlane ||
                    requestedRecoveryDispatchBurstLimit != this.recoveryDispatchBurstLimit)
                {
                    throw new InvalidOperationException(
                        $"Scale-out request-processing coordinator '{this.coordinationKey}' configuration mismatch. " +
                        $"EffectiveGlobal='{this.maxConcurrentWorkflows}', RequestedGlobal='{requestedMaxConcurrentWorkflows}', " +
                        $"EffectivePerControlPlane='{this.maxConcurrentWorkflowsPerControlPlane}', RequestedPerControlPlane='{requestedMaxConcurrentWorkflowsPerControlPlane}', " +
                        $"EffectiveRecoveryBurst='{this.recoveryDispatchBurstLimit}', RequestedRecoveryBurst='{requestedRecoveryDispatchBurstLimit}'.");
                }
            }

            /// <summary>
            /// Schedules one complete watcher workflow.
            /// </summary>
            public Task ScheduleAsync(
                string controlPlaneId,
                string requestId,
                string? sharedRunId,
                bool isRecovery,
                Func<CancellationToken, Task> workflow,
                CancellationToken cancellationToken)
            {
                var logicalRequestKey =
                    BuildLogicalRequestKey(
                        controlPlaneId,
                        requestId);

                List<WorkItem> dispatchableItems;
                WorkItem workItem;

                lock (this.sync)
                {
                    if (this.requests.TryGetValue(logicalRequestKey, out var existing))
                    {
                        return existing.Completion.Task;
                    }

                    workItem =
                        new WorkItem(
                            this,
                            logicalRequestKey,
                            controlPlaneId,
                            requestId,
                            sharedRunId,
                            isRecovery,
                            workflow,
                            cancellationToken);

                    this.requests.Add(
                        logicalRequestKey,
                        workItem);

                    var lane =
                        this.GetOrCreateLaneLocked(controlPlaneId);

                    var queue =
                        isRecovery
                            ? lane.RecoveryQueue
                            : lane.NormalQueue;

                    workItem.QueueNode = queue.AddLast(workItem);

                    Console.WriteLine(
                        $"[RUNTIME SCALE-OUT WORKFLOW COORDINATOR QUEUED] CoordinationKey='{this.coordinationKey}', " +
                        $"ControlPlaneId='{controlPlaneId}', RequestId='{requestId}', SharedRunId='{sharedRunId}', " +
                        $"Priority='{(isRecovery ? "Recovery" : "Normal")}', Active='{this.activeWorkflowCount}', " +
                        $"Queued='{this.GetQueuedCountLocked()}'.");

                    dispatchableItems =
                        this.TakeDispatchableItemsLocked();
                }

                workItem.RegisterCancellation();
                StartItems(dispatchableItems);

                return workItem.Completion.Task;
            }

            /// <summary>
            /// Cancels one queued item without executing its workflow.
            /// </summary>
            public void CancelQueued(
                WorkItem workItem)
            {
                List<WorkItem> dispatchableItems;
                var cancelled = false;

                lock (this.sync)
                {
                    if (workItem.State != WorkItemState.Queued)
                    {
                        return;
                    }

                    RemoveQueuedNode(workItem);
                    workItem.State = WorkItemState.Completed;
                    this.requests.Remove(workItem.LogicalRequestKey);
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                    this.RemoveEmptyLaneLocked(workItem.ControlPlaneId);
                    dispatchableItems = this.TakeDispatchableItemsLocked();
                    cancelled = true;
                }

                if (cancelled)
                {
                    Console.WriteLine(
                        $"[RUNTIME SCALE-OUT WORKFLOW COORDINATOR CANCELLED] CoordinationKey='{this.coordinationKey}', " +
                        $"ControlPlaneId='{workItem.ControlPlaneId}', RequestId='{workItem.RequestId}', " +
                        $"SharedRunId='{workItem.SharedRunId}', State='Queued'.");
                }

                StartItems(dispatchableItems);
            }

            /// <summary>
            /// Starts selected asynchronous workflows directly without queueing a thread-pool work item.
            /// </summary>
            private static void StartItems(
                IReadOnlyList<WorkItem> workItems)
            {
                foreach (var workItem in workItems)
                {
                    workItem.Owner.Start(workItem);
                }
            }

            /// <summary>
            /// Starts one selected workflow.
            /// </summary>
            private void Start(
                WorkItem workItem)
            {
                _ = this.ExecuteAsync(workItem);
            }

            /// <summary>
            /// Executes one admitted workflow and schedules its successor.
            /// </summary>
            private async Task ExecuteAsync(
                WorkItem workItem)
            {
                var executionStartedAtUtc = DateTimeOffset.UtcNow;

                Console.WriteLine(
                    $"[RUNTIME SCALE-OUT WORKFLOW COORDINATOR DISPATCHED] CoordinationKey='{this.coordinationKey}', " +
                    $"ControlPlaneId='{workItem.ControlPlaneId}', RequestId='{workItem.RequestId}', SharedRunId='{workItem.SharedRunId}', " +
                    $"Priority='{(workItem.IsRecovery ? "Recovery" : "Normal")}', " +
                    $"QueueWaitMs='{(executionStartedAtUtc - workItem.QueuedAtUtc).TotalMilliseconds:F3}', " +
                    $"Active='{this.GetActiveCount()}', ActiveForControlPlane='{this.GetActiveCount(workItem.ControlPlaneId)}'.");

                try
                {
                    await workItem
                        .Workflow(workItem.CancellationToken)
                        .ConfigureAwait(false);

                    workItem.Completion.TrySetResult(null);
                }
                catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                }
                catch (Exception exception)
                {
                    workItem.Completion.TrySetException(exception);
                }
                finally
                {
                    List<WorkItem> dispatchableItems;
                    int activeAfterCompletion;
                    int activeForControlPlaneAfterCompletion;

                    lock (this.sync)
                    {
                        if (workItem.State == WorkItemState.Active)
                        {
                            workItem.State = WorkItemState.Completed;
                            this.activeWorkflowCount--;

                            var activeForControlPlane =
                                this.activeByControlPlane[workItem.ControlPlaneId] - 1;

                            if (activeForControlPlane == 0)
                            {
                                this.activeByControlPlane.Remove(workItem.ControlPlaneId);
                            }
                            else
                            {
                                this.activeByControlPlane[workItem.ControlPlaneId] = activeForControlPlane;
                            }

                            this.requests.Remove(workItem.LogicalRequestKey);
                            this.RemoveEmptyLaneLocked(workItem.ControlPlaneId);

                            activeAfterCompletion = this.activeWorkflowCount;
                            activeForControlPlaneAfterCompletion = activeForControlPlane;
                            dispatchableItems = this.TakeDispatchableItemsLocked();
                        }
                        else
                        {
                            activeAfterCompletion = this.activeWorkflowCount;
                            activeForControlPlaneAfterCompletion =
                                this.activeByControlPlane.TryGetValue(workItem.ControlPlaneId, out var active)
                                    ? active
                                    : 0;
                            dispatchableItems = new List<WorkItem>();
                        }
                    }

                    workItem.DisposeCancellationRegistration();

                    Console.WriteLine(
                        $"[RUNTIME SCALE-OUT WORKFLOW COORDINATOR COMPLETED] CoordinationKey='{this.coordinationKey}', " +
                        $"ControlPlaneId='{workItem.ControlPlaneId}', RequestId='{workItem.RequestId}', SharedRunId='{workItem.SharedRunId}', " +
                        $"Priority='{(workItem.IsRecovery ? "Recovery" : "Normal")}', " +
                        $"DurationMs='{(DateTimeOffset.UtcNow - executionStartedAtUtc).TotalMilliseconds:F3}', " +
                        $"Active='{activeAfterCompletion}', ActiveForControlPlane='{activeForControlPlaneAfterCompletion}'.");

                    StartItems(dispatchableItems);
                }
            }

            /// <summary>
            /// Selects every item that can start immediately.
            /// </summary>
            private List<WorkItem> TakeDispatchableItemsLocked()
            {
                var selected = new List<WorkItem>();

                while (this.activeWorkflowCount < this.maxConcurrentWorkflows)
                {
                    var workItem = this.TakeNextItemLocked();

                    if (workItem is null)
                    {
                        break;
                    }

                    workItem.State = WorkItemState.Active;
                    this.activeWorkflowCount++;
                    this.activeByControlPlane.TryGetValue(
                        workItem.ControlPlaneId,
                        out var activeForControlPlane);
                    this.activeByControlPlane[workItem.ControlPlaneId] = activeForControlPlane + 1;
                    selected.Add(workItem);
                }

                return selected;
            }

            /// <summary>
            /// Selects one fair work item.
            /// </summary>
            private WorkItem? TakeNextItemLocked()
            {
                var recoveryAvailable = this.HasEligibleItemLocked(recovery: true);
                var normalAvailable = this.HasEligibleItemLocked(recovery: false);

                if (!recoveryAvailable && !normalAvailable)
                {
                    return null;
                }

                var dispatchRecovery =
                    recoveryAvailable &&
                    (!normalAvailable ||
                     this.consecutiveRecoveryDispatchCount < this.recoveryDispatchBurstLimit);

                var selected = this.TakeNextItemLocked(dispatchRecovery);

                if (selected is null && dispatchRecovery && normalAvailable)
                {
                    selected = this.TakeNextItemLocked(recovery: false);
                    dispatchRecovery = false;
                }
                else if (selected is null && !dispatchRecovery && recoveryAvailable)
                {
                    selected = this.TakeNextItemLocked(recovery: true);
                    dispatchRecovery = true;
                }

                if (selected is null)
                {
                    return null;
                }

                if (dispatchRecovery)
                {
                    this.consecutiveRecoveryDispatchCount++;
                }
                else
                {
                    this.consecutiveRecoveryDispatchCount = 0;
                }

                return selected;
            }

            /// <summary>
            /// Selects one item of the requested priority using control-plane round robin.
            /// </summary>
            private WorkItem? TakeNextItemLocked(
                bool recovery)
            {
                var controlPlaneCount = this.controlPlaneRotation.Count;

                for (var index = 0; index < controlPlaneCount; index++)
                {
                    var node = this.controlPlaneRotation.First!;
                    var controlPlaneId = node.Value;

                    this.controlPlaneRotation.RemoveFirst();
                    this.controlPlaneRotation.AddLast(node);

                    this.activeByControlPlane.TryGetValue(
                        controlPlaneId,
                        out var activeForControlPlane);

                    if (activeForControlPlane >= this.maxConcurrentWorkflowsPerControlPlane)
                    {
                        continue;
                    }

                    if (!this.lanes.TryGetValue(controlPlaneId, out var lane))
                    {
                        continue;
                    }

                    var queue =
                        recovery
                            ? lane.RecoveryQueue
                            : lane.NormalQueue;

                    while (queue.First is not null)
                    {
                        var candidate = queue.First.Value;
                        queue.RemoveFirst();
                        candidate.QueueNode = null;

                        if (candidate.State != WorkItemState.Queued ||
                            candidate.Completion.Task.IsCompleted ||
                            candidate.CancellationToken.IsCancellationRequested)
                        {
                            if (candidate.CancellationToken.IsCancellationRequested)
                            {
                                candidate.State = WorkItemState.Completed;
                                candidate.Completion.TrySetCanceled(candidate.CancellationToken);
                            }

                            this.requests.Remove(candidate.LogicalRequestKey);
                            continue;
                        }

                        return candidate;
                    }
                }

                return null;
            }

            /// <summary>
            /// Returns whether an eligible item exists for the requested priority.
            /// </summary>
            private bool HasEligibleItemLocked(
                bool recovery)
            {
                foreach (var controlPlaneId in this.controlPlaneRotation)
                {
                    this.activeByControlPlane.TryGetValue(
                        controlPlaneId,
                        out var activeForControlPlane);

                    if (activeForControlPlane >= this.maxConcurrentWorkflowsPerControlPlane)
                    {
                        continue;
                    }

                    if (!this.lanes.TryGetValue(controlPlaneId, out var lane))
                    {
                        continue;
                    }

                    var queue =
                        recovery
                            ? lane.RecoveryQueue
                            : lane.NormalQueue;

                    if (queue.Any(
                            candidate =>
                                candidate.State == WorkItemState.Queued &&
                                !candidate.Completion.Task.IsCompleted &&
                                !candidate.CancellationToken.IsCancellationRequested))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Gets or creates one control-plane lane.
            /// </summary>
            private ControlPlaneLane GetOrCreateLaneLocked(
                string controlPlaneId)
            {
                if (this.lanes.TryGetValue(controlPlaneId, out var lane))
                {
                    return lane;
                }

                lane = new ControlPlaneLane();
                this.lanes.Add(controlPlaneId, lane);
                this.controlPlaneRotation.AddLast(controlPlaneId);

                return lane;
            }

            /// <summary>
            /// Removes one lane after its queues and active work are empty.
            /// </summary>
            private void RemoveEmptyLaneLocked(
                string controlPlaneId)
            {
                if (!this.lanes.TryGetValue(controlPlaneId, out var lane) ||
                    lane.RecoveryQueue.Count > 0 ||
                    lane.NormalQueue.Count > 0 ||
                    this.activeByControlPlane.ContainsKey(controlPlaneId))
                {
                    return;
                }

                this.lanes.Remove(controlPlaneId);

                var node = this.controlPlaneRotation.Find(controlPlaneId);
                if (node is not null)
                {
                    this.controlPlaneRotation.Remove(node);
                }
            }

            /// <summary>
            /// Removes one queued node from its owning lane.
            /// </summary>
            private static void RemoveQueuedNode(
                WorkItem workItem)
            {
                var node = workItem.QueueNode;

                if (node?.List is not null)
                {
                    node.List.Remove(node);
                }

                workItem.QueueNode = null;
            }

            /// <summary>
            /// Gets the current active workflow count.
            /// </summary>
            private int GetActiveCount()
            {
                lock (this.sync)
                {
                    return this.activeWorkflowCount;
                }
            }

            /// <summary>
            /// Gets the current active workflow count for one control plane.
            /// </summary>
            private int GetActiveCount(
                string controlPlaneId)
            {
                lock (this.sync)
                {
                    return this.activeByControlPlane.TryGetValue(controlPlaneId, out var count)
                        ? count
                        : 0;
                }
            }

            /// <summary>
            /// Gets the total queued workflow count.
            /// </summary>
            private int GetQueuedCountLocked()
            {
                return this.lanes.Values.Sum(
                    lane => lane.RecoveryQueue.Count + lane.NormalQueue.Count);
            }

            /// <summary>
            /// Builds a process-wide logical request key.
            /// </summary>
            private static string BuildLogicalRequestKey(
                string controlPlaneId,
                string requestId)
            {
                return $"{controlPlaneId}\n{requestId}";
            }
        }

        /// <summary>
        /// Holds queued work for one control plane.
        /// </summary>
        private sealed class ControlPlaneLane
        {
            /// <summary>
            /// Gets recovery work.
            /// </summary>
            public LinkedList<WorkItem> RecoveryQueue { get; } = new();

            /// <summary>
            /// Gets normal scale-out work.
            /// </summary>
            public LinkedList<WorkItem> NormalQueue { get; } = new();
        }

        /// <summary>
        /// Represents one coordinated watcher workflow.
        /// </summary>
        private sealed class WorkItem
        {
            private CancellationTokenRegistration cancellationRegistration;
            private int cancellationRegistrationInitialized;

            /// <summary>
            /// Initializes a new work item.
            /// </summary>
            public WorkItem(
                CoordinatorState owner,
                string logicalRequestKey,
                string controlPlaneId,
                string requestId,
                string? sharedRunId,
                bool isRecovery,
                Func<CancellationToken, Task> workflow,
                CancellationToken cancellationToken)
            {
                this.Owner = owner;
                this.LogicalRequestKey = logicalRequestKey;
                this.ControlPlaneId = controlPlaneId;
                this.RequestId = requestId;
                this.SharedRunId = sharedRunId;
                this.IsRecovery = isRecovery;
                this.Workflow = workflow;
                this.CancellationToken = cancellationToken;
                this.QueuedAtUtc = DateTimeOffset.UtcNow;
            }

            public CoordinatorState Owner { get; }

            public string LogicalRequestKey { get; }

            public string ControlPlaneId { get; }

            public string RequestId { get; }

            public string? SharedRunId { get; }

            public bool IsRecovery { get; }

            public Func<CancellationToken, Task> Workflow { get; }

            public CancellationToken CancellationToken { get; }

            public DateTimeOffset QueuedAtUtc { get; }

            public TaskCompletionSource<object?> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public LinkedListNode<WorkItem>? QueueNode { get; set; }

            public WorkItemState State { get; set; } = WorkItemState.Queued;

            /// <summary>
            /// Registers cancellation after the item has been published to coordinator state.
            /// </summary>
            public void RegisterCancellation()
            {
                if (!this.CancellationToken.CanBeCanceled)
                {
                    return;
                }

                this.cancellationRegistration =
                    this.CancellationToken.Register(
                        static state =>
                        {
                            var workItem = (WorkItem)state!;
                            workItem.Owner.CancelQueued(workItem);
                        },
                        this);

                Volatile.Write(
                    ref this.cancellationRegistrationInitialized,
                    1);

                if (this.Completion.Task.IsCompleted)
                {
                    this.DisposeCancellationRegistration();
                }
            }

            /// <summary>
            /// Disposes the cancellation registration once the item is terminal.
            /// </summary>
            public void DisposeCancellationRegistration()
            {
                if (Interlocked.Exchange(
                        ref this.cancellationRegistrationInitialized,
                        0) == 1)
                {
                    this.cancellationRegistration.Dispose();
                }
            }
        }

        /// <summary>
        /// Represents coordinator lifecycle state for one work item.
        /// </summary>
        private enum WorkItemState
        {
            Queued = 0,
            Active = 1,
            Completed = 2
        }
    }
}
