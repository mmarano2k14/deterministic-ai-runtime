using System.Reflection;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Stores;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class AiPublicSdkPublishedDagStoreTests
    {
        [Fact]
        public void Boundary_reads_published_execution_state_from_the_dag_store()
        {
            var field = typeof(AiPublicSdkBoundary).GetField(
                "_dagExecutions",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);
            Assert.Equal(typeof(IAiDagExecutionStore), field!.FieldType);

            var constructor = Assert.Single(typeof(AiPublicSdkBoundary).GetConstructors());
            Assert.Contains(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(IAiDagExecutionStore));
            Assert.DoesNotContain(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(IAiExecutionStore));
        }
    }
}
