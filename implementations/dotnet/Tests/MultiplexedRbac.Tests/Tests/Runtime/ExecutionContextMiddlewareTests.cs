using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace MultiplexedRbac.Tests.Runtime
{
    public sealed class ExecutionContextMiddlewareTests
    {
        [Fact]
        public async Task InvokeAsync_ReturnsForbidden_WhenContextExpiredBeforeAcquire()
        {
            var store = new StubContextStore
            {
                AcquireResult = InFlightAcquireResult.ContextNotFound
            };

            var nextCalled = false;
            var middleware = CreateMiddleware(
                store,
                new ContextRuntimeOptions
                {
                    EnableRotation = false
                },
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                });

            var http = CreateAuthenticatedContext("ctx-expired");

            await middleware.InvokeAsync(http);

            Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
            Assert.False(nextCalled);
            Assert.Equal(0, store.ReleaseCount);
        }

        [Fact]
        public async Task InvokeAsync_ReturnsConfiguredConcurrencyStatus_WhenLimitExceeded()
        {
            var store = new StubContextStore
            {
                AcquireResult = InFlightAcquireResult.LimitExceeded
            };

            var middleware = CreateMiddleware(
                store,
                new ContextRuntimeOptions
                {
                    EnableRotation = false,
                    ConcurrentLimitExceededStatusCode = StatusCodes.Status429TooManyRequests
                });

            var http = CreateAuthenticatedContext("ctx-live");

            await middleware.InvokeAsync(http);

            Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
            Assert.Equal(0, store.ReleaseCount);
        }

        [Fact]
        public async Task AcquireInFlightAsync_DefaultInterfaceMethod_RemainsCompatibleWithLegacyStores()
        {
            var liveContext = CreateExecutionContext("ctx-live");
            IContextStore liveStore = new LegacyContextStore(liveContext);
            IContextStore missingStore = new LegacyContextStore(context: null);

            Assert.Equal(
                InFlightAcquireResult.LimitExceeded,
                await liveStore.AcquireInFlightAsync("ctx-live", maxInFlight: 1));

            Assert.Equal(
                InFlightAcquireResult.ContextNotFound,
                await missingStore.AcquireInFlightAsync("ctx-missing", maxInFlight: 1));
        }

        [Fact]
        public async Task InvokeAsync_UsesClientMaxInFlightAboveDefault_WhenDemoOverrideEnabled()
        {
            var store = new StubContextStore
            {
                AcquireResult = InFlightAcquireResult.LimitExceeded
            };

            var options = new ContextRuntimeOptions
            {
                EnableRotation = false,
                MaxInFlightPerContextKey = 10,
                AllowClientMaxInFlightOverride = true,
                DemoMaxInFlightHeader = "X-Demo-Max-InFlight"
            };

            var middleware = CreateMiddleware(store, options);
            var http = CreateAuthenticatedContext("ctx-live");
            http.Request.Headers[options.DemoMaxInFlightHeader] = "100000";

            await middleware.InvokeAsync(http);

            Assert.Equal(100000, store.LastMaxInFlight);
            Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        }

        private static ExecutionContextMiddleware CreateMiddleware(
            IContextStore store,
            ContextRuntimeOptions options,
            RequestDelegate? next = null)
        {
            return new ExecutionContextMiddleware(
                next ?? (_ => Task.CompletedTask),
                store,
                new ExecutionContextAccessor(),
                Options.Create(options),
                NullLogger<ExecutionContextMiddleware>.Instance,
                new NullRuntimeEventContext());
        }

        private static Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext CreateExecutionContext(
            string contextKey)
            => new()
            {
                ContextKey = contextKey,
                Project = "test",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "test",
                Namespaces = new List<NamespaceEntry>()
            };

        private static DefaultHttpContext CreateAuthenticatedContext(string contextKey)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers["X-Access-Context"] = contextKey;
            http.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim("sub", "user-1") },
                    authenticationType: "test"));

            return http;
        }

        private sealed class StubContextStore : IContextStore
        {
            public InFlightAcquireResult AcquireResult { get; init; }

            public int LastMaxInFlight { get; private set; }

            public int ReleaseCount { get; private set; }

            public Task<string> StoreAsync(
                Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)
                => throw new NotSupportedException();

            public Task<string> SeedAsync(
                Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)
                => throw new NotSupportedException();

            public Task<Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext?> GetAsync(string key)
                => Task.FromResult<Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext?>(null);

            public Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight)
                => Task.FromResult(AcquireResult == InFlightAcquireResult.Acquired);

            public Task<InFlightAcquireResult> AcquireInFlightAsync(
                string key,
                int maxInFlight)
            {
                LastMaxInFlight = maxInFlight;
                return Task.FromResult(AcquireResult);
            }

            public Task ReleaseInFlightAsync(string key)
            {
                ReleaseCount++;
                return Task.CompletedTask;
            }

            public Task<(string newKey, Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)> RotateAsync(
                string key,
                TimeSpan overlapWindow)
                => throw new NotSupportedException();
        }

        private sealed class LegacyContextStore : IContextStore
        {
            private readonly Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext? _context;

            public LegacyContextStore(
                Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext? context)
            {
                _context = context;
            }

            public Task<string> StoreAsync(
                Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)
                => throw new NotSupportedException();

            public Task<string> SeedAsync(
                Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)
                => throw new NotSupportedException();

            public Task<Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext?> GetAsync(string key)
                => Task.FromResult(_context);

            public Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight)
                => Task.FromResult(false);

            public Task ReleaseInFlightAsync(string key)
                => Task.CompletedTask;

            public Task<(string newKey, Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context)> RotateAsync(
                string key,
                TimeSpan overlapWindow)
                => throw new NotSupportedException();
        }

        private sealed class NullRuntimeEventContext : IRuntimeEventContext
        {
            public void LogDebug(string message, string category, object? data = null) { }
            public void LogDebug(string userId, string message, string category, object? data = null) { }
            public void LogInfo(string message, string category, object? data = null) { }
            public void LogInfo(string userId, string message, string category, object? data = null) { }
            public void LogWarning(string message, string category, object? data = null) { }
            public void LogWarning(string userId, string message, string category, object? data = null) { }
            public void LogError(string message, string category, object? data = null) { }
            public void LogUser(string userId, string message, string category, object? data = null) { }
        }
    }
}
