using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.Runtime.ControlPlane.DI;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic runtime-instance HTTP test host where all startup-bound
    /// settings are supplied explicitly by the test.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Starts an MCP host in <c>RuntimeInstanceOnly</c> mode.
    /// - Applies all test-provided configuration before application startup.
    /// - Registers the HTTP runtime instance provider used by control-plane dispatch tests.
    ///
    /// IMPORTANT:
    /// - This host does not generate configuration values itself.
    /// - The caller must provide a logical control-plane identifier.
    /// - The caller must provide a runtime instance identifier.
    /// - The same control-plane identifier must be shared with the MCP control-plane host
    ///   that will dispatch work to this runtime instance.
    /// </remarks>
    public sealed class GenericRuntimeInstanceHttpTestHost
        : WebApplicationFactory<Program>
    {
        private const string ControlPlaneIdSettingKey =
            "AiEngine:ControlPlane:ControlPlaneId";

        private const string RuntimeInstanceIdSettingKey =
            "AiRuntimeInstanceRegistration:RuntimeInstanceId";

        private const string EngineRuntimeInstanceIdSettingKey =
            "AiEngine:RuntimeInstanceId";

        private const string HostModeSettingKey =
            "AiMcpHost:Mode";

        private const string RuntimeInstanceOnlyMode =
            "RuntimeInstanceOnly";

        private readonly IReadOnlyDictionary<string, string?> settings;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericRuntimeInstanceHttpTestHost"/> class.
        /// </summary>
        /// <param name="settings">Host settings to apply before application startup.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="settings"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required runtime-instance settings are missing or inconsistent.
        /// </exception>
        public GenericRuntimeInstanceHttpTestHost(
            IReadOnlyDictionary<string, string?> settings)
        {
            this.settings =
                settings ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(
                this.settings);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

            foreach (var setting in settings)
            {
                builder.UseSetting(
                    setting.Key,
                    setting.Value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddAiHttpRuntimeInstanceProvider();
            });
        }

        /// <summary>
        /// Validates the required runtime-instance host settings before the test host starts.
        /// </summary>
        /// <param name="settings">The host settings.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when required settings are missing or inconsistent.
        /// </exception>
        private static void ValidateSettings(
            IReadOnlyDictionary<string, string?> settings)
        {
            var mode =
                GetRequiredSetting(
                    settings,
                    HostModeSettingKey);

            if (!string.Equals(
                    mode,
                    RuntimeInstanceOnlyMode,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Generic runtime-instance HTTP test host requires '{HostModeSettingKey}' to be '{RuntimeInstanceOnlyMode}', but found '{mode}'.",
                    nameof(settings));
            }

            _ = GetRequiredSetting(
                settings,
                ControlPlaneIdSettingKey);

            var registrationRuntimeInstanceId =
                GetRequiredSetting(
                    settings,
                    RuntimeInstanceIdSettingKey);

            var engineRuntimeInstanceId =
                GetRequiredSetting(
                    settings,
                    EngineRuntimeInstanceIdSettingKey);

            if (!string.Equals(
                    NormalizeKeySegment(registrationRuntimeInstanceId),
                    NormalizeKeySegment(engineRuntimeInstanceId),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Runtime instance id mismatch. Setting '{RuntimeInstanceIdSettingKey}' is '{registrationRuntimeInstanceId}', " +
                    $"but '{EngineRuntimeInstanceIdSettingKey}' is '{engineRuntimeInstanceId}'.",
                    nameof(settings));
            }
        }

        /// <summary>
        /// Gets a required setting value from a settings dictionary.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        /// <param name="key">The required setting key.</param>
        /// <returns>The required setting value.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the setting is missing or empty.
        /// </exception>
        private static string GetRequiredSetting(
            IReadOnlyDictionary<string, string?> settings,
            string key)
        {
            if (!settings.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Required runtime-instance host setting '{key}' is missing.",
                    nameof(settings));
            }

            return value;
        }

        /// <summary>
        /// Normalizes a value for stable test-host comparisons.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized value.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }
    }
}