namespace Multiplexed.Abstractions.AI.Observability
{
    /// <summary>
    /// Defines canonical metadata keys used to describe exception diagnostics.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names emitted across execution,
    /// control-plane, persistence, recovery, policy, metrics, and observability components.
    /// </remarks>
    public static class AiExceptionMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the exception type name.
        /// </summary>
        public const string ExceptionType = "exception.type";

        /// <summary>
        /// Gets the metadata key carrying the exception message.
        /// </summary>
        public const string ExceptionMessage = "exception.message";

        /// <summary>
        /// Gets the metadata key carrying the exception stack trace.
        /// </summary>
        public const string ExceptionStackTrace = "exception.stack";
    }
}
