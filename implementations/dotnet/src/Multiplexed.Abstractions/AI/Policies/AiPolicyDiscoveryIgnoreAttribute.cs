using System;

namespace Multiplexed.AI.Abstractions.AI.Policies
{
    /// <summary>
    /// Excludes a policy implementation from automatic native singleton discovery.
    /// </summary>
    /// <remarks>
    /// Invocation-bound adapters need a contextual factory to supply their immutable
    /// tenant, execution and implementation binding. They are not global policies.
    /// This attribute only affects assembly scanning; it does not disable policy
    /// evaluation, change authorization, or prohibit explicit server registration.
    /// Derived implementations inherit the same discovery boundary.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class AiPolicyDiscoveryIgnoreAttribute : Attribute
    {
    }
}
