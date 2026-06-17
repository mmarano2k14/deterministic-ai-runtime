using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides deterministic execution-context snapshots for direct runtime integration tests.
    /// </summary>
    /// <remarks>
    /// Direct runtime tests do not pass through MCP, RBAC headers, shared runs, or Redis shared queue.
    /// Therefore they must explicitly attach an ExecutionContextSnapshot to AiRuntimePipelineRunRequest.
    /// </remarks>
    public static class AiRuntimeExecutionContextSnapshotTestFixture
    {
        public const string TenantId = "test-tenant";
        public const string TenantGroupId = "test-tenant-group";
        public const string Project = "deterministic-ai-runtime-tests";
        public const string UserId = "runtime-integration-test";
        public const string CurrentNamespace = "default";
        public const int TtlSeconds = 300;

        /// <summary>
        /// Creates a stable test execution context snapshot.
        /// </summary>
        public static ExecutionContextSnapshot CreateSnapshot(
            string pipelineName,
            string? source = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

            return new ExecutionContextSnapshot
            {
                ContextKey = $"test-context-{Guid.NewGuid():N}",
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                Project = Project,
                UserId = UserId,
                CurrentNamespace = CurrentNamespace,
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = CurrentNamespace,
                        Trns = new HashSet<string>
                        {
                            $"trn:{Project}:runtime:pipeline:run",
                            $"trn:{Project}:runtime:execution:create",
                            $"trn:{Project}:runtime:execution:read",
                            $"trn:{Project}:replay:execution:run",
                            $"trn:{Project}:replay:audit:run",
                            $"trn:{Project}:replay:report:read",
                            $"trn:{Project}:observability:ledger:read",
                            $"trn:{Project}:observability:trace:read"
                        }
                    }
                },
                InFlightCount = 0,
                TtlSeconds = TtlSeconds
            };
        }

        /// <summary>
        /// Creates a runtime pipeline request with a valid execution context snapshot.
        /// </summary>
        public static AiRuntimePipelineRunRequest CreateRunRequest(
            string pipelineName,
            AiPipelineDefinition? pipelineDefinition = null,
            object? input = null,
            string? source = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = pipelineName,
                PipelineDefinition = pipelineDefinition,
                ExecutionContextSnapshot = CreateSnapshot(
                    pipelineName,
                    source),
                Input = input
            };
        }

        /// <summary>
        /// Copies an existing runtime pipeline request and attaches a test execution context snapshot.
        /// </summary>
        public static AiRuntimePipelineRunRequest AttachSnapshot(
            AiRuntimePipelineRunRequest request,
            string? source = null)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = request.PipelineName,
                PipelineJson = request.PipelineJson,
                PipelineJsonFilePath = request.PipelineJsonFilePath,
                PipelineDefinition = request.PipelineDefinition,
                ExecutionContextSnapshot =
                    request.ExecutionContextSnapshot ??
                    CreateSnapshot(
                        request.PipelineName,
                        source),
                Input = request.Input
            };
        }
    }
}