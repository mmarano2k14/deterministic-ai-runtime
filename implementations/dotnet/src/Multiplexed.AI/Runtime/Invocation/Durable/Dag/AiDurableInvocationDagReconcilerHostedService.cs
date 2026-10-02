using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Explicit host opt-in. Scoped runtime dependencies are resolved anew for each tenant pass.</summary>
    public sealed class AiDurableInvocationDagReconcilerHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<AiDurableInvocationDagReconcilerHostedService> _logger;
        private readonly AiDurableInvocationScope[] _tenants;
        private readonly TimeSpan _interval;
        private readonly int _batch;
        private readonly Dictionary<AiDurableInvocationScope, AiDurableInvocationContinuationCursor?> _cursors = new();
        public AiDurableInvocationDagReconcilerHostedService(IServiceScopeFactory scopes,
            AiDurableInvocationDagReconciliationOptions options, ILogger<AiDurableInvocationDagReconcilerHostedService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Interval < TimeSpan.FromMilliseconds(100) || options.Interval > TimeSpan.FromMinutes(5) ||
                options.BatchSize is < 1 or > 100 || options.Scopes is null || options.Scopes.Count == 0)
                throw new ArgumentException("Reconciliation requires explicit scopes, a bounded batch and a 100ms..5min interval.", nameof(options));
            _tenants = options.Scopes.Distinct().ToArray();
            foreach (var tenant in _tenants) AiDurableInvocationValidation.ValidateScope(tenant);
            _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interval = options.Interval; _batch = options.BatchSize;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var tenant in _tenants)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var scope = _scopes.CreateScope();
                            _cursors.TryGetValue(tenant, out var cursor);
                            var result = await scope.ServiceProvider.GetRequiredService<AiDurableInvocationDagReconciler>()
                                .ReconcilePageAsync(tenant, _batch, cursor, stoppingToken).ConfigureAwait(false);
                            _cursors[tenant] = result.NextCursor;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                        catch (Exception exception)
                        {
                            _logger.LogWarning("Durable invocation reconciliation pass failed. ExceptionType={ExceptionType}.", exception.GetType().FullName);
                        }
                    }
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
