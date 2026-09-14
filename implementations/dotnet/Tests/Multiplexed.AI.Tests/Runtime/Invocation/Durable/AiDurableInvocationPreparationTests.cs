using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>Logical identity, immutable pinning and bounded JSON compatibility.</summary>
    public sealed class AiDurableInvocationPreparationTests
    {
        [Theory]
        [InlineData("tenant-a", "execution-a", "analyze", 0, "73e35e50fe61cc90644a86f8dada8c158f821f521726e43318ddd27477e8637e")]
        [InlineData("tenant-ไทย", "run-é", "analyse", 7, "7f111647bba658a83f0d6899281a5495c0497a690aa92cc34755b9af3cae9ec2")]
        public void Identity_Encoding_Matches_Independent_Utf8_Test_Vectors(
            string tenant, string execution, string step, int generation, string digest)
        {
            var identity = new AiDurableInvocationIdentity(tenant, execution, step, generation);
            Assert.Equal("invocation-" + digest, AiDurableInvocationKeys.OperationId(identity));
            Assert.Equal("invocation-effect-" + digest, AiDurableInvocationKeys.EffectIdempotencyKey(identity));
        }

        [Fact]
        public async Task Preparation_Is_Normalized_And_Duplicate_Does_Not_Reset_It()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition() with { InputsJson = "{\"b\":[2,1],\"a\":{\"z\":1,\"x\":2}}" };
            var first = await journal.PrepareAsync(definition);
            clock.Advance(TimeSpan.FromHours(1));
            var second = await journal.PrepareAsync(definition with { InputsJson = " { \"a\": {\"x\":2,\"z\":1}, \"b\":[2,1] } " });
            Assert.Equal(first, second);
            Assert.Equal("{\"a\":{\"x\":2,\"z\":1},\"b\":[2,1]}", first.Definition.InputsJson);
            Assert.Equal(AiDurableInvocationStatus.Prepared, first.Status);
            Assert.Null(first.Lease);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("execution")]
        [InlineData("step")]
        [InlineData("generation")]
        [InlineData("case")]
        public void Every_Identity_Component_Is_Significant(string field)
        {
            var original = DurableInvocationTestSupport.Identity;
            var changed = field switch
            {
                "tenant" => original with { TenantId = "tenant-b" },
                "execution" => original with { ExecutionId = "execution-b" },
                "step" => original with { StepName = "other" },
                "generation" => original with { Generation = 1 },
                _ => original with { TenantId = "TENANT-A" }
            };
            Assert.NotEqual(AiDurableInvocationKeys.OperationId(original), AiDurableInvocationKeys.OperationId(changed));
            Assert.NotEqual(AiDurableInvocationKeys.EffectIdempotencyKey(original), AiDurableInvocationKeys.EffectIdempotencyKey(changed));
        }

        [Fact]
        public void Length_Prefixing_Prevents_Ambiguous_Concatenation()
        {
            var first = new AiDurableInvocationIdentity("a|b", "c", "d");
            var second = new AiDurableInvocationIdentity("a", "b|c", "d");
            Assert.NotEqual(AiDurableInvocationKeys.OperationId(first), AiDurableInvocationKeys.OperationId(second));
            Assert.Equal(AiDurableInvocationKeys.OperationId(first), AiDurableInvocationKeys.OperationId(first with { }));
        }

        [Theory]
        [InlineData("inputs")]
        [InlineData("pipeline")]
        [InlineData("version")]
        [InlineData("definition-hash")]
        [InlineData("publication")]
        [InlineData("publication-hash")]
        [InlineData("implementation")]
        [InlineData("implementation-hash")]
        [InlineData("language")]
        [InlineData("environment")]
        [InlineData("environment-hash")]
        [InlineData("group")]
        [InlineData("control-plane")]
        public async Task Same_Logical_Operation_Rejects_Changed_Frozen_Material(string field)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition();
            await journal.PrepareAsync(definition);
            var changed = field switch
            {
                "inputs" => definition with { InputsJson = "{\"amount\":2}" },
                "pipeline" => definition with { Target = definition.Target with { PipelineName = "other" } },
                "version" => definition with { Target = definition.Target with { PipelineVersion = "2" } },
                "definition-hash" => definition with { Target = definition.Target with { DefinitionSha256 = DurableInvocationTestSupport.Digest('e') } },
                "publication" => definition with { Target = definition.Target with { PublicationRef = "publication-2" } },
                "publication-hash" => definition with { Target = definition.Target with { PublicationSha256 = DurableInvocationTestSupport.Digest('e') } },
                "implementation" => definition with { Target = definition.Target with { ImplementationRef = "other" } },
                "implementation-hash" => definition with { Target = definition.Target with { ImplementationSha256 = DurableInvocationTestSupport.Digest('e') } },
                "language" => definition with { Target = definition.Target with { ExecutionLanguage = "typescript" } },
                "environment" => definition with { Target = definition.Target with { EnvironmentRef = "other" } },
                "environment-hash" => definition with { Target = definition.Target with { EnvironmentSha256 = DurableInvocationTestSupport.Digest('e') } },
                "group" => definition with { Scope = definition.Scope with { TenantGroupId = "other" } },
                _ => definition with { Scope = definition.Scope with { ControlPlaneId = "other" } }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(changed));
            Assert.Equal(definition, (await journal.GetAsync(definition.Scope, definition.Identity))!.Definition);
        }

        [Theory]
        [InlineData("")]
        [InlineData("unknown")]
        [InlineData("Python")]
        [InlineData("mcp")]
        public async Task Only_An_Already_Resolved_Custom_Language_Is_Accepted(string language)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition();
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(definition with
                { Target = definition.Target with { ExecutionLanguage = language } }));
        }

        [Theory]
        [InlineData("{\"a\":1,\"a\":2}")]
        [InlineData("{\"x\":{\"a\":1,\"a\":2}}")]
        [InlineData("{\"x\":[{\"a\":1,\"a\":2}]}")]
        [InlineData("[]")]
        [InlineData("null")]
        public async Task Ambiguous_Or_Nonobject_Inputs_Are_Refused(string json)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(
                DurableInvocationTestSupport.Definition() with { InputsJson = json }));
        }

        [Fact]
        public async Task Invalid_Json_Is_Not_Treated_As_Empty_Input()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();

            await Assert.ThrowsAnyAsync<JsonException>(() => journal.PrepareAsync(
                DurableInvocationTestSupport.Definition() with { InputsJson = "{" }));
        }

        [Fact]
        public async Task Inline_Input_Size_Is_Bounded()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(DurableInvocationTestSupport.Definition() with
                { InputsJson = "{\"value\":\"" + new string('x', 262144) + "\"}" }));
        }

        [Fact]
        public async Task Numeric_Spelling_Is_Not_Silently_Normalized()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(DurableInvocationTestSupport.Definition() with
                { InputsJson = "{\"amount\":1.0}" }));
        }

        [Fact]
        public async Task Lost_Preparation_Acknowledgement_Reuses_The_Same_Durable_Record()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            store.ThrowAfterNextWrite = true;
            await Assert.ThrowsAsync<IOException>(() => journal.PrepareAsync(DurableInvocationTestSupport.Definition()));
            var recovered = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            Assert.Equal(0, recovered.Revision);
            Assert.Single(JsonSerializer.Deserialize<AiDurableInvocationRecord[]>(store.Export())!);
        }

        [Fact]
        public async Task Terminal_Preparation_Cannot_Be_Resurrected()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            Assert.Equal(completed, await journal.PrepareAsync(DurableInvocationTestSupport.Definition()));
        }

        [Fact]
        public async Task Identity_And_Trusted_Tenant_Must_Match()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition();
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(definition with
                { Scope = definition.Scope with { TenantId = "other" } }));
        }

        [Fact]
        public void Negative_Generation_Is_Not_A_Retry_Key()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AiDurableInvocationKeys.OperationId(
                DurableInvocationTestSupport.Identity with { Generation = -1 }));
        }

        [Fact]
        public async Task Pinned_Hashes_Are_Required_Not_Just_Mutable_References()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition();
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(definition with
                { Target = definition.Target with { ImplementationSha256 = "latest" } }));
        }
    }
}
