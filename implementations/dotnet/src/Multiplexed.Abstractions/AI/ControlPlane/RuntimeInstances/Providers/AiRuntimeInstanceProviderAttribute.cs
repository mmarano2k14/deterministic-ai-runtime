namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Declares the provider name used to route runtime instance operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime instance providers are discovered through this attribute and registered
    /// in the provider router. The provider name should be stable, lowercase, and
    /// transport-oriented, for example:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>local</c></description></item>
    /// <item><description><c>redis-command-queue</c></description></item>
    /// <item><description><c>http</c></description></item>
    /// <item><description><c>grpc</c></description></item>
    /// <item><description><c>kubernetes</c></description></item>
    /// </list>
    /// </remarks>
    [AttributeUsage(
        AttributeTargets.Class,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class AiRuntimeInstanceProviderAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceProviderAttribute"/> class.
        /// </summary>
        /// <param name="providerName">The provider name.</param>
        public AiRuntimeInstanceProviderAttribute(
            string providerName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

            ProviderName = providerName.Trim();
        }

        /// <summary>
        /// Gets the provider name.
        /// </summary>
        public string ProviderName { get; }
    }
}