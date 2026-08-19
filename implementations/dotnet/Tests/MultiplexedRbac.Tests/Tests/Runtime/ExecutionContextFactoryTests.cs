using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace MultiplexedRbac.Tests.Runtime
{
    public sealed class ExecutionContextFactoryTests
    {
        [Fact]
        public void CreateCopy_Should_Preserve_Runtime_Counters_And_Ttl()
        {
            var factory = new ExecutionContextFactory();
            var source = CreateContext();

            var copy = factory.CreateCopy(source, "execution-copy");

            Assert.Equal("execution-copy", copy.ContextKey);
            Assert.Equal(source.InFlightCount, copy.InFlightCount);
            Assert.Equal(source.TtlSeconds, copy.TtlSeconds);
            Assert.Equal(source.TenantId, copy.TenantId);
            Assert.Equal(source.TenantGroupId, copy.TenantGroupId);
        }

        [Fact]
        public void CreateSnapshot_Should_Preserve_Runtime_Counters_And_Ttl()
        {
            var factory = new ExecutionContextFactory();
            var source = CreateContext();

            var snapshot = factory.CreateSnapshot(source);

            Assert.Equal(source.ContextKey, snapshot.ContextKey);
            Assert.Equal(source.InFlightCount, snapshot.InFlightCount);
            Assert.Equal(source.TtlSeconds, snapshot.TtlSeconds);
            Assert.Equal(source.TenantId, snapshot.TenantId);
            Assert.Equal(source.TenantGroupId, snapshot.TenantGroupId);
        }

        [Fact]
        public void ExecutionContextSnapshotMapper_Should_Restore_All_Durable_Rbac_Fields()
        {
            var source = CreateContext();
            var snapshot = new ExecutionContextFactory().CreateSnapshot(source);

            var restored = ExecutionContextSnapshotMapper.ToExecutionContext(snapshot);

            Assert.Equal(snapshot.ContextKey, restored.ContextKey);
            Assert.Equal(snapshot.Project, restored.Project);
            Assert.Equal(snapshot.UserId, restored.UserId);
            Assert.Equal(snapshot.TenantId, restored.TenantId);
            Assert.Equal(snapshot.TenantGroupId, restored.TenantGroupId);
            Assert.Equal(snapshot.CurrentNamespace, restored.CurrentNamespace);
            Assert.Equal(snapshot.InFlightCount, restored.InFlightCount);
            Assert.Equal(snapshot.TtlSeconds, restored.TtlSeconds);
            Assert.Equal(snapshot.Namespaces.Count, restored.Namespaces.Count);
            Assert.NotSame(snapshot.Namespaces, restored.Namespaces);
        }

        private static RbacExecutionContext CreateContext()
        {
            return new RbacExecutionContext
            {
                ContextKey = "ctx-runtime-1",
                Project = "test-project",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "runtime",
                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = "runtime",
                        Trns = new HashSet<string> { "trn:test:*" }
                    }
                },
                InFlightCount = 7,
                TtlSeconds = 300
            };
        }
    }
}
