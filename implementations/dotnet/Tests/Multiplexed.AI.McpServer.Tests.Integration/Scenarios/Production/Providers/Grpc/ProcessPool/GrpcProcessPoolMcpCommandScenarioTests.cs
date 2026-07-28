using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Grpc.RuntimePool;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.ProcessPool
{
    /// <summary>
    /// Proves exact gRPC command routing from the MCP integration composition to three real
    /// RuntimeInstanceOnly child processes.
    /// </summary>
    [Collection(GrpcRuntimePoolMcpProofCollection.Name)]
    [Trait("Category", "GrpcProcessPool")]
    public sealed class GrpcProcessPoolMcpCommandScenarioTests
    {
        private readonly GrpcProcessPoolMcpProofFixture fixture;

        /// <summary>
        /// Initializes the gRPC Process Pool proof.
        /// </summary>
        public GrpcProcessPoolMcpCommandScenarioTests(
            GrpcProcessPoolMcpProofFixture fixture)
        {
            this.fixture =
                fixture
                ?? throw new ArgumentNullException(
                    nameof(fixture));
        }

        /// <summary>
        /// Sends one command to every child through the same stable HTTP/2 endpoint and proves
        /// that no command falls back to another runtime instance.
        /// </summary>
        [Fact]
        public async Task Grpc_ProcessPool_Should_Route_Exact_Commands_To_All_Real_Children()
        {
            Assert.Equal(
                3,
                this.fixture.RuntimeInstanceIds.Count);

            using var timeout =
                new CancellationTokenSource(
                    this.fixture.TestTimeout);

            var results =
                await Task.WhenAll(
                        this.fixture.RuntimeInstanceIds
                            .Select(
                                runtimeInstanceId =>
                                    GrpcRuntimePoolCommandClient
                                        .GetQueueStatusAsync(
                                            this.fixture.Client,
                                            runtimeInstanceId,
                                            "grpc-process-pool-5f",
                                            timeout.Token)))
                    .ConfigureAwait(false);

            Assert.Equal(
                3,
                results.Length);

            Assert.All(
                results,
                result =>
                    Assert.True(
                        result.Success,
                        string.Concat(
                            "gRPC Process Pool command failed. FailureReason=",
                            result.FailureReason ?? "<null>",
                            "; Message=",
                            result.Message ?? "<null>",
                            "; RuntimeInstanceId=",
                            result.RuntimeInstanceId)));

            Assert.Equal(
                this.fixture.RuntimeInstanceIds
                    .OrderBy(value => value, StringComparer.Ordinal),
                results
                    .Select(result => result.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal));

            Assert.Equal(
                3,
                results
                    .Select(result => result.RuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }
    }
}
