using System.Reflection;
using Multiplexed.AI.Sdk.Contracts.Boundary;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Boundary
{
    /// <summary>Guards the public SDK contract assembly from engine and infrastructure coupling.</summary>
    public sealed class AiSdkContractDependencyFirewallTests
    {
        private static readonly string[] ForbiddenAssemblyPrefixes =
        [
            "Multiplexed.Abstractions",
            "Multiplexed.AI",
            "Multiplexed.Rbac",
            "Multiplexed.Realtime",
            "MongoDB.",
            "StackExchange.",
            "Grpc.",
            "KubernetesClient",
            "k8s",
            "OpenAI",
            "NServiceBus",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions",
            "SharpCompress",
            "Snappier"
        ];

        [Fact]
        public void Public_Contract_Assembly_Has_No_Engine_Or_Infrastructure_References()
        {
            var assembly = typeof(AiSdkContractAssembly).Assembly;
            var referencedAssemblyNames = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            foreach (var referencedAssemblyName in referencedAssemblyNames)
            {
                Assert.DoesNotContain(
                    ForbiddenAssemblyPrefixes,
                    prefix => referencedAssemblyName.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        [Fact]
        public void Exported_Types_Stay_Inside_The_Public_Contract_Namespace()
        {
            var assembly = typeof(AiSdkContractAssembly).Assembly;
            var exportedTypes = assembly.GetExportedTypes();

            Assert.NotEmpty(exportedTypes);
            Assert.All(
                exportedTypes,
                type => Assert.StartsWith("Multiplexed.AI.Sdk.Contracts", type.Namespace ?? string.Empty));
        }
    }
}
