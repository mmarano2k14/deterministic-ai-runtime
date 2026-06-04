using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Execution.Instance;

namespace Multiplexed.AI.Tests.Runtime.Execution.MultiInstance
{
    /// <summary>
    /// Creates isolated AI runtime hosts that expose different runtime instance identities.
    /// </summary>
    internal sealed class AiRuntimeMultiInstanceTestHostFactory
    {
        /// <summary>
        /// Creates a runtime test host with a deterministic runtime instance identity.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier for the host.</param>
        /// <returns>The created runtime test host.</returns>
        public AiRuntimeTestHost CreateHost(
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var services = new ServiceCollection();

            services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
            services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                new TestAiRuntimeInstanceIdentity(runtimeInstanceId));

            var provider = services.BuildServiceProvider(validateScopes: true);

            return new AiRuntimeTestHost(
                runtimeInstanceId,
                provider);
        }
    }
}