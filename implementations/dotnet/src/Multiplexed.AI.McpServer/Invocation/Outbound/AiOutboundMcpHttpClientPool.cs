using System.Net;

namespace Multiplexed.AI.McpServer.Invocation.Outbound
{
    /// <summary>
    /// Reuses one sockets handler while each MCP invocation receives its own HttpClient/session.
    /// Redirects are disabled so an approved endpoint cannot silently redirect to another host.
    /// </summary>
    internal sealed class AiOutboundMcpHttpClientPool : IDisposable
    {
        private readonly SocketsHttpHandler _handler;
        private bool _disposed;

        public AiOutboundMcpHttpClientPool(AiOutboundMcpToolExecutionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = options.ConnectionTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 64
            };
        }

        public HttpClient CreateClient()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AiOutboundMcpHttpClientPool));
            }
            return new HttpClient(_handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _handler.Dispose();
        }
    }
}
