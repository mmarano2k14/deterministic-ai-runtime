using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Rbac.Core.ExecutionContext;
using static Multiplexed.AI.Tests.Runtime.Invocation.McpStepTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Real TRN decisions, per-invocation caches and tenant checks. No replacement RBAC.</summary>
    public sealed class AiMcpStepRbacTests
    {
        [Theory]
        [InlineData("trn:tests:default:reports:publish:invoke", true)]
        [InlineData("trn:tests:default:reports:publish:*", true)]
        [InlineData("trn:tests:default:reports:*:invoke", true)]
        [InlineData("trn:tests:default:reports:*:*", true)]
        [InlineData("trn:tests:default:*:*:invoke", true)]
        [InlineData("trn:tests:default:*:*:*", true)]
        [InlineData("trn:tests:default:reports:publish:read", false)]
        [InlineData("trn:tests:default:shared-run:execution:submit", false)]
        [InlineData("", false)]
        public async Task Existing_Rbac_Engine_Decides_The_Concrete_Tool_Capability(string grant, bool allowed)
        {
            using var fixture = await CreateAsync(grant: grant);
            fixture.Context.StepState.InputPayloads = new()
            {
                ["document"] = AiStoredPayload.Inline("resolved-document")
            };
            if (allowed)
            {
                Assert.True((await fixture.InvokeAsync()).Success);
                Assert.Single(fixture.Transport.Calls);
                Assert.Equal(1, fixture.Payloads.Calls);
            }
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InvokeAsync());
                Assert.Empty(fixture.Transport.Calls);
                Assert.Equal(0, fixture.Payloads.Calls);
            }
            Assert.Single(fixture.Resolver.Calls);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("connection")]
        [InlineData("tool")]
        [InlineData("tool-case")]
        [InlineData("revision")]
        [InlineData("resource")]
        [InlineData("feature")]
        [InlineData("action")]
        public async Task Resolver_Cannot_Substitute_The_Requested_Target_Or_Request_A_Wildcard(string mismatch)
        {
            var resolver = new Resolver { Handler = (request, _) =>
            {
                var target = Target(request);
                target = mismatch switch
                {
                    "tenant" => target with { TenantId = "foreign" },
                    "group" => target with { TenantGroupId = "foreign" },
                    "connection" => target with { ConnectionRef = "foreign" },
                    "tool" => target with { Tool = "delete" },
                    "tool-case" => target with { Tool = "Publish" },
                    "revision" => target with { ConnectionRevision = "https://example.com" },
                    "resource" => target with { Resource = "*" },
                    "feature" => target with { Feature = "publish:admin" },
                    _ => target with { Action = "*" }
                };
                return Task.FromResult<AiMcpToolBinding?>(target);
            } };
            using var fixture = await CreateAsync(resolver: resolver, grant: "trn:tests:default:*:*:*");
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("project")]
        [InlineData("user")]
        [InlineData("namespace")]
        public async Task Live_Context_Must_Match_The_Durable_Identity(string field)
        {
            using var fixture = await CreateAsync();
            switch (field)
            {
                case "tenant": fixture.Live.TenantId = "other"; break;
                case "group": fixture.Live.TenantGroupId = "other"; break;
                case "project": fixture.Live.Project = "other"; break;
                case "user": fixture.Live.UserId = "other"; break;
                case "namespace": fixture.Live.CurrentNamespace = "other"; break;
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Missing_Or_Incomplete_Snapshot_Is_Not_Replaced_By_The_Ambient_Context(bool incomplete)
        {
            using var fixture = await CreateAsync();
            if (incomplete) fixture.Context.Record.ExecutionContextSnapshot!.TenantId = " ";
            else fixture.Context.Record.ExecutionContextSnapshot = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Missing_Live_Context_Is_Not_Restored_By_The_Adapter()
        {
            using var fixture = await CreateAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WithLiveAsync(null,
                () => fixture.Adapter.ExecuteAsync(fixture.Context)));
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Missing_Authorization_Service_Does_Not_Install_An_Allow_Fallback()
        {
            using var fixture = await CreateAsync(registerAuthorization: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Unknown_Connection_Is_Refused()
        {
            var resolver = new Resolver { Handler = (_, _) => Task.FromResult<AiMcpToolBinding?>(null) };
            using var fixture = await CreateAsync(resolver: resolver);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Fresh_Authorization_Scope_Does_Not_Reuse_An_Earlier_Allow()
        {
            using var fixture = await CreateAsync();
            Assert.True((await fixture.InvokeAsync()).Success);
            fixture.Live.Namespaces[0].Trns.Clear();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InvokeAsync());
            Assert.Single(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Config_Cannot_Choose_Tenant_Or_Grant_The_Target_Capability()
        {
            var config = new Dictionary<string, object?>
            {
                ["tenantId"] = "admin", ["allowed"] = true, ["resource"] = "shared-run",
                ["trns"] = new[] { "trn:tests:default:*:*:*" }
            };
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(config: config)), grant: "");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InvokeAsync());
            Assert.Equal("tenant-1", Assert.Single(fixture.Resolver.Calls).TenantId);
            Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shared_Adapter_And_Services_Do_Not_Mix_Concurrent_Tenants(bool secondAllowed)
        {
            var transport = new Transport { Handler = async (request, token) =>
            {
                await Task.Yield(); token.ThrowIfCancellationRequested(); return Response(request);
            } };
            using var fixture = await CreateAsync(transport: transport);
            var second = CreateContext(fixture.Plan, fixture.Provider, "tenant-2", "execution-2", secondAllowed ? Grant : "");
            var secondLive = ExecutionContextSnapshotMapper.ToExecutionContext(second.Record.ExecutionContextSnapshot!);
            var first = fixture.InvokeAsync();
            var other = fixture.WithLiveAsync(secondLive, () => fixture.Adapter.ExecuteAsync(second));
            if (secondAllowed) await Task.WhenAll(first, other);
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other);
                await first;
            }
            Assert.Equal(secondAllowed ? 2 : 1, transport.Calls.Count);
            Assert.All(transport.Calls, request =>
                Assert.Equal(request.Context.TenantId == "tenant-1" ? "execution-1" : "execution-2", request.Context.ExecutionId));
            Assert.Equal(transport.Calls.Count, transport.Calls.Select(x => x.RequestId).Distinct().Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Adapter_Does_Not_Replace_The_Existing_Ambient_Context(bool denied)
        {
            using var fixture = await CreateAsync(grant: denied ? "" : Grant);
            await fixture.WithLiveAsync(fixture.Live, async () =>
            {
                var previous = fixture.Accessor.Current;
                if (denied) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapter.ExecuteAsync(fixture.Context));
                else await fixture.Adapter.ExecuteAsync(fixture.Context);
                Assert.Same(previous, fixture.Accessor.Current);
                return true;
            });
        }
    }
}
