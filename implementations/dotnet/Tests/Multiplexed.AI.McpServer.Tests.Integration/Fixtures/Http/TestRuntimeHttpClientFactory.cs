using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http
{
    public sealed class TestRuntimeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient client;

        public TestRuntimeHttpClientFactory(
            HttpClient client)
        {
            this.client =
                client ?? throw new ArgumentNullException(nameof(client));
        }

        public HttpClient CreateClient(
            string name)
        {
            return client;
        }
    }
}
