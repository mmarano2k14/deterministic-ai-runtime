using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Records runtime recovery forensics evidence using best-effort persistence.
    /// </summary>
    public sealed class BestEffortAiRuntimeRecoveryForensicsRecorder : IAiRuntimeRecoveryForensicsRecorder
    {
        private readonly IAiRuntimeRecoveryForensicsStore _store;
        private readonly AiRuntimeRecoveryForensicsOptions _options;
        private readonly ILogger<BestEffortAiRuntimeRecoveryForensicsRecorder> _logger;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="BestEffortAiRuntimeRecoveryForensicsRecorder"/> class.
        /// </summary>
        /// <param name="store">The runtime recovery forensics store.</param>
        /// <param name="options">The runtime recovery forensics options.</param>
        /// <param name="logger">The logger.</param>
        public BestEffortAiRuntimeRecoveryForensicsRecorder(
            IAiRuntimeRecoveryForensicsStore store,
            IOptions<AiRuntimeRecoveryForensicsOptions> options,
            ILogger<BestEffortAiRuntimeRecoveryForensicsRecorder> logger)
        {
            _store = store;
            _options = options.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task RecordAsync(
            AiRuntimeRecoveryForensicsRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (!_options.Enabled)
            {
                return;
            }

            try
            {
                await _store
                    .UpsertAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_options.StrictPersistence)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist runtime recovery forensics record. " +
                        "ForensicsId={ForensicsId} ExecutionId={ExecutionId} " +
                        "ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                        record.Identity.ForensicsId,
                        record.Identity.ExecutionId,
                        ex.GetType().FullName,
                        ex.Message);

                    throw;
                }

                _logger.LogWarning(
                    ex,
                    "Failed to persist runtime recovery forensics record. " +
                    "ForensicsId={ForensicsId} ExecutionId={ExecutionId} " +
                    "ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                    record.Identity.ForensicsId,
                    record.Identity.ExecutionId,
                    ex.GetType().FullName,
                    ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task RecordEventAsync(
            AiRuntimeRecoveryForensicsEvent evt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evt);

            if (!_options.Enabled)
            {
                return;
            }

            var diagnoseResumeContextSeeded = string.Equals(
                evt.EventType,
                AiEngineEvents.Recovery.ResumeContextSeeded,
                StringComparison.Ordinal);

            try
            {
                if (diagnoseResumeContextSeeded)
                {
                    _logger.LogWarning(
                        "[FORENSICS DIAGNOSTIC BEFORE] " +
                        "ForensicsId={ForensicsId} EventId={EventId} EventType={EventType}",
                        evt.ForensicsId,
                        evt.EventId,
                        evt.EventType);
                }

                await _store
                    .AppendEventAsync(evt.ForensicsId, evt, cancellationToken)
                    .ConfigureAwait(false);

                if (diagnoseResumeContextSeeded)
                {
                    _logger.LogWarning(
                        "[FORENSICS DIAGNOSTIC AFTER] " +
                        "ForensicsId={ForensicsId} EventId={EventId} EventType={EventType}",
                        evt.ForensicsId,
                        evt.EventId,
                        evt.EventType);
                }
            }
            catch (Exception ex)
            {
                if (_options.StrictPersistence)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist runtime recovery forensics event. " +
                        "ForensicsId={ForensicsId} EventId={EventId} EventType={EventType} " +
                        "ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                        evt.ForensicsId,
                        evt.EventId,
                        evt.EventType,
                        ex.GetType().FullName,
                        ex.Message);

                    throw;
                }

                _logger.LogWarning(
                    ex,
                    "Failed to persist runtime recovery forensics event. " +
                    "ForensicsId={ForensicsId} EventId={EventId} EventType={EventType} " +
                    "ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage}",
                    evt.ForensicsId,
                    evt.EventId,
                    evt.EventType,
                    ex.GetType().FullName,
                    ex.Message);
            }
        }
    }
}