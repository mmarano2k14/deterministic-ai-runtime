using Microsoft.Extensions.Options;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Policies;
using Multiplexed.Abstractions.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Multiplexed.Rbac.Core.Runtime
{
    /// <summary>
    /// Resolves, validates, binds, and rotates the execution context for incoming HTTP requests.
    ///
    /// This middleware enforces fail-closed authorization context handling:
    /// - the request must be authenticated
    /// - the access context header must be present
    /// - the context must exist in the store
    /// - the context must match the authenticated principal
    ///
    /// It also manages in-flight locking and end-of-request context rotation.
    /// </summary>
    public sealed class ExecutionContextMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IContextStore _store;
        private readonly IExecutionContextAccessor _accessor;
        private readonly ContextRuntimeOptions _opt;
        private readonly ILogger<ExecutionContextMiddleware> _logger;
        private readonly IRuntimeEventContext _realtimeEvents;

        public ExecutionContextMiddleware(
            RequestDelegate next,
            IContextStore store,
            IExecutionContextAccessor accessor,
            IOptions<ContextRuntimeOptions> options,
            ILogger<ExecutionContextMiddleware> logger,
            IRuntimeEventContext realtimeEvents)
        {
            _next = next;
            _store = store;
            _accessor = accessor;
            _opt = options.Value;
            _logger = logger;
            _realtimeEvents = realtimeEvents;
        }

        public async Task InvokeAsync(HttpContext http)
        {
            // ------------------------------------------------------------
            // 1. Authentication gate
            // ------------------------------------------------------------
            if (http.User?.Identity?.IsAuthenticated != true)
            {
                _realtimeEvents.LogWarning(
                    "Unauthenticated request blocked before execution context resolution.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method
                    });

                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var tokenUserId = http.User.FindFirst("sub")?.Value;

            // ------------------------------------------------------------
            // 2. Extract Access Context handle
            // ------------------------------------------------------------
            var ctxKey = http.Request.Headers[_opt.AccessContextHeader].ToString();

            if (string.IsNullOrWhiteSpace(ctxKey))
            {
                _realtimeEvents.LogWarning(
                    tokenUserId ?? string.Empty,
                    $"Missing {_opt.AccessContextHeader} request header.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        Header = _opt.AccessContextHeader,
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method
                    });

                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // ------------------------------------------------------------
            // 3. Prepare header overwrite (rotation-safe)
            // ------------------------------------------------------------
            http.Response.OnStarting(() =>
            {
                if (!_opt.EnableRotation)
                {
                    return Task.CompletedTask;
                }

                if (http.Response.StatusCode < _opt.RotateWhenStatusCodeBelow)
                {
                    return RotateAndSetHeaderAsync(http, _store, ctxKey, _opt);
                }

                return Task.CompletedTask;
            });

            // ------------------------------------------------------------
            // 4. Resolve effective in-flight policy
            // ------------------------------------------------------------
            var maxInFlight = ResolveMaxInFlight(http);

            // ------------------------------------------------------------
            // 5. Acquire In-Flight (Lua atomic / store atomic)
            // ------------------------------------------------------------
            var acquireResult = await _store.AcquireInFlightAsync(ctxKey, maxInFlight);

            if (acquireResult == InFlightAcquireResult.ContextNotFound)
            {
                _realtimeEvents.LogWarning(
                    tokenUserId ?? string.Empty,
                    "ExecutionContext not found or expired before in-flight acquisition.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        ContextKey = ctxKey,
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method
                    });

                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (acquireResult == InFlightAcquireResult.LimitExceeded)
            {
                if (_opt.LogConcurrencyViolations)
                {
                    _realtimeEvents.LogWarning(
                        tokenUserId ?? string.Empty,
                        "Concurrent in-flight limit exceeded for the current execution context.",
                        "Http.ExecutionContextMiddleware",
                        data: new
                        {
                            ContextKey = ctxKey,
                            MaxInFlight = maxInFlight,
                            OverflowPolicy = _opt.OverflowPolicy.ToString(),
                            Path = http.Request.Path.ToString(),
                            Method = http.Request.Method
                        });
                }

                switch (_opt.OverflowPolicy)
                {
                    case InFlightOverflowPolicy.Reject:
                    default:
                        http.Response.StatusCode = _opt.ConcurrentLimitExceededStatusCode;
                        return;
                }
            }

            try
            {
                // ------------------------------------------------------------
                // 6. Resolve ExecutionContext from store
                // ------------------------------------------------------------
                var ctx = await _store.GetAsync(ctxKey);

                if (ctx is null)
                {
                    _realtimeEvents.LogWarning(
                        tokenUserId ?? string.Empty,
                        "ExecutionContext not found or expired.",
                        "Http.ExecutionContextMiddleware",
                        data: new
                        {
                            ContextKey = ctxKey,
                            Path = http.Request.Path.ToString(),
                            Method = http.Request.Method
                        });

                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                // ------------------------------------------------------------
                // 7. Bind to authenticated principal (anti-replay)
                // ------------------------------------------------------------
                if (!string.IsNullOrWhiteSpace(tokenUserId) &&
                    !string.Equals(ctx.UserId, tokenUserId, StringComparison.Ordinal))
                {


                    _realtimeEvents.LogWarning(
                        ctx.UserId,
                        "ExecutionContext user binding mismatch detected.",
                        "Http.ExecutionContextMiddleware",
                        data: new
                        {
                            ContextUserId = ctx.UserId,
                            TokenUserId = tokenUserId,
                            ContextKey = ctxKey,
                            Path = http.Request.Path.ToString(),
                            Method = http.Request.Method
                        });

                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                // ------------------------------------------------------------
                // 8. Attach to accessor (official boundary)
                // ------------------------------------------------------------
                ctx.ContextKey = ctxKey;
                _accessor.Set(ctx);

                _realtimeEvents.LogInfo(
                    ctx.UserId,
                    "ExecutionContext successfully attached to the current HTTP request.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        ContextKey = ctxKey,
                        ctx.TenantId,
                        ctx.TenantGroupId,
                        ctx.CurrentNamespace,
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method
                    });

                // ------------------------------------------------------------
                // 9. Execute downstream pipeline
                // ------------------------------------------------------------
                await _next(http);
            }
            finally
            {
                // ------------------------------------------------------------
                // 10. Always release In-Flight counter
                // ------------------------------------------------------------
                await _store.ReleaseInFlightAsync(ctxKey);

                _accessor.Clear();

                _realtimeEvents.LogDebug(
                    tokenUserId ?? string.Empty,
                    "ExecutionContext released and cleared after HTTP request completion.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        ContextKey = ctxKey,
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method,
                        StatusCode = http.Response.StatusCode
                    });
            }
        }

        /// <summary>
        /// Resolves the effective maximum number of concurrent in-flight requests
        /// allowed for the current request.
        ///
        /// Resolution order:
        /// 1. default runtime option
        /// 2. optional demo override header (if enabled)
        ///
        /// Invalid or out-of-range client values are ignored safely.
        /// </summary>
        private int ResolveMaxInFlight(HttpContext http)
        {
            var fallback = _opt.MaxInFlightPerContextKey;

            if (!_opt.AllowClientMaxInFlightOverride)
            {
                return fallback;
            }

            if (!http.Request.Headers.TryGetValue(_opt.DemoMaxInFlightHeader, out var raw))
            {
                return fallback;
            }

            if (!int.TryParse(raw.ToString(), out var parsed))
            {
                return fallback;
            }

            // 0 or negative means unlimited demo mode
            if (parsed <= 0)
            {
                return 0;
            }

            // When this explicitly demo/test-only switch is enabled, the client value
            // is a true override of the runtime default. Production hosts should keep
            // AllowClientMaxInFlightOverride disabled.
            return parsed;
        }

        /// <summary>
        /// Resolves the effective overlap window used during rotation.
        ///
        /// Resolution order:
        /// 1. default runtime option
        /// 2. optional demo override header (if enabled)
        ///
        /// The header value is expected in milliseconds.
        /// Invalid or negative values are ignored safely.
        /// </summary>
        private TimeSpan ResolveRotationOverlapWindow(HttpContext http)
        {
            var fallback = _opt.RotationOverlapWindow;

            if (!_opt.AllowClientRotationOverlapOverride)
            {
                return fallback;
            }

            if (!http.Request.Headers.TryGetValue(_opt.RotationOverlapWindowHeader, out var raw))
            {
                return fallback;
            }

            if (!long.TryParse(raw.ToString(), out var parsedMs))
            {
                return fallback;
            }

            if (parsedMs < 0)
            {
                return fallback;
            }

            return TimeSpan.FromMilliseconds(parsedMs);
        }

        /// <summary>
        /// Rotates the current context key at the end of a successful request
        /// and exposes the new key through the configured response header.
        /// </summary>
        private async Task RotateAndSetHeaderAsync(
            HttpContext http,
            IContextStore store,
            string ctxKey,
            ContextRuntimeOptions options)
        {
            var overlapWindow = ResolveRotationOverlapWindow(http);

            var rotated = await store.RotateAsync(ctxKey, overlapWindow);
            var rotatedKey = rotated.newKey;

            if (!string.IsNullOrWhiteSpace(rotatedKey) &&
                !string.Equals(rotatedKey, ctxKey, StringComparison.Ordinal))
            {
                var tokenUserId = http.User.FindFirst("sub")?.Value;

                http.Response.Headers[options.AccessContextHeader] = rotatedKey;

                _realtimeEvents.LogDebug(
                    tokenUserId ?? string.Empty,
                    "ExecutionContext key rotated successfully at the end of the HTTP request.",
                    "Http.ExecutionContextMiddleware",
                    data: new
                    {
                        OldContextKey = ctxKey,
                        NewContextKey = rotatedKey,
                        OverlapWindowMs = overlapWindow.TotalMilliseconds,
                        Path = http.Request.Path.ToString(),
                        Method = http.Request.Method
                    });
            }
        }
    }
}