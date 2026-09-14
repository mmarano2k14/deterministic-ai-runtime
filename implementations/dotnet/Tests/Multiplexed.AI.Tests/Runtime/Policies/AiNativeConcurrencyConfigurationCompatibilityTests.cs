using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.DI;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Concurrency.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Policies
{
    /// <summary>
    /// Verifies that portable policy syntax resolves to real native registrations
    /// and that pipeline-scoped throttle configuration retains its semantics.
    /// </summary>
    public sealed class AiNativeConcurrencyConfigurationCompatibilityTests
    {
        [Theory]
        [InlineData("\"concurrency.throttle\"", false)]
        [InlineData("{\"name\":\"concurrency.throttle\",\"type\":\"scope\",\"config\":{\"scope\":\"provider\",\"target\":\"openai\",\"limit\":5}}", true)]
        [InlineData("{\"name\":\"concurrency.throttle\",\"kind\":\"scope\",\"config\":{\"scope\":\"provider\",\"target\":\"openai\",\"limit\":5}}", true)]
        public void Pipeline_Policies_Resolve_Legacy_And_Structured_Native_Registrations(
            string policyJson,
            bool hasThrottleConfig)
        {
            using var provider = _createPolicyProvider();
            var definition = _resolvePipelineConcurrency(policyJson);
            var configured = Assert.Single(definition.Policies);
            var registry = provider.GetRequiredService<IAiPolicyRegistry>();

            Assert.Equal("concurrency.throttle", configured.Name);
            Assert.IsType<AiThrottleConcurrencyPolicy>(
                registry.Resolve(configured.Name, AiPolicyKind.Concurrency));

            if (hasThrottleConfig)
            {
                // The legacy type alias remains descriptive metadata; the native
                // registry supplies the executable policy's authoritative family.
                Assert.Equal("scope", configured.Kind);
                _assertProviderThrottle(definition);
            }
            else
            {
                Assert.Null(configured.Kind);
                Assert.Empty(configured.Config);
                Assert.Empty(definition.ThrottleRules);
            }
        }

        [Fact]
        public void Mixed_Legacy_And_Structured_Policies_Preserve_Order_And_Provider_Limit()
        {
            using var provider = _createPolicyProvider();
            var definition = _resolvePipelineConcurrency(
                """
                "concurrency.provider.admission",
                {
                  "name": "concurrency.throttle",
                  "type": "scope",
                  "config": {
                    "scope": "provider",
                    "target": "openai",
                    "limit": 5
                  }
                }
                """);
            var registry = provider.GetRequiredService<IAiPolicyRegistry>();
            var resolved = registry.ResolveMany(
                definition.Policies.Select(policy => policy.Name),
                AiPolicyKind.Concurrency);

            Assert.Equal(
                new[] { "concurrency.provider.admission", "concurrency.throttle" },
                definition.Policies.Select(policy => policy.Name).ToArray());
            Assert.Equal(2, resolved.Count);
            Assert.IsType<AiProviderAdmissionConcurrencyPolicy>(resolved[0]);
            Assert.IsType<AiThrottleConcurrencyPolicy>(resolved[1]);
            _assertProviderThrottle(definition);
        }

        [Fact]
        public void Unknown_Pipeline_Policy_Remains_A_Registry_Error()
        {
            using var provider = _createPolicyProvider();
            var definition = _resolvePipelineConcurrency(
                """
                {
                  "name": "concurrency.scope.default",
                  "type": "scope",
                  "config": {
                    "kind": "provider",
                    "value": "openai",
                    "limit": 5
                  }
                }
                """);
            var configured = Assert.Single(definition.Policies);
            var registry = provider.GetRequiredService<IAiPolicyRegistry>();

            // Syntax compatibility does not install a native implementation or
            // reinterpret an unknown name as a default allow policy.
            Assert.Equal("concurrency.scope.default", configured.Name);
            Assert.False(registry.Exists(configured.Name, AiPolicyKind.Concurrency));
            Assert.Empty(definition.ThrottleRules);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                registry.ResolveMany(
                    definition.Policies.Select(policy => policy.Name),
                    AiPolicyKind.Concurrency));
            Assert.Equal(
                "No AI policy is registered with key 'concurrency.scope.default'.",
                exception.Message);
        }

        /// <summary>
        /// Uses the production scanner and registry without registering test policies.
        /// </summary>
        private static ServiceProvider _createPolicyProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAiPolicyRegistry, DefaultAiPolicyRegistry>();
            services.AddAiPoliciesFromAssemblies(typeof(AiRuntimeAssemblyMarker).Assembly);

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        }

        /// <summary>
        /// Resolves pipeline-only configuration with no local concurrency override.
        /// </summary>
        private static AiConcurrencyDefinition _resolvePipelineConcurrency(string policiesJson)
        {
            var json = $$"""
                {
                  "name": "native-policy-compatibility",
                  "version": "1",
                  "executionMode": "Dag",
                  "config": {
                    "concurrency": {
                      "enabled": true,
                      "maxDegreeOfParallelism": 4,
                      "policies": [ {{policiesJson}} ]
                    }
                  },
                  "steps": [
                    { "name": "hello", "stepKey": "hello-world", "order": 1 }
                  ]
                }
                """;
            var pipeline = JsonSerializer.Deserialize<AiPipelineDefinition>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(pipeline);
            var step = Assert.Single(pipeline!.Steps);
            Assert.Empty(step.Config);

            var definition = new DefaultAiConcurrencyDefinitionResolver().Resolve(pipeline, step);
            Assert.True(definition.Enabled);
            Assert.Equal(4, definition.MaxDegreeOfParallelism);
            return definition;
        }

        /// <summary>
        /// Requires the configured provider limit without changing other providers.
        /// </summary>
        private static void _assertProviderThrottle(AiConcurrencyDefinition definition)
        {
            var rule = Assert.Single(definition.ThrottleRules);
            Assert.Equal("provider", rule.Scope);
            Assert.Equal("openai", rule.Target);
            Assert.Equal(5, rule.Limit);

            var context = new AiConcurrencyContext
            {
                ExecutionId = "execution-policy-compatibility",
                PipelineKey = "native-policy-compatibility:1",
                StepId = "hello",
                StepKey = "hello-world",
                RuntimeInstanceId = "runtime-policy-compatibility",
                LeaseId = "lease-policy-compatibility",
                Provider = "openai"
            };
            var matching = AiConcurrencyThrottleRuleApplicator.Apply(definition, context);
            Assert.Equal(5, matching.MaxProviderConcurrency);
            Assert.Equal(4, matching.MaxDegreeOfParallelism);

            context.Provider = "another-provider";
            var nonMatching = AiConcurrencyThrottleRuleApplicator.Apply(definition, context);
            Assert.Null(nonMatching.MaxProviderConcurrency);
            Assert.Null(definition.MaxProviderConcurrency);
        }
    }
}
