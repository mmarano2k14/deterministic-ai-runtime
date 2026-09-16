using System.Reflection;
using Multiplexed.AI.Sdk.Protocol;

namespace Multiplexed.AI.Sdk.Tests.Boundary
{
    /// <summary>Prevents the external SDK assembly from acquiring runtime or infrastructure assembly dependencies.</summary>
    public sealed class AiSdkExternalDependencyFirewallTests
    {
        [Fact]
        public void Sdk_References_No_Repository_Assembly_Except_Public_Contracts()
        {
            var repositoryReferences = typeof(AiSdkProtocol).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .Where(name => name.StartsWith("Multiplexed.", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var forbiddenReferences = repositoryReferences
                .Where(name => !string.Equals(name, "Multiplexed.AI.Sdk.Contracts", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(forbiddenReferences);
        }
    }
}
