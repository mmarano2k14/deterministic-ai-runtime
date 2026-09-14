using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Whole-run binding, immutable admission, real creator, and resumed unstarted call sites.</summary>
    public sealed class AiPublicationRunPinningTests
    {
        [Fact]
        public async Task All_Code_Is_Pinned_Before_Any_Dag_Creation_Or_Invocation()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            fixture.BeforeDefinitionResolution = () => Assert.Single(fixture.MemoryPayloads.Documents.Keys.Where(k => k.Contains("/run/", StringComparison.Ordinal)));
            var run = await fixture.CreateAsync(publication);
            Assert.Equal(AiExecutionStatus.Pending, run.Status);
            Assert.False(run.PipelineDefinitionSnapshot!.IsInline);
            Assert.Equal(publication.Manifest.Definition.Sha256, run.PipelineDefinitionSnapshot.ContentHash);
            Assert.Equal(0, fixture.LatestLookups);
            Assert.Empty(await fixture.JournalStore.ListDispatchCandidatesAsync(PublicationTestSupport.Scope, "python", fixture.Clock.GetUtcNow(), 100));
            Assert.Empty(await fixture.JournalStore.ListDispatchCandidatesAsync(PublicationTestSupport.Scope, "typescript", fixture.Clock.GetUtcNow(), 100));
        }

        [Fact]
        public async Task Same_Run_Key_And_Canonical_Inputs_Do_Not_Reseed_Execution()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var first = await fixture.CreateAsync(publication, inputs: "{\"b\":2,\"a\":1}");
            var seeds = fixture.ContextSeeds;
            var second = await fixture.CreateAsync(publication, inputs: "{ \"a\": 1, \"b\": 2 }");
            Assert.Equal(first.ExecutionId, second.ExecutionId); Assert.Equal(seeds, fixture.ContextSeeds);
            Assert.Single(fixture.MemoryPayloads.Documents.Keys.Where(k => k.Contains("/run/", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData("publication")]
        [InlineData("inputs")]
        [InlineData("owner")]
        public async Task Conflicting_Admission_Cannot_Overwrite_An_Existing_Run(string conflict)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var original = await fixture.PublishAsync();
            var replacement = await fixture.PublishAsync(PublicationTestSupport.Upload("2"));
            var run = await fixture.CreateAsync(original); var seeds = fixture.ContextSeeds;
            Func<Task> admission = () => fixture.AsAsync(() => fixture.Runs.CreateAsync(PublicationTestSupport.Scope,
                "run-a", conflict == "publication" ? replacement.PublicationRef : original.PublicationRef,
                conflict == "inputs" ? "{\"amount\":2}" : "{\"amount\":1}"),
                conflict == "owner" ? PublicationTestSupport.Identity(user: "other-user") : fixture.Live);
            if (conflict == "owner") await Assert.ThrowsAsync<UnauthorizedAccessException>(admission);
            else await Assert.ThrowsAsync<InvalidOperationException>(admission);
            Assert.Equal(seeds, fixture.ContextSeeds);
            Assert.Equal(original.Manifest.Definition.Sha256, (await fixture.Store.GetRecordAsync(run.ExecutionId))!.PipelineDefinitionSnapshot!.ContentHash);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Concurrent_Run_Admissions_Have_One_Authoritative_Publication(bool conflictingPublication)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var first = await fixture.PublishAsync();
            var second = conflictingPublication ? await fixture.PublishAsync(PublicationTestSupport.Upload("2")) : first;
            fixture.MemoryPayloads.BeforeWrite = async (key, _) =>
            {
                if (key.Contains("/run/", StringComparison.Ordinal)) await Task.Yield();
            };
            async Task<AiExecutionRecord?> Admit(AiPipelinePublication publication)
            {
                try { return await fixture.CreateAsync(publication); }
                catch (InvalidOperationException) when (conflictingPublication) { return null; }
            }
            var results = await Task.WhenAll(Admit(first), Admit(second));
            Assert.Equal(conflictingPublication ? 1 : 2, results.Count(r => r is not null));
            var id = Assert.Single(results.Where(r => r is not null).Select(r => r!.ExecutionId).Distinct());
            var pin = JsonSerializer.Deserialize<AiPublicationRunPin>(fixture.MemoryPayloads.Documents.Single(p => p.Key.Contains("/run/", StringComparison.Ordinal)).Value)!;
            Assert.Equal(pin.DefinitionSha256, (await fixture.Store.GetRecordAsync(id))!.PipelineDefinitionSnapshot!.ContentHash);
            Assert.Equal(pin.PublicationRef, (await fixture.TargetAsync((await fixture.Store.GetRecordAsync(id))!))!.PublicationRef);
        }

        [Fact]
        public async Task A_New_Run_Key_Uses_The_Explicit_New_Publication()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var first = await fixture.PublishAsync();
            var second = await fixture.PublishAsync(PublicationTestSupport.Upload("2"));
            var run1 = await fixture.CreateAsync(first); var run2 = await fixture.CreateAsync(second, "run-b");
            Assert.NotEqual(run1.ExecutionId, run2.ExecutionId);
            Assert.Equal(first.PublicationRef, (await fixture.TargetAsync(run1))!.PublicationRef);
            Assert.Equal(second.PublicationRef, (await fixture.TargetAsync(run2))!.PublicationRef);
        }

        [Fact]
        public async Task Interruption_After_Pin_Before_Dag_Creation_Preserves_The_Original_Publication()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var original = await fixture.PublishAsync();
            fixture.FailContextSeed = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.CreateAsync(original));
            var pin = JsonSerializer.Deserialize<AiPublicationRunPin>(fixture.MemoryPayloads.Documents.Single(p => p.Key.Contains("/run/", StringComparison.Ordinal)).Value)!;
            Assert.Null(await fixture.Store.GetRecordAsync(pin.ExecutionId));
            var replacement = await fixture.PublishAsync(PublicationTestSupport.Upload("2"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateAsync(replacement));
            var run = await fixture.CreateAsync(original);
            Assert.Equal(pin.ExecutionId, run.ExecutionId);
            Assert.Equal(original.PublicationRef, (await fixture.TargetAsync(run))!.PublicationRef);
        }

        [Fact]
        public async Task Lost_Pin_Acknowledgement_Is_Retryable_Without_Rebinding()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            fixture.MemoryPayloads.AfterWrite = (key, _) => key.Contains("/run/", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Injected lost pin acknowledgement.")) : Task.CompletedTask;
            await Assert.ThrowsAsync<IOException>(() => fixture.CreateAsync(publication));
            Assert.Equal(0, fixture.ContextSeeds); fixture.MemoryPayloads.AfterWrite = null;
            var run = await fixture.CreateAsync(publication);
            Assert.Equal(publication.PublicationRef, (await fixture.TargetAsync(run))!.PublicationRef);
        }

        [Fact]
        public async Task Missing_Run_Pin_Cannot_Be_Reconstructed_From_Latest()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var key = fixture.MemoryPayloads.Documents.Keys.Single(k => k.Contains("/run/", StringComparison.Ordinal));
            fixture.MemoryPayloads.Documents.TryRemove(key, out _);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.TargetAsync(run));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Readmission_Cannot_Recreate_A_Deleted_Run_Pin(bool useDifferentPublication)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var original = await fixture.PublishAsync();
            var replacement = await fixture.PublishAsync(PublicationTestSupport.Upload("2"));
            var run = await fixture.CreateAsync(original); var seeds = fixture.ContextSeeds;
            var key = fixture.MemoryPayloads.Documents.Keys.Single(k => k.Contains("/run/", StringComparison.Ordinal));
            fixture.MemoryPayloads.Documents.TryRemove(key, out _);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateAsync(useDifferentPublication ? replacement : original));
            Assert.False(fixture.MemoryPayloads.Documents.ContainsKey(key));
            Assert.Equal(seeds, fixture.ContextSeeds);
            Assert.Equal(original.Manifest.Definition.Sha256, (await fixture.Store.GetRecordAsync(run.ExecutionId))!.PipelineDefinitionSnapshot!.ContentHash);
        }

        [Theory]
        [InlineData("definition")]
        [InlineData("step-key")]
        [InlineData("implementation")]
        [InlineData("language")]
        [InlineData("pipeline-version")]
        public async Task Target_Lookup_Rejects_Metadata_Different_From_The_Pin(string field)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await fixture.AsAsync(async () =>
            {
                var request = await fixture.Services.GetRequiredService<AiDurableInvocationDagBinding>().ReadAsync(run, PublicationTestSupport.Scope, "second");
                request = field switch
                {
                    "definition" => request with { DefinitionSha256 = new string('f', 64) },
                    "step-key" => request with { StepKey = "wrong-key" },
                    "implementation" => request with { ImplementationRef = "impl-" + new string('e', 64) },
                    "language" => request with { ExecutionLanguage = "dotnet" },
                    _ => request with { PipelineVersion = "999" }
                };
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Targets.ResolveAsync(request)); return true;
            });
        }

        [Fact]
        public async Task Real_Dag_Rehydration_Keeps_Unstarted_Code_After_Republication()
        {
            using var first = new PublicationTestSupport.Fixture(); var original = await first.PublishAsync();
            var run = await first.CreateAsync(original);
            Assert.Equal(AiExecutionStatus.Waiting, (await first.RunNextAsync(run.ExecutionId)).Status);
            await first.CompleteAsync(run.ExecutionId, "first");
            await first.RunNextAsync(run.ExecutionId);
            Assert.Equal(AiStepExecutionStatus.Completed, (await first.Store.GetStateAsync(run.ExecutionId))!.Steps["first"].Status);
            Assert.Null(await first.Journal.GetAsync(PublicationTestSupport.Scope, new AiDurableInvocationIdentity("tenant-a", run.ExecutionId, "second")));
            var replacement = await first.PublishAsync(PublicationTestSupport.Upload("2"));
            var recordJson = JsonSerializer.Serialize(await first.Store.GetRecordAsync(run.ExecutionId));
            var stateJson = JsonSerializer.Serialize(await first.Store.GetStateAsync(run.ExecutionId));
            using var restored = new PublicationTestSupport.Fixture();
            restored.MemoryPayloads.Restore(first.MemoryPayloads.Export()); restored.JournalStore.Restore(first.JournalStore.Export());
            await restored.Store.CreateAsync(JsonSerializer.Deserialize<AiExecutionRecord>(recordJson)!, JsonSerializer.Deserialize<AiExecutionState>(stateJson)!);
            Assert.Equal(AiExecutionStatus.Waiting, (await restored.RunNextAsync(run.ExecutionId)).Status);
            var invocation = (await restored.Journal.GetAsync(PublicationTestSupport.Scope,
                new AiDurableInvocationIdentity("tenant-a", run.ExecutionId, "second")))!;
            var originalCode = original.Manifest.Functions.Single(f => f.Site.StepName == "second");
            Assert.Equal(original.PublicationRef, invocation.Definition.Target.PublicationRef);
            Assert.Equal(originalCode.Implementation.Sha256, invocation.Definition.Target.ImplementationSha256);
            Assert.Equal(originalCode.Environment.Sha256, invocation.Definition.Target.EnvironmentSha256);
            Assert.NotEqual(replacement.PublicationRef, invocation.Definition.Target.PublicationRef);
            Assert.Equal("typescript", invocation.Definition.Target.ExecutionLanguage);
            Assert.Equal(0, restored.LatestLookups);
            await restored.CompleteAsync(run.ExecutionId, "second");
            Assert.Equal(AiExecutionStatus.Completed, (await restored.RunNextAsync(run.ExecutionId)).Status);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("{\"a\":1,\"a\":2}")]
        public async Task Ambiguous_Or_Nonobject_Run_Inputs_Are_Rejected(string inputs)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateAsync(publication, inputs: inputs));
            Assert.DoesNotContain(fixture.MemoryPayloads.Documents.Keys, k => k.Contains("/run/", StringComparison.Ordinal));
        }
    }
}
