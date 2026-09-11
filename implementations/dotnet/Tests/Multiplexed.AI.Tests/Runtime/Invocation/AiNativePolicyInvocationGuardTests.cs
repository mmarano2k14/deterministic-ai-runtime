using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>
    /// Tests the native-only boundary. These are not remote policy evaluation tests.
    /// </summary>
    public sealed class AiNativePolicyInvocationGuardTests
    {
        [Fact]
        public void Legacy_Policy_Names_Preserve_Order_And_Empty_Name_Behavior()
        {
            var policies = new[]
            {
                new AiConfiguredPolicyDefinition { Name = "first", Kind = "scope" },
                new AiConfiguredPolicyDefinition { Name = " " },
                new AiConfiguredPolicyDefinition { Name = "second" }
            };
            Assert.Equal(new[] { "first", "second" }, policies.GetPolicyNames());
        }

        [Fact]
        public void Null_Policy_Collection_Remains_Empty()
        {
            IEnumerable<AiConfiguredPolicyDefinition>? policies = null;
            Assert.Empty(policies.GetPolicyNames());
        }

        [Fact]
        public void Custom_Policy_Cannot_Collapse_To_An_Existing_Native_Name()
        {
            var policy = Custom("concurrency.scope.default");
            Assert.Throws<NotSupportedException>(() => new[] { policy }.GetPolicyNames());
        }

        [Fact]
        public void Custom_With_Empty_Name_Cannot_Be_Silently_Filtered_Out()
        {
            Assert.Throws<NotSupportedException>(() => new[] { Custom("") }.GetPolicyNames());
        }

        [Fact]
        public void Custom_Checkpoint_Error_Is_Not_A_Business_Deny_Or_Empty_Result()
        {
            var exception = Assert.Throws<NotSupportedException>(() => AiInvocationBindingResolver.EnsureNativePolicy(Custom("guard")));
            Assert.Contains("native fallback is forbidden", exception.Message);
        }

        [Fact]
        public void Native_Policy_With_Local_Language_Is_Invalid()
        {
            var policy = new AiConfiguredPolicyDefinition { Name = "native", ExecutionLanguage = "python" };
            Assert.Throws<InvalidOperationException>(() => new[] { policy }.GetPolicyNames());
        }

        [Fact]
        public void Explicit_Native_Policy_Remains_Supported()
        {
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "native", Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Native }
            };
            Assert.Equal(new[] { "native" }, new[] { policy }.GetPolicyNames());
        }

        [Fact]
        public void Mcp_Is_Rejected_As_A_Policy_Invocation()
        {
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "guard", Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Mcp, ConnectionRef = "reports", Tool = "publish"
                }
            };
            Assert.Throws<InvalidOperationException>(() => new[] { policy }.GetPolicyNames());
        }

        private static AiConfiguredPolicyDefinition Custom(string name) => new()
        {
            Name = name, ExecutionLanguage = "python",
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "publication/guard/v1" }
        };
    }
}
