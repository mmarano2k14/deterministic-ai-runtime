using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Describes the current server-side custom execution availability for one policy family.
    /// </summary>
    public enum AiCustomPolicyFamilyAvailability
    {
        /// <summary>The family has an installed hosted execution contract.</summary>
        Hosted = 0,

        /// <summary>The family has a frozen custom contract and immutable publication support, but no hosted adapter yet.</summary>
        ContractDefined = 1,

        /// <summary>The family has a runtime checkpoint but remains native-only by design in the current product scope.</summary>
        NativeOnly = 2,

        /// <summary>The policy kind exists as taxonomy only; no independent runtime checkpoint is implemented.</summary>
        NoRuntimeCheckpoint = 3
    }

    /// <summary>
    /// Server-side capability statement for one policy family. This is not a public SDK capability model.
    /// </summary>
    public sealed record AiCustomPolicyFamilyCapability(
        AiPolicyKind Kind,
        AiCustomPolicyFamilyAvailability Availability,
        string? ContractId)
    {
        /// <summary>Gets whether immutable custom code may be attached to this family during publication.</summary>
        public bool SupportsCustomPublication =>
            Availability is AiCustomPolicyFamilyAvailability.Hosted or AiCustomPolicyFamilyAvailability.ContractDefined;

        /// <summary>Gets whether the current runtime may execute this family through a hosted custom adapter.</summary>
        public bool SupportsHostedExecution => Availability == AiCustomPolicyFamilyAvailability.Hosted;
    }

    /// <summary>
    /// Finite policy-family capability matrix for the current runtime implementation.
    /// A declared enum value is never treated as evidence that an execution checkpoint exists.
    /// </summary>
    public static class AiCustomPolicyFamilyCapabilities
    {
        private static readonly IReadOnlyList<AiCustomPolicyFamilyCapability> Values =
        [
            new(AiPolicyKind.Retry, AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyContracts.RetryV1),
            new(AiPolicyKind.Timeout, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null),
            new(AiPolicyKind.CircuitBreaker, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null),
            new(AiPolicyKind.RateLimit, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null),
            new(AiPolicyKind.Validation, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null),
            new(AiPolicyKind.Routing, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null),
            new(AiPolicyKind.Retention, AiCustomPolicyFamilyAvailability.NativeOnly, null),
            new(AiPolicyKind.Concurrency, AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyContracts.ConcurrencyV1),
            new(AiPolicyKind.Delegation, AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyContracts.DelegationV1)
        ];

        /// <summary>Gets one entry for every currently declared <see cref="AiPolicyKind"/> value.</summary>
        public static IReadOnlyList<AiCustomPolicyFamilyCapability> All => Values;

        /// <summary>Gets the capability statement for one known policy family.</summary>
        public static AiCustomPolicyFamilyCapability Get(AiPolicyKind kind)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown AI policy kind.");
            }

            return Values.Single(value => value.Kind == kind);
        }
    }
}
