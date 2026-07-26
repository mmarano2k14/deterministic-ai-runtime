using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Provides process-local, concurrency-safe TCP port leases for runtime pool children.
    /// </summary>
    public sealed class AiRuntimeProcessPoolPortAllocator :
        IAiRuntimeProcessPoolPortAllocator
    {
        private static readonly SemaphoreSlim AllocationGate = new(1, 1);
        private static readonly ConcurrentDictionary<int, byte> ReservedPorts = new();

        /// <inheritdoc />
        public async Task<IAiRuntimeProcessPoolPortLease> ReserveAsync(
            int basePort,
            int maxPort,
            CancellationToken cancellationToken = default)
        {
            if (basePort <= 0 || basePort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(basePort));
            }

            if (maxPort < basePort || maxPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPort));
            }

            await AllocationGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                for (var port = basePort; port <= maxPort; port++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ReservedPorts.TryAdd(port, 0))
                    {
                        continue;
                    }

                    if (IsAvailable(port))
                    {
                        return new PortLease(port);
                    }

                    ReservedPorts.TryRemove(port, out _);
                }
            }
            finally
            {
                AllocationGate.Release();
            }

            throw new InvalidOperationException(
                $"No available runtime pool child port exists between {basePort} and {maxPort}.");
        }

        /// <summary>
        /// Tests whether the operating system can bind the candidate port.
        /// </summary>
        /// <param name="port">The candidate port.</param>
        /// <returns><see langword="true"/> when the port can be bound.</returns>
        private static bool IsAvailable(
            int port)
        {
            TcpListener? listener = null;

            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                listener?.Stop();
            }
        }

        /// <summary>
        /// Releases one process-local port reservation exactly once.
        /// </summary>
        private sealed class PortLease : IAiRuntimeProcessPoolPortLease
        {
            private int disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="PortLease"/> class.
            /// </summary>
            /// <param name="port">The reserved port.</param>
            public PortLease(
                int port)
            {
                this.Port = port;
            }

            /// <inheritdoc />
            public int Port { get; }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                {
                    ReservedPorts.TryRemove(this.Port, out _);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
