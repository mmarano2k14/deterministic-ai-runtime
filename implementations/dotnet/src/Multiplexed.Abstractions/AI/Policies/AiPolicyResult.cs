namespace Multiplexed.AI.Abstractions.AI.Policies
{
    /// <summary>
    /// Represents the result of an AI policy execution.
    /// </summary>
    /// <remarks>
    /// This class provides both non-generic and strongly typed (generic) factory methods
    /// to create policy results. It is designed to be the single entry point for creating
    /// policy outcomes across the runtime.
    /// </remarks>
    public class AiPolicyResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiPolicyResult"/> class.
        /// </summary>
        /// <param name="kind">The policy result kind.</param>
        /// <param name="message">An optional message describing the result.</param>
        protected AiPolicyResult(
            AiPolicyResultKind kind,
            string? message = null)
        {
            Kind = kind;
            Message = message;
        }

        /// <summary>
        /// Gets the policy result kind.
        /// </summary>
        public AiPolicyResultKind Kind { get; }

        /// <summary>
        /// Gets an optional message describing the policy result.
        /// </summary>
        public string? Message { get; }

        /// <summary>
        /// Gets a value indicating whether the policy execution was successful.
        /// </summary>
        /// <remarks>
        /// A policy is considered successful when its <see cref="Kind"/> is
        /// <see cref="AiPolicyResultKind.Success"/>.
        /// </remarks>
        public bool IsSuccess => Kind == AiPolicyResultKind.Success;

        // =========================
        // NON GENERIC FACTORIES
        // =========================

        /// <summary>
        /// Creates a successful policy result.
        /// </summary>
        /// <param name="message">An optional message describing the result.</param>
        /// <returns>A success policy result.</returns>
        public static AiPolicyResult Success(string? message = null)
        {
            return new AiPolicyResult(AiPolicyResultKind.Success, message);
        }

        /// <summary>
        /// Creates a policy result indicating that execution should be blocked.
        /// </summary>
        /// <param name="message">An optional message describing why execution was blocked.</param>
        /// <returns>A blocking policy result.</returns>
        public static AiPolicyResult Block(string? message = null)
        {
            return new AiPolicyResult(AiPolicyResultKind.Block, message);
        }

        /// <summary>
        /// Creates a policy result indicating that a retry may be performed.
        /// </summary>
        /// <param name="message">An optional message describing the retry recommendation.</param>
        /// <returns>A retry policy result.</returns>
        public static AiPolicyResult Retry(string? message = null)
        {
            return new AiPolicyResult(AiPolicyResultKind.Retry, message);
        }

        // =========================
        // GENERIC FACTORIES
        // =========================

        /// <summary>
        /// Creates a strongly typed successful policy result.
        /// </summary>
        /// <typeparam name="T">The type of the data produced by the policy.</typeparam>
        /// <param name="data">The strongly typed data.</param>
        /// <param name="message">An optional message describing the result.</param>
        /// <returns>A strongly typed success policy result.</returns>
        public static AiPolicyResultGeneric<T> Success<T>(
            T data,
            string? message = null)
        {
            return new AiPolicyResultGeneric<T>(AiPolicyResultKind.Success, data, message);
        }

        /// <summary>
        /// Creates a strongly typed policy result indicating that execution should be blocked.
        /// </summary>
        /// <typeparam name="T">The type of the data associated with the policy.</typeparam>
        /// <param name="message">An optional message describing why execution was blocked.</param>
        /// <returns>A strongly typed blocking policy result.</returns>
        public static AiPolicyResultGeneric<T> Block<T>(string? message = null)
        {
            return new AiPolicyResultGeneric<T>(AiPolicyResultKind.Block, default, message);
        }

        /// <summary>
        /// Creates a blocking result while preserving family-specific decision data.
        /// Both arguments are required to preserve the historical Block&lt;T&gt;(message) overload.
        /// </summary>
        public static AiPolicyResultGeneric<T> Block<T>(T data, string? message)
        {
            return new AiPolicyResultGeneric<T>(AiPolicyResultKind.Block, data, message);
        }

        /// <summary>
        /// Creates a strongly typed policy result indicating that a retry may be performed.
        /// </summary>
        /// <typeparam name="T">The type of the retry-related data.</typeparam>
        /// <param name="data">The strongly typed retry data.</param>
        /// <param name="message">An optional message describing the retry recommendation.</param>
        /// <returns>A strongly typed retry policy result.</returns>
        public static AiPolicyResultGeneric<T> Retry<T>(
            T data,
            string? message = null)
        {
            return new AiPolicyResultGeneric<T>(AiPolicyResultKind.Retry, data, message);
        }
    }
}