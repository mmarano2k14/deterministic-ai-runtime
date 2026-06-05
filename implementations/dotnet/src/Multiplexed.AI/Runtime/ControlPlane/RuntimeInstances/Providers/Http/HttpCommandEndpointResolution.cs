using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Represents the result of resolving an HTTP command endpoint.
    /// </summary>
    public sealed class HttpCommandEndpointResolution
    {
        /// <summary>
        /// Gets a value indicating whether endpoint resolution succeeded.
        /// </summary>
        public bool Success { get; private init; }

        /// <summary>
        /// Gets the resolved command endpoint.
        /// </summary>
        public Uri? Endpoint { get; private init; }

        /// <summary>
        /// Gets the failure reason when endpoint resolution failed.
        /// </summary>
        public string? FailureReason { get; private init; }

        /// <summary>
        /// Gets the human-readable failure message when endpoint resolution failed.
        /// </summary>
        public string? Message { get; private init; }

        /// <summary>
        /// Creates a successful endpoint resolution.
        /// </summary>
        /// <param name="endpoint">The resolved endpoint.</param>
        /// <returns>The endpoint resolution.</returns>
        public static HttpCommandEndpointResolution Succeeded(
            Uri endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            return new HttpCommandEndpointResolution
            {
                Success = true,
                Endpoint = endpoint
            };
        }

        /// <summary>
        /// Creates a failed endpoint resolution.
        /// </summary>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The human-readable message.</param>
        /// <returns>The endpoint resolution.</returns>
        public static HttpCommandEndpointResolution Failed(
            string failureReason,
            string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            return new HttpCommandEndpointResolution
            {
                Success = false,
                FailureReason = failureReason,
                Message = message
            };
        }
    }
}
