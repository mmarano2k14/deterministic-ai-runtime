using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Opt-in control-plane polling. One page per scope/language per pass, independent DI scopes
    /// per invocation and a bounded shared launch capacity. Cursors are hints and may restart safely.
    /// </summary>
    public sealed class AiWorkerDispatchHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly AiWorkerPollingOptions _polling;
        private readonly AiWorkerSupervisionOptions _supervision;
        private readonly ILogger<AiWorkerDispatchHostedService> _logger;
        private readonly TimeProvider _time;
        private readonly Dictionary<(AiDurableInvocationScope Scope, string Language), AiWorkerDispatchCursor?> _cursors = new();
        public AiWorkerDispatchHostedService(IServiceScopeFactory scopes, AiWorkerPollingOptions polling,
            AiWorkerSupervisionOptions supervision, ILogger<AiWorkerDispatchHostedService> logger, TimeProvider? timeProvider = null)
        { _scopes = scopes; _polling = polling; _supervision = supervision; _logger = logger; _time = timeProvider ?? TimeProvider.System; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var scope in _polling.Scopes)
                        foreach (var language in _polling.ExecutionLanguages)
                        {
                            try { await PollOnceAsync(scope, language, stoppingToken).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                            catch (Exception exception)
                            { _logger.LogWarning("Worker dispatch page failed. ExceptionType={ExceptionType}.", exception.GetType().FullName); }
                        }
                    await Task.Delay(_polling.Interval, _time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
        private async Task PollOnceAsync(AiDurableInvocationScope tenant, string language, CancellationToken token)
        {
            var key = (tenant, language); _cursors.TryGetValue(key, out var cursor);
            IReadOnlyList<AiDurableInvocationRecord> page;
            using (var readScope = _scopes.CreateScope())
                page = await readScope.ServiceProvider.GetRequiredService<AiWorkerDispatchPageReader>()
                    .ReadAsync(tenant, language, _polling.PageSize, cursor, token).ConfigureAwait(false);
            _cursors[key] = page.Count == 0 ? null : new(page[^1].UpdatedAtUtc, page[^1].OperationId);
            await Parallel.ForEachAsync(page, new ParallelOptions
                { CancellationToken = token, MaxDegreeOfParallelism = _supervision.MaxConcurrentProcesses },
                async (candidate, cancellation) =>
                {
                    try
                    {
                        using var invocationScope = _scopes.CreateScope();
                        var result = await invocationScope.ServiceProvider.GetRequiredService<AiWorkerInvocationSupervisor>()
                            .DispatchAsync(tenant, candidate, cancellation).ConfigureAwait(false);
                        if (result.Disposition is AiWorkerDispatchDisposition.ReconciliationRequired or AiWorkerDispatchDisposition.CapacityQuarantined)
                            _logger.LogWarning("Worker dispatch requires attention. OperationId={OperationId}, Disposition={Disposition}.",
                                result.OperationId, result.Disposition);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    { _logger.LogWarning("Worker dispatch candidate failed. ExceptionType={ExceptionType}.", exception.GetType().FullName); }
                }).ConfigureAwait(false);
        }
    }
}
