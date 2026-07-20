using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the runtime-provider-specific configuration for runtime-host production scenarios.
    /// </summary>
    public interface IProcessHostScenarioRuntimeProfile
    {
        /// <summary>
        /// Gets the runtime host creation mode used by the scenario.
        /// </summary>
        AiRuntimeHostCreationMode HostCreationMode { get; }

        /// <summary>
        /// Gets the provider name.
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Gets the provider label used in output and result metadata.
        /// </summary>
        string ProviderLabel { get; }

        /// <summary>
        /// Gets the log prefix used by scenario output.
        /// </summary>
        string LogPrefix { get; }

        /// <summary>
        /// Gets the requested-by value used by MCP requests.
        /// </summary>
        string RequestedBy { get; }

        /// <summary>
        /// Gets the source value used by MCP requests.
        /// </summary>
        string Source { get; }

        /// <summary>
        /// Builds provider-specific MCP host settings for a runtime-host production scenario.
        /// </summary>
        /// <param name="scenario">The production scenario definition.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path.</param>
        /// <returns>The MCP host settings.</returns>
        Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath);
    }
}