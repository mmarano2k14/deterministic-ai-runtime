using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiplexed.Realtime.DI;
using Multiplexed.Realtime.Events.Abstractions;
using Multiplexed.Realtime.Events.Runtime;
using Multiplexed.Realtime.Handlers;

namespace Multiplexed.AI.Tests.Unit.Realtime
{
    /// <summary>
    /// Verifies that runtime log events are mirrored to the standard logging
    /// pipeline without replacing realtime transport delivery.
    /// </summary>
    public sealed class SystemLogRuntimeEventHandlerTests
    {
        /// <summary>
        /// Ensures both the existing realtime transport handler and the new
        /// system-log handler are registered for runtime log events.
        /// </summary>
        [Fact]
        public void AddMultiplexRealtime_Should_Register_Both_RuntimeLog_Handlers()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMultiplexRealtime();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var handlers = scope.ServiceProvider
                .GetServices<IRuntimeEventHandler<RuntimeLogEvent>>()
                .ToArray();

            Assert.Single(
                handlers.OfType<RealtimeDispatchHandler<RuntimeLogEvent>>());

            Assert.Single(
                handlers.OfType<SystemLogRuntimeEventHandler>());
        }

        /// <summary>
        /// Ensures an error runtime event is written at the matching system-log
        /// level with its structured category, message, and payload preserved.
        /// </summary>
        [Fact]
        public async Task HandleAsync_Should_Write_Structured_Runtime_Event_To_System_Log()
        {
            var logger = new CapturingLogger<SystemLogRuntimeEventHandler>();
            var handler = new SystemLogRuntimeEventHandler(logger);
            var occurredAtUtc = new DateTimeOffset(2026, 7, 29, 10, 14, 57, TimeSpan.Zero);

            await handler.HandleAsync(
                new RuntimeLogEvent
                {
                    Level = "Error",
                    Message = "Step 'step-002' threw an exception.",
                    Category = "ai.step.exception",
                    UserId = "tenant-a",
                    Data = new
                    {
                        ExecutionId = "execution-1",
                        Step = "step-002",
                        Exception = "redis-timeout"
                    },
                    RealtimeTarget = RealtimeTarget.Group("runtime-console"),
                    OccurredAtUtc = occurredAtUtc
                });

            var entry = Assert.Single(logger.Entries);

            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("ai.step.exception", entry.Message);
            Assert.Contains("Step 'step-002' threw an exception.", entry.Message);
            Assert.Contains("execution-1", entry.Message);
            Assert.Contains("redis-timeout", entry.Message);
        }

        /// <summary>
        /// Ensures unknown runtime levels remain visible instead of being dropped.
        /// </summary>
        [Fact]
        public async Task HandleAsync_Should_Fallback_To_Information_For_Unknown_Level()
        {
            var logger = new CapturingLogger<SystemLogRuntimeEventHandler>();
            var handler = new SystemLogRuntimeEventHandler(logger);

            await handler.HandleAsync(
                new RuntimeLogEvent
                {
                    Level = "custom",
                    Message = "Custom runtime event.",
                    Category = "runtime.custom",
                    RealtimeTarget = RealtimeTarget.Group("runtime-console")
                });

            var entry = Assert.Single(logger.Entries);

            Assert.Equal(LogLevel.Information, entry.Level);
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<LogEntry> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add(
                    new LogEntry(
                        logLevel,
                        formatter(state, exception),
                        exception));
            }
        }

        private sealed record LogEntry(
            LogLevel Level,
            string Message,
            Exception? Exception);
    }
}
