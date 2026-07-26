using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Owns one generated child gRPC client and channel.
    /// </summary>
    public sealed class AiRuntimePoolGrpcClient :
        IAiRuntimePoolGrpcClient
    {
        private readonly GrpcChannel channel;
        private readonly AiRuntimeInstanceCommandGrpc
            .AiRuntimeInstanceCommandGrpcClient client;
        private int disposed;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolGrpcClient"/> class.
        /// </summary>
        /// <param name="channel">The owned gRPC channel.</param>
        /// <param name="client">The generated command client.</param>
        public AiRuntimePoolGrpcClient(
            GrpcChannel channel,
            AiRuntimeInstanceCommandGrpc
                .AiRuntimeInstanceCommandGrpcClient client)
        {
            this.channel =
                channel
                ?? throw new ArgumentNullException(nameof(channel));

            this.client =
                client
                ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceGrpcCommandResponse>
            ExecuteCommandAsync(
                AiRuntimeInstanceGrpcCommandRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref this.disposed) != 0,
                this);

            return await this.client
                .ExecuteCommandAsync(
                    request,
                    cancellationToken:
                        cancellationToken)
                .ResponseAsync
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref this.disposed,
                    1) == 0)
            {
                this.channel.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
