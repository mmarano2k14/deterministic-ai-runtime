using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Metrics.Policy;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests the centralized Policy Metrics projection.
    /// </summary>
    public sealed class PolicyMetricsAiControlPlaneEventSinkTests
    {
        /// <summary>
        /// Verifies that a canonical policy decision preserves the existing execution and decision metrics calls.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Project_Policy_Decision_To_Existing_Metrics()
        {
            var metrics = new CapturingPolicyMetrics();
            var sink = new PolicyMetricsAiControlPlaneEventSink(metrics);

            await sink.RecordAsync(
                CreatePolicyEvent(
                    AiEngineEvents.Policy.Denied,
                    new Dictionary<string, object?>
                    {
                        [AiPolicyMetadataKeys.Name] = "RiskPolicy",
                        [AiPolicyMetadataKeys.ResultSuccess] = false.ToString(),
                        [AiPolicyMetadataKeys.ResultKind] = AiPolicyResultKind.Block.ToString(),
                        [AiObservabilityMetadataKeys.DottedDurationMs] = "12.50"
                    }),
                CancellationToken.None).ConfigureAwait(false);

            var execution = Assert.Single(metrics.Executions);
            Assert.Equal("execution-1", execution.ExecutionId);
            Assert.Equal("RiskPolicy", execution.PolicyName);
            Assert.False(execution.Success);
            Assert.Equal(TimeSpan.FromMilliseconds(12.5), execution.Duration);

            var decision = Assert.Single(metrics.Decisions);
            Assert.Equal("execution-1", decision.ExecutionId);
            Assert.Equal("RiskPolicy", decision.PolicyName);
            Assert.Equal(AiPolicyResultKind.Block, decision.Kind);
            Assert.Empty(metrics.Failures);
        }

        /// <summary>
        /// Verifies that an exception-based canonical policy failure preserves the existing failure metric only.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Project_Exception_Policy_Failure_To_Existing_Failure_Metric()
        {
            var metrics = new CapturingPolicyMetrics();
            var sink = new PolicyMetricsAiControlPlaneEventSink(metrics);

            await sink.RecordAsync(
                CreatePolicyEvent(
                    AiEngineEvents.Policy.Failed,
                    new Dictionary<string, object?>
                    {
                        [AiPolicyMetadataKeys.Name] = "ThrowingPolicy",
                        [AiExceptionMetadataKeys.ExceptionType] = nameof(InvalidOperationException)
                    }),
                CancellationToken.None).ConfigureAwait(false);

            var failure = Assert.Single(metrics.Failures);
            Assert.Equal("execution-1", failure.ExecutionId);
            Assert.Equal("ThrowingPolicy", failure.PolicyName);
            Assert.Empty(metrics.Executions);
            Assert.Empty(metrics.Decisions);
        }

        /// <summary>
        /// Verifies that generic legacy control-plane events are ignored by the Policy Metrics projection.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Ignore_Legacy_ControlPlane_Events()
        {
            var metrics = new CapturingPolicyMetrics();
            var sink = new PolicyMetricsAiControlPlaneEventSink(metrics);

            await sink.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationCompleted,
                    Area = AiControlPlaneArea.Policy,
                    Operation = "policy.execute",
                    Correlation = new AiRuntimeExecutionCorrelationContext
                    {
                        ExecutionId = "execution-1",
                        CorrelationId = "execution-1"
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Empty(metrics.Executions);
            Assert.Empty(metrics.Decisions);
            Assert.Empty(metrics.Failures);
        }

        private static AiControlPlaneEvent CreatePolicyEvent(
            string semanticEventType,
            IReadOnlyDictionary<string, object?> properties)
        {
            return new AiControlPlaneEvent
            {
                SemanticEventType = semanticEventType,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Policy,
                Operation = "policy.execute",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    ExecutionId = "execution-1",
                    CorrelationId = "execution-1"
                },
                Properties = properties
            };
        }

        private sealed class CapturingPolicyMetrics : IAiPolicyMetrics
        {
            public List<ExecutionMetric> Executions { get; } = new();

            public List<FailureMetric> Failures { get; } = new();

            public List<DecisionMetric> Decisions { get; } = new();

            public void RecordExecution(
                string executionId,
                string policyName,
                bool success,
                TimeSpan duration)
            {
                Executions.Add(new ExecutionMetric(executionId, policyName, success, duration));
            }

            public void RecordFailure(string executionId, string policyName)
            {
                Failures.Add(new FailureMetric(executionId, policyName));
            }

            public void RecordDecision(
                string executionId,
                string policyName,
                AiPolicyResultKind kind)
            {
                Decisions.Add(new DecisionMetric(executionId, policyName, kind));
            }
        }

        private sealed record ExecutionMetric(
            string ExecutionId,
            string PolicyName,
            bool Success,
            TimeSpan Duration);

        private sealed record FailureMetric(
            string ExecutionId,
            string PolicyName);

        private sealed record DecisionMetric(
            string ExecutionId,
            string PolicyName,
            AiPolicyResultKind Kind);
    }
}
