using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Multiplexed.AI.Tests.Unit.Observability.Events
{
    /// <summary>
    /// Guards migrated production components against reintroducing direct orchestration of observability surfaces.
    /// </summary>
    public sealed class AiCentralizedObservabilityOrchestrationContractTests
    {
        /// <summary>
        /// Verifies that the migrated policy engine emits canonical events instead of calling Ledger or Policy Metrics directly.
        /// </summary>
        [Fact]
        public void PolicyEngine_Should_Not_Directly_Orchestrate_Ledger_Or_PolicyMetrics()
        {
            var repositoryRoot = FindRepositoryRoot();
            var sourcePath = Path.Combine(
                repositoryRoot,
                "src",
                "Multiplexed.AI",
                "Runtime",
                "AI",
                "Policies",
                "AiPolicyEngine.cs");

            var source = File.ReadAllText(sourcePath);

            Assert.False(source.Contains("_obs.Ledger", StringComparison.Ordinal));
            Assert.False(source.Contains("_obs.Metrics.Policy", StringComparison.Ordinal));
            Assert.False(source.Contains(".Metrics.Policy.Record", StringComparison.Ordinal));
            Assert.True(source.Contains("AiEngineEvents.Policy", StringComparison.Ordinal));
            Assert.True(source.Contains("this.observer", StringComparison.Ordinal));
        }


        /// <summary>
        /// Verifies that migrated control-plane producers no longer append Runtime Lifecycle Journal facts directly.
        /// </summary>
        [Fact]
        public void Migrated_ControlPlane_Should_Not_Directly_Append_Runtime_Lifecycle_Journal()
        {
            var repositoryRoot = FindRepositoryRoot();
            var controlPlaneRoot = Path.Combine(
                repositoryRoot,
                "src",
                "Multiplexed.AI",
                "Runtime",
                "ControlPlane");

            var violations = Directory
                .EnumerateFiles(controlPlaneRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(
                    "RuntimeLifecycleJournalAiControlPlaneEventSink.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "AiRuntimeLifecycleObservabilityCompatibility.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "AiRuntimeLifecycleEventWriter.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => File.ReadAllText(path).Contains("AppendOnceAsync(", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(repositoryRoot, path))
                .ToArray();

            Assert.Empty(violations);
        }

        /// <summary>
        /// Verifies that migrated recovery producers no longer invoke the Recovery Forensics recorder directly.
        /// Compatibility constructors may still accept the recorder and adapt it behind the Event Manager.
        /// </summary>
        [Fact]
        public void Migrated_Recovery_Should_Not_Directly_Invoke_Recovery_Forensics_Recorder()
        {
            var repositoryRoot = FindRepositoryRoot();
            var roots = new[]
            {
                Path.Combine(repositoryRoot, "src", "Multiplexed.AI", "Runtime", "ControlPlane"),
                Path.Combine(repositoryRoot, "src", "Multiplexed.AI", "Runtime", "Execution", "Instance", "Worker")
            };

            var violations = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.EndsWith(
                    "RecoveryForensicsAiControlPlaneEventSink.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "AiRecoveryObservabilityCompatibility.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    Source = File.ReadAllText(path)
                })
                .Where(item =>
                    item.Source.Contains("forensicsRecorder.RecordAsync", StringComparison.Ordinal) ||
                    item.Source.Contains("forensicsRecorder.RecordEventAsync", StringComparison.Ordinal) ||
                    item.Source.Contains("this.forensicsRecorder.RecordAsync", StringComparison.Ordinal) ||
                    item.Source.Contains("this.forensicsRecorder.RecordEventAsync", StringComparison.Ordinal) ||
                    item.Source.Contains("_forensicsRecorder.RecordAsync", StringComparison.Ordinal) ||
                    item.Source.Contains("_forensicsRecorder.RecordEventAsync", StringComparison.Ordinal))
                .Select(item => Path.GetRelativePath(repositoryRoot, item.Path))
                .ToArray();

            Assert.Empty(violations);
        }

        /// <summary>
        /// Freezes the small set of direct durable Ledger write boundaries that intentionally remain until
        /// an exact durability-preserving Event Manager migration is proven for the execution engine.
        /// </summary>
        /// <remarks>
        /// This is an explicit 11L exception list, not permission to add new direct Ledger orchestration.
        /// Read-only Ledger query services and Ledger implementation/projection files are excluded.
        /// </remarks>
        [Fact]
        public void Production_Should_Not_Add_New_Direct_Ledger_Write_Boundaries()
        {
            var repositoryRoot = FindRepositoryRoot();
            var productionRoot = Path.Combine(repositoryRoot, "src", "Multiplexed.AI");

            var actual = Directory
                .EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    Path.Combine("Runtime", "Observability", "Ledger"),
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "RuntimeObservabilityAiControlPlaneEventSink.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    Source = File.ReadAllText(path)
                })
                .Where(item =>
                    item.Source.Contains(".Ledger.RecordAsync(", StringComparison.Ordinal) ||
                    item.Source.Contains("ObservabilityService.Ledger", StringComparison.Ordinal))
                .Select(item => Path.GetRelativePath(repositoryRoot, item.Path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var expected = new[]
            {
                "src/Multiplexed.AI/Runtime/Execution/Engine/Helpers/AiDagExecutionHelpers.cs",
                "src/Multiplexed.AI/Runtime/Execution/Instance/Worker/AiRuntimePipelineBackgroundController.cs",
                "src/Multiplexed.AI/Runtime/Execution/Payloads/DefaultAiStepPayloadStore.cs"
            }
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// Freezes the current direct semantic-metrics boundaries that remain coupled to execution-engine
        /// durability or implementation telemetry and therefore are not silently widened during Step 11.
        /// </summary>
        /// <remarks>
        /// Policy metrics are already centralized and are not part of this exception set. These remaining
        /// call sites must be reviewed again when their corresponding execution-engine durable Ledger boundary
        /// is migrated; this guard prevents new direct semantic metric orchestration from appearing meanwhile.
        /// </remarks>
        [Fact]
        public void Production_Should_Not_Add_New_Direct_Semantic_Metric_Boundaries()
        {
            var repositoryRoot = FindRepositoryRoot();
            var productionRoot = Path.Combine(repositoryRoot, "src", "Multiplexed.AI");

            var actual = Directory
                .EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    Path.Combine("Runtime", "Observability", "Metrics"),
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "PolicyMetricsAiControlPlaneEventSink.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "AiPolicyObservabilityCompatibility.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(
                    "ServiceCollectionExtensions;.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    Source = File.ReadAllText(path)
                })
                .Where(item =>
                    item.Source.Contains("ObservabilityService.Metrics", StringComparison.Ordinal) ||
                    item.Source.Contains("_observability.Metrics", StringComparison.Ordinal) ||
                    item.Source.Contains("observability.Metrics", StringComparison.Ordinal) ||
                    item.Source.Contains("_services.Metrics", StringComparison.Ordinal))
                .Select(item => Path.GetRelativePath(repositoryRoot, item.Path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var expected = new[]
            {
                "src/Multiplexed.AI/Runtime/Execution/Engine/Batch/AiDagBatchExecutionRunner.cs",
                "src/Multiplexed.AI/Runtime/Execution/Engine/Creation/AiDagExecutionCreator.cs",
                "src/Multiplexed.AI/Runtime/Execution/Engine/Distributed/AiDagDistributedExecutionRunner.cs",
                "src/Multiplexed.AI/Runtime/Execution/Engine/Local/AiDagLocalExecutionRunner.cs",
                "src/Multiplexed.AI/Runtime/Execution/Engine/Retention/AiDagRetentionCoordinator.cs",
                "src/Multiplexed.AI/Runtime/Execution/Instance/Worker/AiRuntimeInstanceWorker.cs",
                "src/Multiplexed.AI/Runtime/Execution/Instance/Worker/AiRuntimePipelineBackgroundControllerHostedService.cs",
                "src/Multiplexed.AI/Stores/Cache/Redis/Dag/RedisDagStoreClaimService.cs",
                "src/Multiplexed.AI/Stores/Cache/Redis/Dag/RedisDagStoreRecoveryService.cs",
                "src/Multiplexed.AI/Stores/Cache/Redis/Dag/RedisDagStoreTransitionService.cs"
            }
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

            Assert.Equal(expected, actual);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Multiplexed.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                $"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }
    }
}
