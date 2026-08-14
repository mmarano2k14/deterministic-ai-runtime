using System;
using System.Linq;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates the runtime-owned Kubernetes Runtime Pool Pod specification.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodSpecBuilderTests
    {
        /// <summary>
        /// Verifies separate stable/readiness endpoints plus one exact port per child runtime.
        /// </summary>
        [Fact]
        public void Build_Should_Expose_StablePoolPort_And_ExactChildPorts()
        {
            var options = CreatePoolOptions("http");
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            options.MaximumPodCount = 7;

            var spec =
                CreateBuilder(options).Build(plan);

            Assert.Equal(7, spec.MaximumPodCount);
            Assert.Equal(5, spec.Ports.Count);
            Assert.Equal("pool-http", spec.Ports[0].Name);
            Assert.Equal(8080, spec.Ports[0].Port);
            Assert.Null(spec.Ports[0].RuntimeInstanceId);
            Assert.Equal("pool-ready", spec.Ports[1].Name);
            Assert.Equal(8081, spec.Ports[1].Port);
            Assert.Null(spec.Ports[1].RuntimeInstanceId);

            Assert.Equal(
                new[] { 18080, 18081, 18082 },
                spec.Ports
                    .Skip(2)
                    .Select(port => port.Port)
                    .ToArray());

            Assert.Equal(
                plan.RuntimeInstances
                    .Select(runtime => runtime.RuntimeInstanceId)
                    .ToArray(),
                spec.Ports
                    .Skip(2)
                    .Select(port => port.RuntimeInstanceId)
                    .ToArray());
        }

        /// <summary>
        /// Verifies that gRPC receives a stable gRPC port name without changing child identity.
        /// </summary>
        [Fact]
        public void Build_Should_Use_GrpcStablePortName_For_GrpcTransport()
        {
            var options = CreatePoolOptions("grpc");
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var spec =
                CreateBuilder(options).Build(plan);

            Assert.Equal("pool-grpc", spec.Ports[0].Name);
            Assert.Equal("grpc", spec.Bootstrap.TransportName);
            Assert.All(
                spec.Bootstrap.RuntimeInstances,
                runtime => Assert.Equal("grpc", runtime.TransportName));
        }

        /// <summary>
        /// Verifies that the bootstrap contract preserves exact planned runtime identities.
        /// </summary>
        [Fact]
        public void Build_Should_Preserve_Exact_RuntimePlans_In_BootstrapContract()
        {
            var options = CreatePoolOptions("http");
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var spec =
                CreateBuilder(options).Build(plan);

            Assert.Equal(plan.PoolId, spec.Bootstrap.PoolId);
            Assert.Equal(plan.PodRequestId, spec.Bootstrap.PodRequestId);
            Assert.Equal(3, spec.Bootstrap.InitialRuntimeInstanceCount);
            Assert.Equal(3, spec.Bootstrap.MinimumRuntimeInstanceCount);
            Assert.Equal(3, spec.Bootstrap.MaximumRuntimeInstanceCount);
            Assert.Equal(1, spec.Bootstrap.StartupParallelism);
            Assert.Equal(8081, spec.Bootstrap.ReadinessPort);
            Assert.Equal(30, spec.Bootstrap.ShutdownTimeoutSeconds);

            Assert.Equal(
                plan.RuntimeInstances
                    .Select(runtime => runtime.RuntimeInstanceId)
                    .ToArray(),
                spec.Bootstrap.RuntimeInstances
                    .Select(runtime => runtime.RuntimeInstanceId)
                    .ToArray());
        }

        /// <summary>
        /// Verifies required pool metadata and additional non-authoritative metadata.
        /// </summary>
        [Fact]
        public void Build_Should_Add_Required_And_Custom_DiagnosticMetadata()
        {
            var options = CreatePoolOptions("http");
            var hostOptions = CreateHostOptions();
            hostOptions.Labels["team"] = "runtime";
            hostOptions.Annotations["description"] = "integration pool";

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var spec =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    options,
                    hostOptions)
                    .Build(plan);

            Assert.Equal(
                "true",
                spec.Labels["multiplexed.ai/runtime-pool"]);
            Assert.Equal(
                "http",
                spec.Labels["multiplexed.ai/transport"]);
            Assert.Equal(
                "runtime",
                spec.Labels["team"]);

            Assert.Equal(
                plan.PoolId,
                spec.Annotations["multiplexed.ai/pool-id"]);
            Assert.Equal(
                plan.PodRequestId,
                spec.Annotations["multiplexed.ai/pod-request-id"]);
            Assert.Equal(
                "integration pool",
                spec.Annotations["description"]);
        }

        /// <summary>
        /// Verifies required metadata cannot be replaced by caller metadata.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Required_Metadata_Override()
        {
            var options = CreatePoolOptions("http");
            var hostOptions = CreateHostOptions();
            hostOptions.Labels["multiplexed.ai/runtime-pool"] = "false";

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var builder =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    options,
                    hostOptions);

            var exception =
                Assert.Throws<InvalidOperationException>(
                    () => builder.Build(plan));

            Assert.Contains(
                "reserved",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies a topology plan from another logical pool cannot be hosted accidentally.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Plan_From_Different_Pool()
        {
            var configuredOptions = CreatePoolOptions("http");
            var foreignOptions = CreatePoolOptions("http");
            foreignOptions.PoolId = "pool-foreign";

            var foreignPlan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    foreignOptions,
                    "request-0001");

            var builder = CreateBuilder(configuredOptions);

            Assert.Throws<InvalidOperationException>(
                () => builder.Build(foreignPlan));
        }

        /// <summary>
        /// Verifies that a stale topology plan cannot silently change the readiness endpoint.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Plan_With_Different_ReadinessPort()
        {
            var options = CreatePoolOptions("grpc");
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001")
                with
                {
                    ReadinessPort = 8082
                };

            var builder = CreateBuilder(options);

            var exception =
                Assert.Throws<InvalidOperationException>(
                    () => builder.Build(plan));

            Assert.Contains(
                "readiness port",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies the existing Kubernetes Pod specification type is not used or modified.
        /// </summary>
        [Fact]
        public void Build_Should_Return_Dedicated_RuntimePool_PodSpec()
        {
            var options = CreatePoolOptions("http");
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var result = CreateBuilder(options).Build(plan);

            Assert.IsType<AiKubernetesRuntimePoolPodSpec>(result);
        }

        /// <summary>
        /// Creates valid fixed-size Runtime Pool topology options.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions CreatePoolOptions(
            string transport)
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                Namespace = "runtime-tests",
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix = "runtime-pool",
                ProviderName = transport,
                TransportName = transport,
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 18080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// Creates valid Kubernetes pool host options.
        /// </summary>
        private static AiKubernetesRuntimePoolHostOptions CreateHostOptions()
        {
            return new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage = "multiplexed-ai-runtime:test",
                ContainerName = "runtime-pool"
            };
        }

        /// <summary>
        /// Creates a Pod specification builder.
        /// </summary>
        private static AiKubernetesRuntimePoolPodSpecBuilder CreateBuilder(
            AiKubernetesRuntimePoolOptions options)
        {
            return new AiKubernetesRuntimePoolPodSpecBuilder(
                options,
                CreateHostOptions());
        }
    }
}
