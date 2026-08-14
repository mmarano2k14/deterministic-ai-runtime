// ============================================================================
// Multiplexed.Rbac.Core.Runtime - DI Extensions
// Goal: avoid copy/paste in each microservice.
//
// Usage:
//
// builder.Services
//   .AddMultiplexed.Rbac.CoreRuntime(builder.Configuration)
//   .AddMultiplexed.Rbac.CoreHttp();              // API only
//
// builder.Services
//   .AddMultiplexed.Rbac.CoreRuntime(builder.Configuration)
//   .AddMultiplexed.Rbac.CoreNServiceBus();       // Worker (and API for outgoing behavior)
//
// Then in API pipeline:
// app.UseAuthentication();
// app.UseMiddleware<ExecutionContextMiddleware>();
// app.UseMiddleware<NamespaceGuardMiddleware>();
// ============================================================================

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Registration;
using Multiplexed.Rbac.Core.Authorization.Scope;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;
using StackExchange.Redis;

// NOTE: adjust namespaces/types below to match your real locations:
// - AuthorizationScope
// - IAuthorizationEngine / TrnAuthorizationEngine
// - ExecutionContextAccessor : IExecutionContextAccessor
// - RedisContextStore, MemoryContextStore, CompositeContextStore
// - ExecutionContextMiddleware, NamespaceGuardMiddleware
//
// NServiceBus behaviors live in Multiplexed.Rbac.Core.Runtime.NServiceBus
//   OutgoingExecutionContextHeaderBehavior
//   IncomingExecutionContextRehydrateBehavior

namespace Multiplexed.Rbac.Core.Runtime.DI
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the shared Multiplexed RBAC runtime used by BOTH HTTP APIs and message endpoints:
        /// - Auth runtime (AuthorizationScope, Engine, Accessor)
        /// - Redis + Memory fallback stores
        /// - CompositeContextStore as IContextStore
        /// - ContextRuntimeOptions
        /// </summary>
        public static IServiceCollection AddMultiplexedRbacRuntime(
            this IServiceCollection services,
            IConfiguration configuration,
            Action<ContextRuntimeOptions>? configureRuntimeOptions = null)
        {
            // ------------------------------------------------------------
            // 1) Authorization runtime (scoped boundary)
            // ------------------------------------------------------------
            services.AddScoped<AuthorizationScope>();
            services.AddScoped<IAuthorizationEngine, TrnAuthorizationEngine>();

            // Keep scoped if your accessor is implemented with scoped storage.
            // (If it is AsyncLocal-based, Singleton is fine too - but don't change it here.)
            //services.AddScoped<IExecutionContextAccessor, ExecutionContextAccessor>();
            services.AddSingleton<IExecutionContextAccessor, ExecutionContextAccessor>();
            services.AddSingleton<IExecutionContextFactory, ExecutionContextFactory>();

            // Proxy + dynamic registration (Part 4)
            // By default: scan calling assembly? No. We keep this explicit per host:
            // hosts can call AddAuthorizedServices(typeof(Program).Assembly) themselves if they want
            // OR you can provide an overload below.
            // services.AddAuthorizedServices(...);

            // ------------------------------------------------------------
            // 2) Runtime options (Part 3)
            // ------------------------------------------------------------
            services.Configure<ContextRuntimeOptions>(opt =>
            {
                // defaults
                opt.SessionIdleTimeout = TimeSpan.FromMinutes(20);
                opt.AccessContextHeader = "X-Access-Context";

                // host override
                configureRuntimeOptions?.Invoke(opt);
            });

            // ------------------------------------------------------------
            // 3) Redis infrastructure
            // ------------------------------------------------------------
            services.AddMemoryCache();

            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisConnectionString =
                    configuration.GetConnectionString("Redis")
                    ?? "localhost:6379";

                var redisConfiguration =
                    ConfigurationOptions.Parse(
                        redisConnectionString);

                redisConfiguration.SyncTimeout = 10_000;
                redisConfiguration.AsyncTimeout = 10_000;

                return ConnectionMultiplexer.Connect(
                    redisConfiguration);
            });

            // ------------------------------------------------------------
            // 4) Stores
            // ------------------------------------------------------------

            services.AddSingleton<RedisContextStore>();

            services.AddSingleton(sp =>
            {
                var mem = sp.GetRequiredService<IMemoryCache>();
                var runtimeOptions = sp
                    .GetRequiredService<IOptions<ContextRuntimeOptions>>()
                    .Value;

                return new MemoryContextStore(
                    mem,
                    ttl: runtimeOptions.SessionIdleTimeout);
            });

            services.AddSingleton<IContextStore>(sp =>
            {
                var primary = sp.GetRequiredService<RedisContextStore>();
                var fallback = sp.GetRequiredService<MemoryContextStore>();
                return new CompositeContextStore(primary, fallback);
            });


            services.Configure<TrnBuilderOptions>(opt =>
            {
                // Option 1: from config
                opt.Project = configuration["Multiplexed.Rbac.Core:Project"] ?? "rbac-demo";

                // Option 2: hardcode for sample
                // opt.Project = "rbac-demo";
            });


            services.AddSingleton<TrnBuilder>();

            return services;
        }

        /// <summary>
        /// Optional helper to keep dynamic proxy registration consistent per host.
        /// Host must pass its assembly (usually typeof(Program).Assembly).
        /// </summary>
        public static IServiceCollection AddMultiplexedRbacAuthorizedServices(
            this IServiceCollection services,
            params System.Reflection.Assembly[] assemblies)
        {
            foreach (var a in assemblies)
                services.AddAuthorizedServices(a);

            return services;
        }

        /// <summary>
        /// Registers HTTP-only runtime components (middlewares).
        /// NOTE: Pipeline ordering is done in app.Use..., not here.
        /// </summary>
        public static IServiceCollection AddMultiplexedRbacHttp(this IServiceCollection services)
        {
            // Middlewares
            //services.AddTransient<ExecutionContextMiddleware>();
            //services.AddTransient<NamespaceGuardMiddleware>();

            return services;
        }
    }
}