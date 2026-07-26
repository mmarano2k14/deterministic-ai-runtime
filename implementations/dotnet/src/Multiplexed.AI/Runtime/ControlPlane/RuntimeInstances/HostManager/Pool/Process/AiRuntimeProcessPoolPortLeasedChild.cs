using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Releases a runtime child transport port when the underlying child lifecycle completes.
    /// </summary>
    public sealed class AiRuntimeProcessPoolPortLeasedChild :
        IAiRuntimeProcessPoolChild
    {
        private readonly IAiRuntimeProcessPoolChild inner;
        private readonly IAiRuntimeProcessPoolPortLease portLease;
        private readonly Task<AiRuntimeProcessPoolChildExit> completion;
        private int leaseReleased;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeProcessPoolPortLeasedChild"/> class.
        /// </summary>
        /// <param name="inner">The underlying runtime child.</param>
        /// <param name="portLease">The child transport port lease.</param>
        public AiRuntimeProcessPoolPortLeasedChild(
            IAiRuntimeProcessPoolChild inner,
            IAiRuntimeProcessPoolPortLease portLease)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.portLease = portLease ?? throw new ArgumentNullException(nameof(portLease));
            this.completion = this.ObserveCompletionAsync();
        }

        /// <inheritdoc />
        public string PoolId => this.inner.PoolId;

        /// <inheritdoc />
        public string HostId => this.inner.HostId;

        /// <inheritdoc />
        public string RuntimeInstanceId => this.inner.RuntimeInstanceId;

        /// <inheritdoc />
        public int Ordinal => this.inner.Ordinal;

        /// <inheritdoc />
        public AiRuntimeProcessPoolChildStatus Status => this.inner.Status;

        /// <inheritdoc />
        public Task<AiRuntimeProcessPoolChildExit> Completion => this.completion;

        /// <inheritdoc />
        public Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            return this.inner.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Releases the child transport port lease exactly once.
        /// </summary>
        /// <returns>A task representing asynchronous lease release.</returns>
        public async ValueTask ReleasePortLeaseAsync()
        {
            if (Interlocked.Exchange(ref this.leaseReleased, 1) == 0)
            {
                await this.portLease.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Observes the inner child completion and always releases its transport port.
        /// </summary>
        private async Task<AiRuntimeProcessPoolChildExit> ObserveCompletionAsync()
        {
            try
            {
                return await this.inner.Completion.ConfigureAwait(false);
            }
            finally
            {
                await this.ReleasePortLeaseAsync().ConfigureAwait(false);
            }
        }
    }
}
