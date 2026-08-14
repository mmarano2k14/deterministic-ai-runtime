using Microsoft.Extensions.Logging;
using Multiplexed.Realtime.Events.Runtime;

namespace Multiplexed.Realtime.Handlers
{
    /// <summary>
    /// Mirrors structured runtime log events to the standard host logging pipeline.
    ///
    /// The realtime transport handler remains registered independently, so adding
    /// this sink does not replace or alter SignalR, WebSocket, or null transport
    /// delivery. In containerized hosts, these entries are written to stdout/stderr
    /// by the configured logging provider and are therefore visible through
    /// <c>kubectl logs</c>.
    /// </summary>
    public sealed class SystemLogRuntimeEventHandler : IRuntimeEventHandler<RuntimeLogEvent>
    {
        private readonly ILogger<SystemLogRuntimeEventHandler> _logger;

        /// <summary>
        /// Initializes a new system-log runtime event handler.
        /// </summary>
        public SystemLogRuntimeEventHandler(
            ILogger<SystemLogRuntimeEventHandler> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task HandleAsync(
            RuntimeLogEvent @event,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(@event);

            _logger.Log(
                ResolveLogLevel(@event.Level),
                "[RUNTIME EVENT] Level='{RuntimeLevel}', Category='{RuntimeCategory}', UserId='{RuntimeUserId}', OccurredAtUtc='{OccurredAtUtc:O}', Message='{RuntimeMessage}', Data={RuntimeData}",
                @event.Level,
                @event.Category,
                @event.UserId,
                @event.OccurredAtUtc,
                @event.Message,
                @event.Data);

            return Task.CompletedTask;
        }

        private static LogLevel ResolveLogLevel(string? level)
        {
            return level?.Trim().ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "information" or "info" => LogLevel.Information,
                "warning" or "warn" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "critical" or "fatal" => LogLevel.Critical,
                "none" => LogLevel.None,
                _ => LogLevel.Information
            };
        }
    }
}
