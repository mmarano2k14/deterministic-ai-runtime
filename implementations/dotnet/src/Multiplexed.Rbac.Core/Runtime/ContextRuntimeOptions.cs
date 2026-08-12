using Microsoft.AspNetCore.Http;
using Multiplexed.Rbac.Core.Policies;

namespace Multiplexed.Rbac.Core.Runtime
{
    /// <summary>
    /// Runtime configuration for the Multiplexed RBAC execution pipeline.
    /// 
    /// This class centralizes all runtime behaviors related to:
    /// - access context transport
    /// - session lifecycle
    /// - context key rotation
    /// - concurrency protection (anti-race / anti-replay)
    /// 
    /// These options are consumed primarily by:
    /// - ExecutionContextMiddleware
    /// - ContextStore implementations (InMemory / Redis)
    /// </summary>
    public sealed class ContextRuntimeOptions
    {
        /// <summary>
        /// Header used to transmit the Access Context handle.
        /// 
        /// The client sends this header with each request, and the server
        /// may return a new value if the context key is rotated.
        /// </summary>
        public string AccessContextHeader { get; set; } = "X-Access-Context";

        /// <summary>
        /// Logical session idle timeout.
        /// 
        /// Determines when a session should be considered expired due
        /// to inactivity. This works alongside Redis TTL but represents
        /// the logical lifetime of a session.
        /// </summary>
        public TimeSpan SessionIdleTimeout { get; set; }
            = TimeSpan.FromMinutes(20);

        /// <summary>
        /// Enables automatic context key rotation at the end of a request.
        /// 
        /// Rotation helps mitigate replay risks and limits long-term
        /// reuse of context handles.
        /// </summary>
        public bool EnableRotation { get; set; } = true;

        /// <summary>
        /// Rotate the context key only when the response status code
        /// is below this threshold.
        /// 
        /// Default behavior: rotate only on successful responses (&lt; 400).
        /// </summary>
        public int RotateWhenStatusCodeBelow { get; set; } = 400;

        /// <summary>
        /// Default maximum number of concurrent in-flight requests allowed
        /// for a single access context key.
        /// 
        /// 1  = strict single-flight protection (recommended)
        /// n  = bounded concurrent reuse
        /// <= 0 = unlimited reuse (unsafe, demo/debug only)
        /// </summary>
        public int MaxInFlightPerContextKey { get; set; } = 10;

        /// <summary>
        /// Allows clients to override the MaxInFlightPerContextKey value
        /// using a request header. When enabled, the supplied positive value
        /// replaces the runtime default and may be higher than that default.
        /// 
        /// This feature is intended only for testing or demo environments
        /// and should remain disabled in production.
        /// </summary>
        public bool AllowClientMaxInFlightOverride { get; set; } = false;

        /// <summary>
        /// Header name used by demo clients to request a custom max in-flight value.
        /// 
        /// Example:
        /// X-Demo-Max-InFlight: 5
        /// </summary>
        public string DemoMaxInFlightHeader { get; set; } = "X-Demo-Max-InFlight";

        /// <summary>
        /// Defines the behavior when the in-flight limit is exceeded.
        /// 
        /// Currently only the Reject policy is implemented.
        /// Future policies may allow waiting or queueing strategies.
        /// </summary>
        public InFlightOverflowPolicy OverflowPolicy { get; set; }
            = InFlightOverflowPolicy.Reject;

        /// <summary>
        /// HTTP status code returned when the in-flight limit
        /// for a context key is exceeded.
        /// 
        /// Default: 429 Too Many Requests.
        /// </summary>
        public int ConcurrentLimitExceededStatusCode { get; set; }
            = StatusCodes.Status429TooManyRequests;

        /// <summary>
        /// TTL used for Redis-based in-flight counters.
        /// 
        /// This protects against situations where a process crashes
        /// before releasing the counter. The TTL ensures that
        /// abandoned counters eventually expire.
        /// </summary>
        public TimeSpan InFlightCounterTtl { get; set; }
            = TimeSpan.FromSeconds(30);

        /// <summary>
        /// When enabled, the TTL of the in-flight counter is refreshed
        /// on each successful acquire operation.
        /// 
        /// This prevents expiration during long-running requests.
        /// </summary>
        public bool RefreshInFlightCounterTtlOnAcquire { get; set; } = true;

        /// <summary>
        /// Enables security logging when concurrency violations occur.
        /// 
        /// Useful for detecting replay attempts, misbehaving clients,
        /// or unexpected concurrent usage of the same context key.
        /// </summary>
        public bool LogConcurrencyViolations { get; set; } = true;

        /// <summary>
        /// Use Redis preload script and Sha caching
        /// </summary>
        public bool UseRedisLuaScriptShaCaching { get; set; } = true;

        /// <summary>
        /// Aloow to averlop rotation Window for testign purpose
        /// </summary>
        public bool AllowClientRotationOverlapOverride { get; set; } = true;

        /// <summary>
        /// Header used in demo mode to override the rotation overlap window.
        /// </summary>
        public string RotationOverlapWindowHeader { get; set; } = "X-Demo-Rotation-Overlap-Ms";

        /// <summary>
        /// Default overlap window applied after context rotation.
        /// </summary>
        public TimeSpan RotationOverlapWindow { get; set; } = TimeSpan.FromSeconds(10);
    }
}