using System.Reflection;
using ModelContextProtocol.Server;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.McpServer.Tools;
using Multiplexed.Rbac.Core.Authorization.Attributes;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    /// <summary>Closes the server adapter surface without making engine/control-plane contracts public.</summary>
    public sealed class AiPublicSdkServerBoundaryClosureTests
    {
        private static readonly string[] ForbiddenParameterFragments =
        [
            "tenantId",
            "tenantGroupId",
            "sharedRunId",
            "localRunId",
            "runtimeInstanceId",
            "workerId",
            "claimToken",
            "lease",
            "epoch",
            "controlPlaneId"
        ];

        [Fact]
        public void Boundary_Interface_Uses_Only_Public_Contracts_String_And_CancellationToken()
        {
            var methods = typeof(IAiPublicSdkBoundary).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            Assert.Equal(10, methods.Length);

            foreach (var method in methods)
            {
                AssertPublicTask(method.ReturnType, method.Name);
                foreach (var parameter in method.GetParameters())
                {
                    Assert.True(
                        parameter.ParameterType == typeof(string) ||
                        parameter.ParameterType == typeof(CancellationToken) ||
                        IsPublicContract(parameter.ParameterType),
                        $"Boundary method '{method.Name}' exposes non-public parameter type '{parameter.ParameterType}'.");
                }
            }
        }

        [Fact]
        public void Mcp_Tools_Do_Not_Accept_Server_Owned_Identity_Parameters()
        {
            var methods = ToolMethods();
            Assert.Equal(10, methods.Length);

            foreach (var parameter in methods.SelectMany(method => method.GetParameters()))
            {
                if (parameter.ParameterType == typeof(CancellationToken)) continue;
                var name = parameter.Name ?? string.Empty;
                Assert.DoesNotContain(
                    ForbiddenParameterFragments,
                    fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void Execution_Mcp_Tools_Keep_Their_Existing_Capability_Gates()
        {
            AssertCapability(nameof(PublicSdkMcpTools.SubmitExecutionAsync), "shared-run", "execution", "submit");
            AssertCapability(nameof(PublicSdkMcpTools.ObserveExecutionAsync), "execution", "control", "read");
            AssertCapability(nameof(PublicSdkMcpTools.WatchExecutionAsync), "execution", "control", "read");
            AssertCapability(nameof(PublicSdkMcpTools.GetExecutionResultAsync), "execution", "control", "read");
            AssertCapability(nameof(PublicSdkMcpTools.CancelExecutionAsync), "execution", "control", "cancel");
            AssertCapability(nameof(PublicSdkMcpTools.PauseExecutionAsync), "execution", "control", "pause");
            AssertCapability(nameof(PublicSdkMcpTools.ResumeExecutionAsync), "execution", "control", "resume");
            AssertCapability(nameof(PublicSdkMcpTools.SubmitExecutionInputAsync), "execution", "control", "input");
            AssertCapability(nameof(PublicSdkMcpTools.ReplayExecutionAsync), "replay", "execution", "run");
        }

        [Fact]
        public void Mcp_Tool_Return_Types_Are_Public_Contract_Types()
        {
            foreach (var method in ToolMethods())
                AssertPublicTask(method.ReturnType, method.Name);
        }

        private static MethodInfo[] ToolMethods() => typeof(PublicSdkMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        private static void AssertCapability(string methodName, string resource, string feature, string action)
        {
            var method = typeof(PublicSdkMcpTools).GetMethod(methodName)
                ?? throw new InvalidOperationException($"Public SDK MCP method '{methodName}' was not found.");
            var capability = Assert.Single(method.GetCustomAttributes<RequireCapabilityAttribute>(true));
            Assert.Equal(resource, capability.Resource);
            Assert.Equal(feature, capability.Feature);
            Assert.Equal(action, capability.Action);
        }

        private static void AssertPublicTask(Type type, string memberName)
        {
            Assert.True(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>),
                $"Public SDK member '{memberName}' must return Task<T>.");
            var resultType = type.GetGenericArguments()[0];
            Assert.True(IsPublicContract(resultType),
                $"Public SDK member '{memberName}' exposes non-public return type '{resultType}'.");
        }

        private static bool IsPublicContract(Type type) =>
            type.Namespace?.StartsWith("Multiplexed.AI.Sdk.Contracts", StringComparison.Ordinal) == true;
    }
}
