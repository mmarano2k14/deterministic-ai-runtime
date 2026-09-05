using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeHostedService : IHostedService
    {
        private readonly IAiRuntimePipelineBackgroundController _controller;

        public RuntimeAnalysisRuntimeHostedService(
            IAiRuntimePipelineBackgroundController controller)
        {
            _controller =
                controller
                ?? throw new ArgumentNullException(
                    nameof(controller));
        }

        public Task StartAsync(
            CancellationToken cancellationToken)
        {
            return _controller.StartAsync(
                cancellationToken);
        }

        public Task StopAsync(
            CancellationToken cancellationToken)
        {
            return _controller.StopAsync(
                cancellationToken);
        }
    }
}
