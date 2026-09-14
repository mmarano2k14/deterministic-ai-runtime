using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Observability.Context;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Real correlation access during exact creation, without replacing RBAC identity or DAG logic.</summary>
    public sealed class AiPublicationCorrelationTests
    {
        [Fact]
        public void Fixture_Uses_Existing_Correlation_Accessor_With_Runtime_Identity()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var engine = fixture.EngineServices;
            var correlation = Assert.IsType<AsyncLocalAiRuntimeCorrelationAccessor>(engine.ObservabilityService.Correlation);
            var current = Assert.IsType<AiRuntimeExecutionCorrelationContext>(correlation.Current);

            Assert.Equal(engine.RuntimeInstanceIdentity.RuntimeInstanceId, current.RuntimeInstanceId);
            Assert.Equal(engine.RuntimeInstanceIdentity.RuntimeInstanceId, current.CorrelationId);
            Assert.Equal("runtime-host", current.WorkerId);
        }

        [Fact]
        public async Task Exact_Dag_Creation_With_Runtime_Fallback_Preserves_The_Pin_And_Owner()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var correlation = fixture.EngineServices.ObservabilityService.Correlation;
            var previous = Assert.IsType<AiRuntimeExecutionCorrelationContext>(correlation.Current);
            var run = await fixture.CreateAsync(publication);

            Assert.Equal(AiExecutionStatus.Pending, run.Status);
            Assert.Equal(publication.Manifest.Definition.Sha256, run.PipelineDefinitionSnapshot!.ContentHash);
            Assert.Equal(publication.PublicationRef, (await fixture.TargetAsync(run))!.PublicationRef);
            Assert.Same(previous, correlation.Current);
            Assert.Equal("runtime-host", previous.WorkerId);
            var owner = Assert.IsType<ExecutionContextSnapshot>(run.ExecutionContextSnapshot);
            Assert.Equal(fixture.Live.UserId, owner.UserId);
            Assert.Equal(fixture.Live.TenantId, owner.TenantId);
            Assert.Equal(fixture.Live.TenantGroupId, owner.TenantGroupId);
            var state = Assert.IsType<AiExecutionState>(await fixture.Store.GetStateAsync(run.ExecutionId));
            Assert.Equal(2, state.Steps.Count);
            Assert.All(state.Steps.Values, step => Assert.NotNull(step.Retry));
        }

        [Fact]
        public async Task Exact_Dag_Creation_Preserves_An_Ambient_Correlation_Scope()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var correlation = fixture.EngineServices.ObservabilityService.Correlation;
            var previous = correlation.Current;
            var ambient = new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = "publication-request",
                RuntimeInstanceId = "runtime-a",
                WorkerId = "publication-worker"
            };

            using (correlation.Push(ambient))
            {
                await Task.Yield();
                var run = await fixture.CreateAsync(publication);
                Assert.Same(ambient, correlation.Current);
                Assert.Equal("publication-worker", correlation.Current!.WorkerId);
                Assert.Equal(AiExecutionStatus.Pending, run.Status);
                Assert.Equal(fixture.Live.UserId, run.ExecutionContextSnapshot!.UserId);
            }

            Assert.Same(previous, correlation.Current);
        }

        [Fact]
        public async Task Failed_Creation_Does_Not_Replace_The_Ambient_Correlation_Scope()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var correlation = fixture.EngineServices.ObservabilityService.Correlation;
            var previous = correlation.Current;
            var ambient = new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = "interrupted-publication-request",
                RuntimeInstanceId = "runtime-a",
                WorkerId = "publication-worker"
            };
            fixture.FailContextSeed = true;

            using (correlation.Push(ambient))
            {
                await Assert.ThrowsAsync<IOException>(() => fixture.CreateAsync(publication));
                Assert.Same(ambient, correlation.Current);
            }

            Assert.Same(previous, correlation.Current);
            var run = await fixture.CreateAsync(publication);
            Assert.Equal(AiExecutionStatus.Pending, run.Status);
            Assert.Equal(publication.PublicationRef, (await fixture.TargetAsync(run))!.PublicationRef);
        }

        [Fact]
        public async Task Runtime_Correlation_Does_Not_Replace_Missing_Rbac_Context()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            Assert.NotNull(fixture.EngineServices.ObservabilityService.Correlation.Current);
            var previous = fixture.Accessor.Current;
            var reads = fixture.MemoryPayloads.Reads;
            var writes = fixture.MemoryPayloads.Writes.Count;

            fixture.Accessor.Clear();
            try
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Runs.CreateAsync(
                    PublicationTestSupport.Scope, "missing-owner", publication.PublicationRef, "{}"));
                Assert.Equal(reads, fixture.MemoryPayloads.Reads);
                Assert.Equal(writes, fixture.MemoryPayloads.Writes.Count);
                Assert.Equal(0, fixture.ContextSeeds);
            }
            finally
            {
                if (previous is null) fixture.Accessor.Clear();
                else fixture.Accessor.Set(previous);
            }
        }
    }
}
