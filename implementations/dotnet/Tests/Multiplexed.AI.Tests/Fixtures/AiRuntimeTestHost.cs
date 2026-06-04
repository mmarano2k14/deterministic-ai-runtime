using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Tests.Runtime.Execution.MultiInstance
{
    /// <summary>
    /// Represents one isolated AI runtime host participating in distributed DAG execution tests.
    /// </summary>
    internal sealed class AiRuntimeTestHost : IDisposable
    {
        private IAiExecutionEngine? _engine;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeTestHost"/> class.
        /// </summary>
        /// <param name="runtimeInstanceId">The expected runtime instance identifier.</param>
        /// <param name="serviceProvider">The service provider backing this runtime host.</param>
        public AiRuntimeTestHost(
            string runtimeInstanceId,
            ServiceProvider serviceProvider)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            RuntimeInstanceId = runtimeInstanceId;
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            RuntimeInstanceIdentity =
                ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();
        }

        /// <summary>
        /// Gets the expected runtime instance identifier for this host.
        /// </summary>
        public string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the service provider backing this runtime host.
        /// </summary>
        public ServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the runtime instance identity resolved by this host.
        /// </summary>
        public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <summary>
        /// Gets the AI execution engine for this runtime host.
        /// </summary>
        /// <remarks>
        /// The engine is resolved lazily because some fixture tests only validate runtime
        /// instance identity isolation and do not require full AI runtime registration.
        /// </remarks>
        public IAiExecutionEngine Engine =>
            _engine ??= ServiceProvider.GetRequiredService<IAiExecutionEngine>();

        /// <summary>
        /// Disposes the underlying service provider.
        /// </summary>
        public void Dispose()
        {
            ServiceProvider.Dispose();
        }
    }

    public sealed class FakeRuntimeInstanceWorker : IAiRuntimeInstanceWorker
    {
        public int RunCount { get; private set; }

        public Task<AiExecutionRecord> RunExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            RunCount++;

            return Task.FromResult(
                new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    Status = AiExecutionStatus.Completed
                });
        }
    }
}