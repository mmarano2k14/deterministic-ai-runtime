using System.Net;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Proves exact gRPC routing through one stable Kestrel HTTP/2 endpoint and real
    /// RuntimeInstanceOnly child processes.
    /// </summary>
    [Collection(RuntimeProcessPoolEndToEndCollection.Name)]
    [Trait("Category", "RuntimePoolGrpcEndToEnd")]
    public sealed class RuntimePoolGrpcEndToEndProofTests :
        IClassFixture<RuntimeProcessPoolEndToEndTestFixture>
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly RuntimeProcessPoolEndToEndTestFixture fixture;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RuntimePoolGrpcEndToEndProofTests"/> class.
        /// </summary>
        /// <param name="fixture">
        /// The centralized strongly typed process-pool test fixture.
        /// </param>
        public RuntimePoolGrpcEndToEndProofTests(
            RuntimeProcessPoolEndToEndTestFixture fixture)
        {
            this.fixture =
                fixture
                ?? throw new ArgumentNullException(
                    nameof(fixture));
        }

        /// <summary>
        /// Proves stable exact gRPC routing before and after one real child-process failure.
        /// </summary>
        [Fact]
        public async Task Stable_Grpc_Endpoint_Should_Route_Exact_Runtime_Before_And_After_Replacement()
        {
            var identity =
                this.fixture.CreateIdentity();

            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(
                options =>
                    options.Listen(
                        IPAddress.Loopback,
                        port: 0,
                        listenOptions =>
                            listenOptions.Protocols =
                                HttpProtocols.Http2));

            this.fixture.ConfigureControlPlane(
                builder.Configuration,
                identity);

            builder.Services.AddSingleton(
                this.fixture.RedisConnection);

            this.fixture.RegisterControlPlaneIdentity(
                builder.Services,
                identity);

            builder.Services.AddAiControlPlane(
                configuration: builder.Configuration);

            builder.Services.AddSingleton<
                TrackingSystemRuntimeProcessPoolLauncher>();

            builder.Services.AddSingleton<
                IAiRuntimeProcessPoolChildProcessLauncher>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        TrackingSystemRuntimeProcessPoolLauncher>());

            builder.Services.AddAiRuntimeProcessPool(
                this.fixture.CreatePoolOptions(identity),
                this.fixture.CreateGrpcRuntimeInstanceOptions(
                    identity));

            await using var application =
                builder.Build();

            application.MapAiRuntimePoolGrpcCommandService();

            using var timeout =
                new CancellationTokenSource(
                    this.fixture.TestTimeout);

            await application.StartAsync(
                timeout.Token);

            try
            {
                var manager =
                    application.Services.GetRequiredService<
                        IAiRuntimeProcessPoolManager>();

                var routeRegistry =
                    application.Services.GetRequiredService<
                        IAiRuntimePoolRouteRegistry>();

                var launcher =
                    application.Services.GetRequiredService<
                        TrackingSystemRuntimeProcessPoolLauncher>();

                var initial =
                    await WaitForHealthyPoolAsync(
                        manager,
                        routeRegistry,
                        expectedRuntimeIds: null,
                        timeout.Token);

                var runtimeA1 =
                    initial.Children.Single(
                        child => child.Ordinal == 1);

                var runtimeA2 =
                    initial.Children.Single(
                        child => child.Ordinal == 2);

                var runtimeA3 =
                    initial.Children.Single(
                        child => child.Ordinal == 3);

                var initialRoutes =
                    await routeRegistry
                        .ListByHostIdAsync(
                            initial.HostId,
                            timeout.Token)
                        .ConfigureAwait(false);

                var routeA2 =
                    initialRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA2.RuntimeInstanceId));

                var routeA3 =
                    initialRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA3.RuntimeInstanceId));

                using var channel =
                    CreateStableRouterChannel(
                        application.Services);

                var client =
                    new AiRuntimeInstanceCommandGrpc
                        .AiRuntimeInstanceCommandGrpcClient(
                            channel);

                var beforeFailure =
                    await SendCommandAsync(
                        client,
                        runtimeA2.RuntimeInstanceId,
                        timeout.Token);

                AssertCommandSucceeded(
                    beforeFailure,
                    "before child failure");

                Assert.Equal(
                    runtimeA2.RuntimeInstanceId,
                    beforeFailure.RuntimeInstanceId);

                launcher.KillUnexpectedly(
                    runtimeA1.RuntimeInstanceId);

                var replacement =
                    await WaitForReplacementAsync(
                        manager,
                        routeRegistry,
                        runtimeA1.RuntimeInstanceId,
                        runtimeA2.RuntimeInstanceId,
                        runtimeA3.RuntimeInstanceId,
                        timeout.Token);

                var runtimeA4 =
                    Assert.Single(
                        replacement.Children.Where(
                            child => child.Ordinal == 4));

                var replacementRoutes =
                    await routeRegistry
                        .ListByHostIdAsync(
                            replacement.HostId,
                            timeout.Token)
                        .ConfigureAwait(false);

                Assert.Equal(
                    routeA2.RouteId,
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA2.RuntimeInstanceId))
                        .RouteId);

                Assert.Equal(
                    routeA3.RouteId,
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA3.RuntimeInstanceId))
                        .RouteId);

                var routeA4 =
                    replacementRoutes.Single(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA4.RuntimeInstanceId));

                Assert.NotEqual(
                    routeA2.RouteId,
                    routeA4.RouteId);

                Assert.NotEqual(
                    routeA3.RouteId,
                    routeA4.RouteId);

                var formerA1 =
                    await SendCommandAsync(
                        client,
                        runtimeA1.RuntimeInstanceId,
                        timeout.Token);

                Assert.False(formerA1.Success);

                Assert.Equal(
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .RouteNotFound,
                    formerA1.FailureReason);

                var afterFailureA2 =
                    await SendCommandAsync(
                        client,
                        runtimeA2.RuntimeInstanceId,
                        timeout.Token);

                var afterFailureA3 =
                    await SendCommandAsync(
                        client,
                        runtimeA3.RuntimeInstanceId,
                        timeout.Token);

                var afterFailureA4 =
                    await SendCommandAsync(
                        client,
                        runtimeA4.RuntimeInstanceId,
                        timeout.Token);

                AssertCommandSucceeded(
                    afterFailureA2,
                    "A2 after A1 replacement");

                AssertCommandSucceeded(
                    afterFailureA3,
                    "A3 after A1 replacement");

                AssertCommandSucceeded(
                    afterFailureA4,
                    "A4 replacement");

                Assert.Equal(
                    runtimeA2.RuntimeInstanceId,
                    afterFailureA2.RuntimeInstanceId);

                Assert.Equal(
                    runtimeA3.RuntimeInstanceId,
                    afterFailureA3.RuntimeInstanceId);

                Assert.Equal(
                    runtimeA4.RuntimeInstanceId,
                    afterFailureA4.RuntimeInstanceId);

                Assert.True(
                    launcher.Children.Count >= 4);
            }
            finally
            {
                using var cleanup =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(30));

                await application.StopAsync(
                    cleanup.Token);
            }
        }

        /// <summary>
        /// Creates a real gRPC channel targeting the stable dynamic Kestrel endpoint.
        /// </summary>
        private static GrpcChannel CreateStableRouterChannel(
            IServiceProvider services)
        {
            var server =
                services.GetRequiredService<IServer>();

            var addresses =
                server.Features
                    .Get<IServerAddressesFeature>()
                    ?.Addresses
                ?? throw new InvalidOperationException(
                    "The Kestrel server did not expose any listening address.");

            var stableAddress =
                addresses.Single(
                    address =>
                        address.StartsWith(
                            "http://127.0.0.1:",
                            StringComparison.OrdinalIgnoreCase));

            return GrpcChannel.ForAddress(
                stableAddress);
        }

        /// <summary>
        /// Sends one valid existing command through the stable gRPC service.
        /// </summary>
        private static async Task<AiRuntimeInstanceCommandResult>
            SendCommandAsync(
                AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient client,
                string runtimeInstanceId,
                CancellationToken cancellationToken)
        {
            var command =
                new AiRuntimeInstanceCommandRequest
                {
                    Operation =
                        AiRuntimeInstanceCommandOperation
                            .GetQueueStatus,
                    RuntimeInstanceId =
                        runtimeInstanceId,
                    QueueRequest =
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation =
                                AiRuntimeQueueControlPlaneOperation
                                    .GetQueueStatus,
                            RuntimeInstanceId =
                                runtimeInstanceId,
                            CorrelationId =
                                Guid.NewGuid()
                                    .ToString("N"),
                            RequestedBy =
                                "runtime-pool-grpc-e2e",
                            Source =
                                "runtime-pool-router"
                        }
                };

            var response =
                await client.ExecuteCommandAsync(
                    new AiRuntimeInstanceGrpcCommandRequest
                    {
                        RequestJson =
                            JsonSerializer.Serialize(
                                command,
                                JsonOptions)
                    },
                    cancellationToken:
                        cancellationToken);

            return JsonSerializer.Deserialize<
                    AiRuntimeInstanceCommandResult>(
                    response.ResponseJson,
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "The stable Runtime Pool gRPC service returned an empty command result.");
        }

        /// <summary>
        /// Verifies one real command result and exposes its exact failure payload.
        /// </summary>
        private static void AssertCommandSucceeded(
            AiRuntimeInstanceCommandResult result,
            string phase)
        {
            ArgumentNullException.ThrowIfNull(result);

            Assert.True(
                result.Success,
                string.Concat(
                    "Runtime Pool gRPC command failed during '",
                    phase,
                    "'. FailureReason=",
                    result.FailureReason ?? "<null>",
                    "; Message=",
                    result.Message ?? "<null>",
                    "; RuntimeInstanceId=",
                    result.RuntimeInstanceId));
        }

        /// <summary>
        /// Waits until the initial three children each own one exact ready gRPC route.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForHealthyPoolAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolRouteRegistry routeRegistry,
                IReadOnlyCollection<string>? expectedRuntimeIds,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot =
                    await manager
                        .GetSnapshotAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                var runtimeIds =
                    expectedRuntimeIds ??
                    snapshot.Children
                        .Select(
                            child =>
                                child.RuntimeInstanceId)
                        .ToArray();

                var routes =
                    await routeRegistry
                        .ListByHostIdAsync(
                            snapshot.HostId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    !snapshot.IsBelowMinimumCapacity &&
                    routes.Count == runtimeIds.Count &&
                    runtimeIds.All(
                        runtimeInstanceId =>
                            routes.Any(
                                route =>
                                    StringComparer.Ordinal.Equals(
                                        route.RuntimeInstanceId,
                                        runtimeInstanceId) &&
                                    StringComparer.Ordinal.Equals(
                                        route.PoolId,
                                        snapshot.PoolId) &&
                                    StringComparer.Ordinal.Equals(
                                        route.HostId,
                                        snapshot.HostId) &&
                                    StringComparer.OrdinalIgnoreCase.Equals(
                                        route.TransportName,
                                        "grpc") &&
                                    route.Status ==
                                        AiRuntimePoolRouteStatus.Ready)))
                {
                    return snapshot;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until A1 disappears, A2/A3 remain, and a fresh routed A4 becomes ready.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForReplacementAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolRouteRegistry routeRegistry,
                string failedRuntimeInstanceId,
                string preservedRuntimeA2,
                string preservedRuntimeA3,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot =
                    await manager
                        .GetSnapshotAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                var runtimeA4 =
                    snapshot.Children
                        .SingleOrDefault(
                            child =>
                                child.Ordinal == 4);

                var activeRuntimeIds =
                    snapshot.Children
                        .Select(
                            child =>
                                child.RuntimeInstanceId)
                        .ToArray();

                var routes =
                    await routeRegistry
                        .ListByHostIdAsync(
                            snapshot.HostId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var replacementObserved =
                    snapshot.Status ==
                        AiRuntimeProcessPoolManagerStatus.Running &&
                    snapshot.Children.Count == 3 &&
                    runtimeA4 is not null &&
                    !activeRuntimeIds.Contains(
                        failedRuntimeInstanceId,
                        StringComparer.Ordinal) &&
                    activeRuntimeIds.Contains(
                        preservedRuntimeA2,
                        StringComparer.Ordinal) &&
                    activeRuntimeIds.Contains(
                        preservedRuntimeA3,
                        StringComparer.Ordinal) &&
                    routes.Count == 3 &&
                    !routes.Any(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                failedRuntimeInstanceId)) &&
                    routes.Any(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                preservedRuntimeA2) &&
                            route.Status ==
                                AiRuntimePoolRouteStatus.Ready) &&
                    routes.Any(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                preservedRuntimeA3) &&
                            route.Status ==
                                AiRuntimePoolRouteStatus.Ready) &&
                    routes.Any(
                        route =>
                            StringComparer.Ordinal.Equals(
                                route.RuntimeInstanceId,
                                runtimeA4.RuntimeInstanceId) &&
                            route.Status ==
                                AiRuntimePoolRouteStatus.Ready);

                if (replacementObserved)
                {
                    return snapshot;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
