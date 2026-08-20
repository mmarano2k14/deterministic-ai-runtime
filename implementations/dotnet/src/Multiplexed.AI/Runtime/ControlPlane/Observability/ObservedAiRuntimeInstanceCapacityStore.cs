using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;


namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Observability decorator for runtime instance capacity stores.
    /// </summary>
    /// <remarks>
    /// This decorator records control-plane events around any <see cref="IAiRuntimeInstanceCapacityStore" /> implementation,
    /// including Redis-backed and in-memory capacity stores.
    /// </remarks>
    public sealed class ObservedAiRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
    {
        private const string CapacityPublishOperation = "runtime-instance-capacity-publish";
        private const string CapacityGetOperation = "runtime-instance-capacity-get";
        private const string CapacityListOperation = "runtime-instance-capacity-list";
        private const string CapacityRemoveOperation = "runtime-instance-capacity-remove";

        private readonly IAiRuntimeInstanceCapacityStore inner;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservedAiRuntimeInstanceCapacityStore" /> class.
        /// </summary>
        /// <param name="inner">The wrapped runtime instance capacity store.</param>
        /// <param name="observer">The control-plane observer.</param>
        public ObservedAiRuntimeInstanceCapacityStore(
            IAiRuntimeInstanceCapacityStore inner,
            IAiControlPlaneObserver observer)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordAsync(
                    AiControlPlaneEventType.OperationStarted,
                    CapacityPublishOperation,
                    descriptor.RuntimeInstanceId,
                    descriptor.ControlPlaneId,
                    descriptor.TenantId,
                    descriptor.TenantGroupId,
                    null,
                    null,
                    null,
                    CreateDescriptorProperties(descriptor, null),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await this.inner
                    .PublishAsync(descriptor, cancellationToken)
                    .ConfigureAwait(false);

                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        CapacityPublishOperation,
                        descriptor.RuntimeInstanceId,
                        descriptor.ControlPlaneId,
                        descriptor.TenantId,
                        descriptor.TenantGroupId,
                        AiControlPlaneOperationOutcome.Succeeded,
                        null,
                        durationMs,
                        CreateDescriptorProperties(descriptor, durationMs),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationFailed,
                        CapacityPublishOperation,
                        descriptor.RuntimeInstanceId,
                        descriptor.ControlPlaneId,
                        descriptor.TenantId,
                        descriptor.TenantGroupId,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        CreateFailureProperties(CreateDescriptorProperties(descriptor, durationMs), exception),
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordAsync(
                    AiControlPlaneEventType.OperationStarted,
                    CapacityGetOperation,
                    runtimeInstanceId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var descriptor = await this.inner
                    .GetAsync(runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);
                var outcome = descriptor is null
                    ? AiControlPlaneOperationOutcome.CompletedWithIssues
                    : AiControlPlaneOperationOutcome.Succeeded;
                var failureReason = descriptor is null
                    ? "runtime-instance-capacity-not-found"
                    : null;

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        CapacityGetOperation,
                        runtimeInstanceId,
                        descriptor?.ControlPlaneId,
                        descriptor?.TenantId,
                        descriptor?.TenantGroupId,
                        outcome,
                        failureReason,
                        durationMs,
                        descriptor is null
                            ? new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId, ["found"] = false, [AiObservabilityMetadataKeys.DurationMs] = durationMs }
                            : CreateDescriptorProperties(descriptor, durationMs),
                        cancellationToken)
                    .ConfigureAwait(false);

                return descriptor;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationFailed,
                        CapacityGetOperation,
                        runtimeInstanceId,
                        null,
                        null,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        CreateFailureProperties(new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId, [AiObservabilityMetadataKeys.DurationMs] = durationMs }, exception),
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordAsync(
                    AiControlPlaneEventType.OperationStarted,
                    CapacityListOperation,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>(),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var descriptors = await this.inner
                    .ListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        CapacityListOperation,
                        null,
                        null,
                        null,
                        null,
                        AiControlPlaneOperationOutcome.Succeeded,
                        null,
                        durationMs,
                        new Dictionary<string, object?> { ["descriptorCount"] = descriptors.Count, [AiObservabilityMetadataKeys.DurationMs] = durationMs },
                        cancellationToken)
                    .ConfigureAwait(false);

                return descriptors;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationFailed,
                        CapacityListOperation,
                        null,
                        null,
                        null,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        CreateFailureProperties(new Dictionary<string, object?> { [AiObservabilityMetadataKeys.DurationMs] = durationMs }, exception),
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordAsync(
                    AiControlPlaneEventType.OperationStarted,
                    CapacityRemoveOperation,
                    runtimeInstanceId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var removed = await this.inner
                    .RemoveAsync(runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);
                var outcome = removed
                    ? AiControlPlaneOperationOutcome.Succeeded
                    : AiControlPlaneOperationOutcome.CompletedWithIssues;
                var failureReason = removed
                    ? null
                    : "runtime-instance-capacity-not-removed";

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        CapacityRemoveOperation,
                        runtimeInstanceId,
                        null,
                        null,
                        null,
                        outcome,
                        failureReason,
                        durationMs,
                        new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId, ["removed"] = removed, [AiObservabilityMetadataKeys.DurationMs] = durationMs },
                        cancellationToken)
                    .ConfigureAwait(false);

                return removed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var durationMs = CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow);

                await this.RecordAsync(
                        AiControlPlaneEventType.OperationFailed,
                        CapacityRemoveOperation,
                        runtimeInstanceId,
                        null,
                        null,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        CreateFailureProperties(new Dictionary<string, object?> { [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId, [AiObservabilityMetadataKeys.DurationMs] = durationMs }, exception),
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Records a capacity store control-plane event.
        /// </summary>
        /// <param name="eventType">The event type.</param>
        /// <param name="operation">The operation name.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="outcome">The operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordAsync(
            AiControlPlaneEventType eventType,
            string operation,
            string? runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?> properties,
            CancellationToken cancellationToken)
        {
            try
            {
                var effectiveCorrelationId = string.IsNullOrWhiteSpace(runtimeInstanceId)
                    ? $"capacity-store:{operation}:{Guid.NewGuid():N}"
                    : runtimeInstanceId;

                var effectiveProperties = new Dictionary<string, object?>(properties, StringComparer.Ordinal)
                {
                    ["operation"] = operation,
                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId,
                    [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = tenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = tenantGroupId
                };

                await this.observer
                    .RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.InstanceRegistry,
                            Operation = operation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = effectiveCorrelationId,
                                RuntimeInstanceId = runtimeInstanceId
                            },
                            Properties = effectiveProperties
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must never break runtime capacity visibility operations.
            }
        }

        /// <summary>
        /// Creates event properties from a runtime instance capacity descriptor.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <returns>The event properties.</returns>
        private static IReadOnlyDictionary<string, object?> CreateDescriptorProperties(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            long? durationMs)
        {
            return new Dictionary<string, object?>
            {
                [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = descriptor.RuntimeInstanceId,
                [AiControlPlaneMetadataKeys.ControlPlaneId] = descriptor.ControlPlaneId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = descriptor.TenantId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = descriptor.TenantGroupId,
                ["role"] = descriptor.Role.ToString(),
                ["status"] = descriptor.Status.ToString(),
                ["workerCount"] = descriptor.WorkerCount,
                ["activeWorkerCount"] = descriptor.ActiveWorkerCount,
                ["availableWorkerCount"] = descriptor.AvailableWorkerCount,
                ["queuedRunCount"] = descriptor.QueuedRunCount,
                ["runningRunCount"] = descriptor.RunningRunCount,
                ["activeRunCount"] = descriptor.ActiveRunCount,
                ["maxConcurrentRuns"] = descriptor.MaxConcurrentRuns,
                ["maxRunSlots"] = descriptor.MaxRunSlots,
                ["availableRunSlots"] = descriptor.AvailableRunSlots,
                ["reservedRunSlots"] = descriptor.ReservedRunSlots,
                ["effectiveAvailableRunSlots"] = descriptor.EffectiveAvailableRunSlots,
                ["isQueuePaused"] = descriptor.IsQueuePaused,
                ["canAcceptRun"] = descriptor.CanAcceptRun,
                [AiObservabilityMetadataKeys.DurationMs] = durationMs
            };
        }

        /// <summary>
        /// Adds exception details to event properties.
        /// </summary>
        /// <param name="properties">The original properties.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>The failure event properties.</returns>
        private static IReadOnlyDictionary<string, object?> CreateFailureProperties(
            IReadOnlyDictionary<string, object?> properties,
            Exception exception)
        {
            var result = new Dictionary<string, object?>(properties, StringComparer.Ordinal)
            {
                [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName,
                [AiExceptionMetadataKeys.ExceptionMessage] = exception.Message
            };

            return result;
        }

        /// <summary>
        /// Calculates elapsed duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="completedAtUtc">The completion timestamp.</param>
        /// <returns>The elapsed duration in milliseconds.</returns>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }
    }
}
