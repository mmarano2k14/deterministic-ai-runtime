using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Runtime.DI;
using Multiplexed.Rbac.Core.Runtime.Messaging.NServiceBus;
using Multiplexed.Rbac.Core.Runtime.Messaging.NServiceBus.DI;
using Multiplexed.Realtime.DI;
using Multiplexed.Realtime.Events;
using Multiplexed.Realtime.Resolvers;
using MultiplexedRbac.Sample.Crm.Api.AI.Policies;
using MultiplexedRbac.Sample.Crm.Api.AI.Providers;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;
using MultiplexedRbac.Sample.Crm.Api.AI.Steps;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;
using MultiplexedRbac.Sample.Crm.Api.Auth;
using MultiplexedRbac.Sample.Crm.Services;


// Alias to avoid System.ExecutionContext confusion
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;
using Multiplexed.AI.DI.Engine;

var builder = WebApplication.CreateBuilder(args);

// The Deterministic AI Runtime contains context-bound and optional services
// that are intentionally resolved only inside runtime execution scopes.
// EnterpriseRuntimeDemoHost uses the default ServiceProvider semantics.
// Match that host behavior when embedding the runtime in ASP.NET.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = false;
    options.ValidateScopes = false;
});

builder.WebHost.UseUrls("http://localhost:5000");


// --------------------------------------------------------------------
// 1️⃣ Controllers + Swagger
// --------------------------------------------------------------------

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MultiplexedRbac.Sample.Crm.Api",
        Version = "v1",
        Description = "Part 6 — Transport-agnostic deterministic RBAC demo"
    });

    // X-Access-Context header (core to Part 3 & 6)
    c.AddSecurityDefinition("X-Access-Context", new OpenApiSecurityScheme
    {
        Name = "X-Access-Context",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "ExecutionContext handle resolved by the runtime."
    });

    // DEV-only fake user override
    c.AddSecurityDefinition("X-Demo-UserId", new OpenApiSecurityScheme
    {
        Name = "X-Demo-UserId",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "DEV only: sets authenticated user id."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "X-Access-Context"
                }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "X-Demo-UserId"
                }
            },
            Array.Empty<string>()
        }
    });
});


// --------------------------------------------------------------------
// 2️⃣ Authentication (DEV Fake Auth)
// --------------------------------------------------------------------
// Required because ExecutionContextMiddleware denies unauthenticated users.

builder.Services
    .AddAuthentication(FakeAuthHandler.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(
        FakeAuthHandler.AuthenticationScheme, _ => { });

builder.Services.AddAuthorization();


// --------------------------------------------------------------------
// 3️⃣ Multiplexed RBAC Runtime Registration
// --------------------------------------------------------------------
// Registers:
// - IExecutionContextAccessor
// - IContextStore (Composite)
// - AuthorizationScope
// - TrnAuthorizationEngine
// - Proxy-based dynamic registration (Part 4)
// - HTTP middleware (Part 3)
// - NServiceBus behaviors (Part 6)

builder.Services
    .AddMultiplexedRbacRuntime(
        builder.Configuration,
        options =>
        {
            options.MaxInFlightPerContextKey = 10;
            options.AllowClientMaxInFlightOverride = true;
            options.DemoMaxInFlightHeader = "X-Demo-Max-InFlight";
            options.InFlightCounterTtl = TimeSpan.FromSeconds(30);
            options.LogConcurrencyViolations = true;
            options.UseRedisLuaScriptShaCaching = true;
            options.AllowClientRotationOverlapOverride = true;
            options.RotationOverlapWindowHeader = "X-Demo-Rotation-Overlap-Ms";
            options.RotationOverlapWindow = TimeSpan.FromMilliseconds(10000);
        })
    .AddMultiplexedRbacHttp()
    .AddMultiplexedRbacNServiceBus()
    .AddCrmServices()
    .AddMultiplexedRbacAuthorizedServices(typeof(Program).Assembly);

builder.Services.AddSingleton<MultiplexedRbac.Sample.Crm.Api.Context.DemoSeedState>();


// --------------------------------------------------------------------
// 4️⃣ AI Runtime Analysis — bounded snapshot foundation
// --------------------------------------------------------------------
// This is application/demo behavior.
// It does not modify or replace the Deterministic AI Runtime.
// The next AI provider layer will consume the normalized snapshot produced here.

builder.Services.AddSingleton<
    IRuntimeAnalysisSnapshotBuilder,
    RuntimeAnalysisSnapshotBuilder>();

builder.Services
    .AddOptions<OpenAiRuntimeAnalysisOptions>()
    .Bind(
        builder.Configuration.GetSection(
            OpenAiRuntimeAnalysisOptions.SectionName))
    .PostConfigure(options =>
    {
        var apiKey = Environment.GetEnvironmentVariable(
            OpenAiRuntimeAnalysisOptions.ApiKeyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options.ApiKey = apiKey;
        }
    });

builder.Services.AddSingleton<RuntimeAnalysisResultValidator>();

builder.Services.AddHttpClient<
        IAiRuntimeAnalysisProvider,
        OpenAiRuntimeAnalysisProvider>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(90);
    });


// --------------------------------------------------------------------
// 5️⃣ Deterministic AI Runtime — hosted by this sample API
// --------------------------------------------------------------------
// OpenAI execution does NOT run from the controller.
// The controller submits a one-step DAG to the existing runtime.
// The custom step resolves IAiRuntimeAnalysisProvider inside the DAG.

var aiEngineOptions = new AiEngineOptions
{
    // The controller submits the pipeline definition with the run request.
    // This matches the runtime-defined pipeline pattern used by the
    // Enterprise Runtime demo.
    DefaultPipelineDefinitionSource = "Runtime"
};

// The runtime's durable archived-step index is Mongo-backed even when most
// analysis payloads remain inline. Match the Enterprise Runtime host profile:
// Mongo is the durable source of truth and Redis is the bounded hot cache.
var mongoConnectionString =
    builder.Configuration.GetConnectionString("Mongo")
    ?? "mongodb://localhost:27017";

var mongoDatabaseName =
    builder.Configuration["Mongo:DatabaseName"]
    ?? "deterministic_ai_runtime_demo";

aiEngineOptions.PayloadStore.Enabled = true;
aiEngineOptions.PayloadStore.Provider = "mongo-redis";
aiEngineOptions.PayloadStore.RequireReplaySafePayloads = true;

// The current analysis request/result normally remains inline. Larger payloads
// can be externalized durably without changing the DAG or controller contract.
aiEngineOptions.PayloadStore.MaxInlineSizeBytes = 256 * 1024;

aiEngineOptions.PayloadStore.Mongo.Enabled = true;
aiEngineOptions.PayloadStore.Mongo.ConnectionString =
    mongoConnectionString;
aiEngineOptions.PayloadStore.Mongo.DatabaseName =
    mongoDatabaseName;
aiEngineOptions.PayloadStore.Mongo.CollectionName =
    "ai_runtime_analysis_payloads";

aiEngineOptions.PayloadStore.RedisCache.Enabled = true;
aiEngineOptions.PayloadStore.RedisCache.KeyPrefix =
    "ai-demo:runtime-analysis:payload";
aiEngineOptions.PayloadStore.RedisCache.ExpirationSeconds = 3600;
aiEngineOptions.PayloadStore.RedisCache.MaxCacheablePayloadBytes =
    256 * 1024;

aiEngineOptions.PayloadStore.StepIndexCache.Enabled = true;
aiEngineOptions.PayloadStore.StepIndexCache.KeyPrefix =
    "ai-demo:runtime-analysis:step-index";
aiEngineOptions.PayloadStore.StepIndexCache.ExpirationSeconds = 3600;
aiEngineOptions.PayloadStore.StepIndexCache.RefreshTtlOnRead = true;

// Mongo observability is still disabled for this first one-step analysis DAG.
// Canonical lifecycle events / realtime remain available independently.
aiEngineOptions.Snapshots.Enabled = false;
aiEngineOptions.Observability.EnableTracing = false;
aiEngineOptions.Observability.EnableInMemoryRecording = false;
aiEngineOptions.Observability.EnableMetrics = false;

builder.Services.AddMemoryCache();

builder.Services.AddMultiplexAI(
    aiEngineOptions);

// AiDagExecutionEngineServices requires the replay metadata service even when
// this first demo execution does not actively invoke replay.
builder.Services.AddAiExecutionReplay();

// AiDagLocalExecutionRunner requires the runtime signal publisher and the
// logical control-plane id resolver. Use the runtime's existing Redis-backed
// signal implementation and discovery core rather than application stubs.
builder.Services.AddAiRuntimeSignals();
builder.Services.AddAiControlPlaneDiscoveryCore();

builder.Services.AddAiStepsFromAssemblies(
    typeof(AnalyzeRuntimeWithAiStep).Assembly);

// Application-level governance policies.
//
// AddMultiplexAI already owns the policy registry and built-in policy engine
// infrastructure. We only contribute custom IAiPolicy implementations from
// this sample assembly; the runtime core remains unchanged.
builder.Services.AddAiPoliciesFromAssemblies(
    typeof(RuntimeAnalysisScenarioLimitsPolicy).Assembly);

builder.Services.AddSingleton<RuntimeAnalysisScenarioPolicyDefinitionFactory>();
builder.Services.AddSingleton<RuntimeAnalysisPipelineDefinitionFactory>();
builder.Services.AddScoped<RuntimeAnalysisExecutionResultReader>();
builder.Services.AddSingleton<
    IRuntimeAnalysisHumanApprovalStore,
    RedisRuntimeAnalysisHumanApprovalStore>();
builder.Services.AddSingleton(
    new RuntimeAnalysisRuntimeOptions());

builder.Services.AddScoped<RuntimeAnalysisExecutionContextSnapshotFactory>();
builder.Services.AddScoped<
    IRuntimeAnalysisRuntimeExecutor,
    RuntimeAnalysisRuntimeExecutor>();
builder.Services.AddScoped<
    IRuntimeAnalysisHumanApprovalService,
    RuntimeAnalysisHumanApprovalService>();

builder.Services.AddHostedService<RuntimeAnalysisRuntimeHostedService>();

// IMPORTANT: one realtime channel / one worker.
//
// The original RBAC sample called AddMultiplexRealtime() without assemblies,
// which implicitly scanned typeof(IRuntimeEvent).Assembly and therefore
// registered the SignalR dispatch handler for RuntimeLogEvent.
//
// The AI runtime adds its own [RealtimeEvent] event types in Multiplexed.AI.
// Scan BOTH event assemblies in this single call. Do not call
// AddMultiplexRealtime() twice: every call adds another RuntimeEventWorker.
builder.Services
    .AddMultiplexRealtime(
        configureChannel: null,
        typeof(IRuntimeEvent).Assembly,
        typeof(AiRuntimeAssemblyMarker).Assembly)
    .AddSignalRRealtimeTransport(options =>
    {
        options.CorsPolicy = "SignalRCors";
        options.AllowedOrigins =
        [
            "http://localhost:3000"
        ];
        options.UseUserIdentifier<QueryStringRealtimeUserIdentifierResolver>();
    });


// --------------------------------------------------------------------
// 6️⃣ Cookies ticket for protection
// --------------------------------------------------------------------

builder.Services.AddDataProtection();
builder.Services.AddSingleton<IDemoBootstrapTicketProtector, DemoBootstrapTicketProtector>();


// --------------------------------------------------------------------
// 7️⃣ NServiceBus Endpoint (API acts as publisher)
// --------------------------------------------------------------------

builder.Host.UseNServiceBus(_ =>
{
    var endpointConfig = new EndpointConfiguration("MultiplexedRbac.Sample.Crm.Api");

    endpointConfig.EnableInstallers();
    endpointConfig.UseSerialization<SystemJsonSerializer>();

    var transport = endpointConfig.UseTransport<RabbitMQTransport>();
    transport.ConnectionString("host=localhost");
    transport.UseConventionalRoutingTopology(QueueType.Classic);

    // IMPORTANT:
    // Propagate X-Access-Context into outgoing message headers.
    // This proves transport-agnostic authorization.
    endpointConfig.Pipeline.Register(
        typeof(OutgoingExecutionContextHeaderBehavior),
        "Propagate X-Access-Context into outgoing NServiceBus headers.");

    return endpointConfig;
});

var app = builder.Build();


// --------------------------------------------------------------------
// 8️⃣ DEV Seed — deterministic test context
// --------------------------------------------------------------------
// This simulates a login phase (Part 1).
// In production, context would be created at authentication time.

using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IContextStore>();
    var demo = scope.ServiceProvider.GetRequiredService<MultiplexedRbac.Sample.Crm.Api.Context.DemoSeedState>();

    //var ctx = MultiplexedRbac.Sample.Crm.Api.Context.ContextFactory.Full("demo-user-1");
    //var key = await store.StoreAsync(ctx);

    //demo.AccessContextKey = key;
    //Console.WriteLine($"[SEED] Demo ContextKey: {key}");
}

// --------------------------------------------------------------------
// 9️⃣ HTTP Pipeline Ordering (CRITICAL)
// --------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDeveloperExceptionPage();

}

//app.UseHttpsRedirection();

// 1️⃣ Authentication first
app.UseAuthentication();



app.UseWhen(ctx =>
{
    var p = ctx.Request.Path;
    return !p.StartsWithSegments("/demo")
        && !p.StartsWithSegments("/swagger")
        && !p.StartsWithSegments("/runtime")
        && !p.StartsWithSegments("/openapi");
},
branch =>
{
    // 2️⃣ Resolve + bind ExecutionContext
    branch.UseMiddleware<ExecutionContextMiddleware>();
    // 3️⃣ Enforce namespace isolation boundary
    branch.UseMiddleware<NamespaceGuardMiddleware>();
});


// 4️⃣ ASP.NET authorization layer
app.UseRouting();

app.UseCors("SignalRCors");

app.UseAuthorization();

app.MapControllers();
app.MapMultiplexRealtime("/runtime/live")
   .RequireCors("SignalRCors");

app.MapMethods("/cors-test", new[] { "OPTIONS", "GET" }, () => Results.Ok("ok"))
   .RequireCors("SignalRCors");

app.Run();
