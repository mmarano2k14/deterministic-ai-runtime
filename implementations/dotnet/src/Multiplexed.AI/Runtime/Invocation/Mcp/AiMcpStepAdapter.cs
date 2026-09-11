using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Executes one short MCP attempt through the existing IAiStep/DAG path. No AiStep
    /// attribute: a contextual adapter must never be registered as a native step.
    /// The adapter holds plan metadata only, not the last tenant, arguments or result.
    /// </summary>
    public sealed class AiMcpStepAdapter : IAiStep
    {
        private readonly AiStepInvocationAdapterContext _metadata;
        private readonly IAiMcpToolResolver _resolver;
        private readonly IAiMcpToolTransport _transport;
        private readonly TimeSpan _timeout;

        internal AiMcpStepAdapter(
            AiStepInvocationAdapterContext metadata,
            IAiMcpToolResolver resolver,
            IAiMcpToolTransport transport,
            TimeSpan timeout)
        {
            _metadata = metadata;
            _resolver = resolver;
            _transport = transport;
            _timeout = timeout;
        }

        public string Name => _metadata.StepName;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.Record.ExecutionMode != AiExecutionMode.Dag || context.ConcurrencyAdmissionDefinition is not null ||
                context.InvocationBinding != _metadata.Binding || context.StepName != _metadata.StepName ||
                context.StepKey != _metadata.StepKey || context.Record.PipelineName != _metadata.PipelineName ||
                string.IsNullOrWhiteSpace(context.ExecutionId))
            {
                throw new InvalidOperationException("An MCP adapter can execute only its compiled DAG step, never an admission context.");
            }

            var identity = AiMcpInvocationIdentity.Capture(context);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
            var deadlineUtc = DateTimeOffset.UtcNow.Add(_timeout);
            deadline.CancelAfter(_timeout);
            try
            {
                var resolutionRequest = new AiMcpToolResolutionRequest(identity.TenantId, identity.TenantGroupId,
                    _metadata.Binding.ConnectionRef!, _metadata.Binding.Tool!);
                var target = await AwaitBoundedAsync(_resolver.ResolveAsync(resolutionRequest, deadline.Token), deadline.Token)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The MCP connection/tool is unavailable for this tenant.");
                deadline.Token.ThrowIfCancellationRequested();
                identity.ValidateTarget(target, _metadata.Binding);
                identity.Authorize(context.Services, target);

                // Reuse runtime path/payload resolution. Only declared inputs are sent;
                // Config, state, reserved values and runtime services are not auto-added.
                var inputs = await AwaitBoundedAsync(context.GetHelper().GetResolvedInputsAsync(
                    includeReservedVariables: false, cancellationToken: deadline.Token), deadline.Token).ConfigureAwait(false);
                var arguments = AiMcpToolJson.CopyArguments(inputs);
                deadline.Token.ThrowIfCancellationRequested();
                identity.EnsureCurrent(context.Services);
                var request = new AiMcpToolRequest(1, Guid.NewGuid().ToString("N"), deadlineUtc,
                    new AiMcpToolInvocationContext(identity.TenantId, identity.TenantGroupId, context.ExecutionId,
                        _metadata.PipelineName, _metadata.PipelineVersion, Name, _metadata.StepKey),
                    target.ConnectionRef, target.ConnectionRevision, target.Tool, arguments);

                var response = await AwaitBoundedAsync(_transport.InvokeAsync(request, deadline.Token), deadline.Token)
                    .ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var result = AiMcpToolResponseReader.Read(response, request.RequestId);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                !context.CancellationToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("MCP invocation exceeded the server deadline; the external outcome may be unknown.", ex);
                }
                throw new InvalidOperationException("An MCP dependency cancelled without runtime cancellation or deadline expiry.", ex);
            }
        }

        private static async Task<T> AwaitBoundedAsync<T>(Task<T>? pending, CancellationToken cancellationToken)
        {
            if (pending is null) throw new InvalidOperationException("An MCP invocation dependency returned no task.");
            try
            {
                return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A dependency may ignore cancellation. Do not accept its late result;
                // observe a late fault without a retry or a detached runtime mutation.
                _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
        }
    }
}
