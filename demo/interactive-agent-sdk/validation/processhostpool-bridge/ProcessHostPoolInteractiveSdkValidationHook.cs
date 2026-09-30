using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Xunit.Abstractions;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Validation.InteractiveSdk;

/// <summary>
/// Environment-gated bridge from the existing production ProcessHostPool harness
/// to the external interactive SDK topology validator.
/// </summary>
internal static class ProcessHostPoolInteractiveSdkValidationHook
{
    private const string EnabledEnvironmentVariable =
        "AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION";

    public static bool IsEnabled =>
        StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
            "1");

    /// <summary>
    /// Applies replay-safe payload storage to the settings shared by the control
    /// plane and ProcessHostPool runtime hosts. This must run before the pool is
    /// started so every external runtime observes the same durable payload authority.
    /// </summary>
    public static void ApplyRuntimeValidationSettings(
        IDictionary<string, string?> settings,
        string tenantId,
        string tenantGroupId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantGroupId);
        if (!IsEnabled)
        {
            return;
        }

        var mongoConnectionString =
            RequiredSetting(
                settings,
                "ConnectionStrings:Mongo",
                "Mongo:ConnectionString");
        var mongoDatabaseName =
            RequiredSetting(settings, "Mongo:DatabaseName");
        var configuredControlPlaneId =
            RequiredSetting(settings, "AiEngine:ControlPlane:ControlPlaneId");

        settings["AiEngine:PayloadStore:Enabled"] = "true";
        settings["AiEngine:PayloadStore:Provider"] = "mongo-redis";
        settings["AiEngine:PayloadStore:RequireReplaySafePayloads"] = "true";
        settings["AiEngine:Payloads:RequireReplaySafePayloads"] = "true";
        settings["AiEngine:PayloadStore:Mongo:Enabled"] = "true";
        settings["AiEngine:PayloadStore:Mongo:ConnectionString"] = mongoConnectionString;
        settings["AiEngine:PayloadStore:Mongo:DatabaseName"] = mongoDatabaseName;
        settings["AiEngine:PayloadStore:RedisCache:Enabled"] = "true";
        settings["AiEngine:PayloadStore:RedisCache:KeyPrefix"] =
            $"ai:payload:interactive-sdk:{configuredControlPlaneId}";
        settings["AiEngine:PayloadStore:RedisCache:ExpirationSeconds"] = "3600";

        // Pin the public control-plane hosted-invocation role to the exact tenant
        // scope used by the production ProcessHostPool scenario. This mirrors the
        // already validated external-SDK topology: the control plane reconciles
        // durable DAG state but never polls worker pages itself.
        settings["AiHostedInvocation:Enabled"] = "true";
        settings["AiHostedInvocation:EnableWorkerPolling"] = "false";
        settings["AiHostedInvocation:EnableDagReconciliation"] = "true";
        settings["AiHostedInvocation:EnableLocalWorkerProfiles"] = "false";
        settings["AiHostedInvocation:TenantId"] = tenantId;
        settings["AiHostedInvocation:TenantGroupId"] = tenantGroupId;
        settings["AiHostedInvocation:ControlPlaneId"] = configuredControlPlaneId;
    }

    /// <summary>
    /// Pins the parent ProcessHost and its RuntimeInstanceOnly children to distinct
    /// hosted-invocation roles. The parent owns only the process pool; child runtime
    /// processes own worker polling for the exact production tenant scope.
    /// </summary>
    public static void ApplyProcessHostRuntimeValidationSettings(
        IDictionary<string, string?> settings,
        string tenantId,
        string tenantGroupId,
        string controlPlaneId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
        if (!IsEnabled)
        {
            return;
        }

        // Parent ProcessHost: topology/process-pool authority only. Explicitly pin
        // this off so ambient launcher/matrix environment cannot start a poller.
        settings["AiHostedInvocation:Enabled"] = "false";

        // The production ProcessHostPool proof deliberately uses a placeholder because
        // its historical workloads do not call a real model. Interactive SDK validation
        // executes real ai.prompt steps, so replace that placeholder on the server-side
        // ProcessHost configuration. ProcessRuntimePoolBootstrapRegistration then composes
        // this value into each RuntimeInstanceOnly child as OpenAI__ApiKey. The external
        // SDK matrix process still has OPENAI_API_KEY removed below.
        settings["OpenAI:ApiKey"] = RequiredEnvironment("OPENAI_API_KEY");

        // RuntimeInstanceOnly children: worker execution authority. These variables
        // are injected by AiRuntimeProcessPoolRuntimeInstance into each child process.
        const string prefix = "AiRuntimeProcessPoolRuntimeInstance:EnvironmentVariables:";
        settings[prefix + "AiHostedInvocation__Enabled"] = "true";
        settings[prefix + "AiHostedInvocation__EnableWorkerPolling"] = "true";
        settings[prefix + "AiHostedInvocation__EnableDagReconciliation"] = "false";
        settings[prefix + "AiHostedInvocation__EnableLocalWorkerProfiles"] = "true";
        settings[prefix + "AiHostedInvocation__TenantId"] = tenantId;
        settings[prefix + "AiHostedInvocation__TenantGroupId"] = tenantGroupId;
        settings[prefix + "AiHostedInvocation__ControlPlaneId"] = controlPlaneId;
    }

    /// <summary>
    /// Applies real JWT authentication only to the public control-plane test host.
    /// ProcessHostPool internal runtime endpoints keep their existing transport auth.
    /// </summary>
    public static void ApplyAuthenticationSettings(
        IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsEnabled)
        {
            return;
        }

        var issuer = RequiredEnvironment("AiMcpAuthentication__Issuer");
        var audience = RequiredEnvironment("AiMcpAuthentication__Audience");
        var signingKey = RequiredEnvironment("AiMcpAuthentication__SymmetricSigningKey");
        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Interactive SDK ProcessHostPool validation requires a JWT signing key of at least 32 UTF-8 bytes.");
        }

        // This host is the public interactive SDK boundary, not a matrix harness host.
        // StandaloneAccessContextEndpoint intentionally does not map its bootstrap
        // route while AiMatrixHarness is enabled, so pin the public control-plane
        // host to the normal authentication/bootstrap path. The ProcessHostPool
        // has already been started from the original settings before this method
        // is called, therefore this does not alter the runtime-host topology.
        settings["AiMatrixHarness:Enabled"] = "false";
        // The public control-plane owns shared-queue pumping/reconciliation, but it
        // must not run the runtime worker polling loop. Runtime workers were already
        // started from the original ProcessHostPool settings before this override.
        settings["AiHostedInvocation:EnableWorkerPolling"] = "false";
        settings["Tests:UseRealJwtAuthentication"] = "true";
        settings["AiMcpAuthentication:Enabled"] = "true";
        settings["AiMcpAuthentication:Issuer"] = issuer;
        settings["AiMcpAuthentication:Audience"] = audience;
        settings["AiMcpAuthentication:SymmetricSigningKey"] = signingKey;
    }

    /// <summary>
    /// Runs the external SDK 3-client matrix against the already-started production
    /// ProcessHostPool and returns true when the historical scenario should stop
    /// after the interactive validation workload.
    /// </summary>
    public static async Task<bool> TryRunAsync(
        object profile,
        object cluster,
        GenericMcpServerTestHost controlPlaneHost,
        IReadOnlyDictionary<string, string?> controlPlaneSettings,
        string poolId,
        string controlPlaneId,
        object tenant,
        int maximumProcessHostCount,
        int runtimeCountPerHost,
        ITestOutputHelper output)
    {
        if (!IsEnabled)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(controlPlaneHost);
        ArgumentNullException.ThrowIfNull(controlPlaneSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
        ArgumentNullException.ThrowIfNull(output);

        var transport = NormalizeTransport(
            Environment.GetEnvironmentVariable("AI_INTERACTIVE_SDK_RUNTIME_TRANSPORT")
            ?? ReadString(profile, "ProviderName")
            ?? ReadString(profile, "TransportName")
            ?? throw new InvalidOperationException(
                "The ProcessHostPool transport could not be resolved from the validation environment or scenario profile."));

        if (maximumProcessHostCount < 2 || runtimeCountPerHost < 2)
        {
            throw new InvalidOperationException(
                $"Interactive ProcessHostPool validation requires at least 2 parent hosts and 2 child runtimes per host. " +
                $"Configured ProcessHostCount='{maximumProcessHostCount}', RuntimeCountPerHost='{runtimeCountPerHost}'.");
        }

        var repositoryRoot = RequiredEnvironment("AI_INTERACTIVE_SDK_REPOSITORY_ROOT");
        var inventoryPath = RequiredEnvironment("AI_INTERACTIVE_SDK_INVENTORY_PATH");
        var evidenceDirectory = RequiredEnvironment("AI_INTERACTIVE_SDK_EVIDENCE_DIR");

        output.WriteLine(
            $"[INTERACTIVE SDK PROCESSHOSTPOOL] Starting bridge. Transport='{transport}', PoolId='{poolId}', " +
            $"ProcessHostCount='{maximumProcessHostCount}', RuntimeCountPerHost='{runtimeCountPerHost}'.");

        await AssertRealJwtAuthenticationSchemeAsync(
                controlPlaneHost.Services,
                output)
            .ConfigureAwait(false);

        await using var proxy =
            await GenericMcpNetworkProxy
                .StartAsync(controlPlaneHost, output, CancellationToken.None)
                .ConfigureAwait(false);

        var inventory =
            await ProcessHostPoolReflectionInventory.BuildAsync(
                    cluster,
                    controlPlaneHost.Services,
                    poolId,
                    transport,
                    proxy.McpEndpoint,
                    maximumProcessHostCount,
                    runtimeCountPerHost)
                .ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(inventoryPath)!);

        var inventoryJson =
            JsonSerializer.Serialize(
                (object)inventory,
                inventory.GetType(),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

        await File.WriteAllTextAsync(
                inventoryPath,
                inventoryJson + Environment.NewLine)
            .ConfigureAwait(false);

        output.WriteLine(
            $"[INTERACTIVE SDK PROCESSHOSTPOOL] Inventory written. Path='{inventoryPath}', " +
            $"HostCount='{inventory.Hosts.Count}', RuntimeCount='{inventory.Hosts.Sum(x => x.RuntimeInstances.Count)}', " +
            $"McpEndpoint='{proxy.McpEndpoint}'.");

        var matrixScript =
            Path.Combine(
                repositoryRoot,
                "demo",
                "interactive-agent-sdk",
                "validation",
                "run_processhostpool_matrix.py");
        if (!File.Exists(matrixScript))
        {
            throw new FileNotFoundException(
                "The ProcessHostPool matrix runner was not found.",
                matrixScript);
        }

        Directory.CreateDirectory(evidenceDirectory);
        var python = Environment.GetEnvironmentVariable("AI_INTERACTIVE_SDK_PYTHON") ?? "python";
        var arguments = new List<string>
        {
            Quote(matrixScript),
            "--runtime-transport", transport,
            "--runtime-endpoint", Quote(proxy.McpEndpoint),
            "--inventory", Quote(inventoryPath),
            "--evidence-dir", Quote(evidenceDirectory),
            "--auth-mode", "local-jwt",
            "--skip-prepare"
        };

        var start = new ProcessStartInfo
        {
            FileName = python,
            Arguments = string.Join(" ", arguments),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        CopyEnvironment(start, "AiMcpAuthentication__Issuer");
        CopyEnvironment(start, "AiMcpAuthentication__Audience");
        CopyEnvironment(start, "AiMcpAuthentication__SymmetricSigningKey");
        CopyEnvironment(start, "AI_DEMO_PROJECT");
        CopyEnvironment(start, "AI_DEMO_NAMESPACE");
        CopyEnvironment(start, "AI_DEMO_USER_ID");
        CopyEnvironment(start, "AI_DEMO_TENANT_ID");
        CopyEnvironment(start, "AI_DEMO_TENANT_GROUP_ID");
        CopyEnvironment(start, "AI_DEMO_TOKEN_TTL_SECONDS");

        // The production ProcessHostPool is tenant-scoped. The external SDK JWT
        // must therefore use the exact tenant identity of the already-running
        // harness instead of the Docker-demo tenant defaults.
        var tenantId = RequiredString(tenant, "TenantId");
        var tenantGroupId = ReadString(tenant, "TenantGroupId");
        start.Environment["AI_DEMO_TENANT_ID"] = tenantId;
        if (string.IsNullOrWhiteSpace(tenantGroupId))
        {
            start.Environment.Remove("AI_DEMO_TENANT_GROUP_ID");
        }
        else
        {
            start.Environment["AI_DEMO_TENANT_GROUP_ID"] = tenantGroupId;
        }

        // Both bootstrap and MCP must cross the same loopback proxy / TestServer
        // boundary. Never allow a stale developer-shell or Docker endpoint to
        // create the RBAC access context against a different host.
        start.Environment["AI_RUNTIME_ENDPOINT"] = proxy.McpEndpoint;
        start.Environment["AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT"] = proxy.AccessContextEndpoint;
        start.Environment.Remove("AI_RUNTIME_ACCESS_CONTEXT");

        // OPENAI_API_KEY remains server/runtime-owned. Never pass it to the
        // external .NET, TypeScript or Python SDK consumer process.
        start.Environment.Remove("OPENAI_API_KEY");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The ProcessHostPool matrix process could not be started.");

        var stdoutTask = PumpAsync(process.StandardOutput, output, "[SDK MATRIX]");
        var stderrTask = PumpAsync(process.StandardError, output, "[SDK MATRIX ERR]");
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Interactive SDK ProcessHostPool matrix failed with exit code '{process.ExitCode}'.");
        }

        output.WriteLine(
            $"[INTERACTIVE SDK PROCESSHOSTPOOL] PASS. Transport='{transport}', SDKClients='3/3'.");
        return true;
    }

    private static async Task AssertRealJwtAuthenticationSchemeAsync(
        IServiceProvider services,
        ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);

        var options =
            services
                .GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<
                        Microsoft.AspNetCore.Authentication.AuthenticationOptions>>()
                .Value;

        const string expectedScheme = "InteractiveSdkJwtBearer";
        if (!StringComparer.Ordinal.Equals(
                options.DefaultAuthenticateScheme,
                expectedScheme) ||
            !StringComparer.Ordinal.Equals(
                options.DefaultChallengeScheme,
                expectedScheme))
        {
            throw new InvalidOperationException(
                "Interactive SDK ProcessHostPool validation did not resolve the dedicated JWT authentication scheme. " +
                $"Expected='{expectedScheme}', " +
                $"DefaultScheme='{options.DefaultScheme ?? "(null)"}', " +
                $"DefaultAuthenticateScheme='{options.DefaultAuthenticateScheme ?? "(null)"}', " +
                $"DefaultChallengeScheme='{options.DefaultChallengeScheme ?? "(null)"}', " +
                $"DefaultForbidScheme='{options.DefaultForbidScheme ?? "(null)"}'.");
        }

        var schemeProvider =
            services.GetRequiredService<
                Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
        var scheme =
            await schemeProvider
                .GetSchemeAsync(expectedScheme)
                .ConfigureAwait(false);
        var handlerType = scheme?.HandlerType;
        const string expectedHandlerType =
            "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";

        if (handlerType is null ||
            !StringComparer.Ordinal.Equals(
                handlerType.FullName,
                expectedHandlerType))
        {
            throw new InvalidOperationException(
                "Interactive SDK ProcessHostPool validation did not resolve a real JwtBearerHandler. " +
                $"Scheme='{expectedScheme}', " +
                $"HandlerType='{handlerType?.FullName ?? "(null)"}'.");
        }

        output.WriteLine(
            "[INTERACTIVE SDK PROCESSHOSTPOOL] Authentication scheme verified. " +
            $"DefaultAuthenticateScheme='{options.DefaultAuthenticateScheme}', " +
            $"DefaultChallengeScheme='{options.DefaultChallengeScheme}', " +
            $"HandlerType='{handlerType.FullName}'.");
    }

    private static async Task PumpAsync(
        StreamReader reader,
        ITestOutputHelper output,
        string prefix)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            output.WriteLine($"{prefix} {line}");
        }
    }

    private static void CopyEnvironment(ProcessStartInfo start, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            start.Environment[name] = value;
        }
    }

    private static string RequiredSetting(
        IDictionary<string, string?> settings,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (settings.TryGetValue(name, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException(
            $"Interactive SDK ProcessHostPool validation requires one of these existing harness settings: '{string.Join(", ", names)}'.");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required interactive SDK validation environment variable '{name}' is missing.");

    private static string NormalizeTransport(string value) =>
        value.Trim().Equals("grpc", StringComparison.OrdinalIgnoreCase)
            ? "gRPC"
            : value.Trim().Equals("http", StringComparison.OrdinalIgnoreCase)
                ? "HTTP"
                : throw new InvalidOperationException(
                    $"Unsupported ProcessHostPool transport '{value}'.");

    private static string? ReadString(object instance, string name)
    {
        var member = FindMember(instance.GetType(), name);
        return member is null ? null : Convert.ToString(ReadMember(instance, member), CultureInfo.InvariantCulture);
    }

    private static string RequiredString(object instance, string name)
    {
        var value = ReadString(instance, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Required interactive SDK validation member '{name}' was not found on '{instance.GetType().FullName}'.");
    }

    private static MemberInfo? FindMember(Type type, string name) =>
        type.GetMember(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
            .FirstOrDefault();

    private static object? ReadMember(object instance, MemberInfo member) =>
        member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => null
        };

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed class GenericMcpNetworkProxy : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly HttpClient upstream;

        private GenericMcpNetworkProxy(
            WebApplication application,
            HttpClient upstream,
            string baseAddress)
        {
            this.application = application;
            this.upstream = upstream;
            BaseAddress = baseAddress.TrimEnd('/');
        }

        public string BaseAddress { get; }
        public string McpEndpoint => BaseAddress + "/mcp";
        public string AccessContextEndpoint => BaseAddress + "/auth/access-context";

        public static async Task<GenericMcpNetworkProxy> StartAsync(
            GenericMcpServerTestHost host,
            ITestOutputHelper output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(output);

            var upstream = host.CreateClient();
            upstream.Timeout = Timeout.InfiniteTimeSpan;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var logGate = new object();

            app.Run(async context =>
            {
                var inboundAuthorization = context.Request.Headers.Authorization.ToString();
                var inboundAuthorizationPresent = !string.IsNullOrWhiteSpace(inboundAuthorization);
                var inboundAuthorizationScheme = ReadAuthorizationScheme(inboundAuthorization);
                var inboundAccessContextPresent =
                    context.Request.Headers.TryGetValue("X-Access-Context", out var inboundAccessContext) &&
                    !string.IsNullOrWhiteSpace(inboundAccessContext.ToString());

                WriteProxyDiagnostic(
                    output,
                    logGate,
                    $"[INTERACTIVE SDK PROXY] Inbound Method='{context.Request.Method}', " +
                    $"Path='{context.Request.Path}', AuthorizationPresent='{inboundAuthorizationPresent}', " +
                    $"AuthorizationScheme='{inboundAuthorizationScheme}', " +
                    $"AccessContextPresent='{inboundAccessContextPresent}'.");

                using var request = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method),
                    context.Request.Path + context.Request.QueryString.Value);

                foreach (var header in context.Request.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    {
                        request.Content ??= new StreamContent(context.Request.Body);
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }

                var forwardedAuthorization =
                    request.Headers.TryGetValues("Authorization", out var forwardedAuthorizationValues)
                        ? forwardedAuthorizationValues.FirstOrDefault() ?? string.Empty
                        : string.Empty;
                var forwardedAuthorizationPresent = !string.IsNullOrWhiteSpace(forwardedAuthorization);
                var forwardedAuthorizationScheme = ReadAuthorizationScheme(forwardedAuthorization);
                var forwardedAccessContextPresent =
                    request.Headers.TryGetValues("X-Access-Context", out var forwardedAccessContextValues) &&
                    forwardedAccessContextValues.Any(value => !string.IsNullOrWhiteSpace(value));

                WriteProxyDiagnostic(
                    output,
                    logGate,
                    $"[INTERACTIVE SDK PROXY] Forwarded Method='{context.Request.Method}', " +
                    $"Path='{context.Request.Path}', AuthorizationPresent='{forwardedAuthorizationPresent}', " +
                    $"AuthorizationScheme='{forwardedAuthorizationScheme}', " +
                    $"AccessContextPresent='{forwardedAccessContextPresent}'.");

                if (context.Request.ContentLength is > 0 ||
                    context.Request.Headers.ContainsKey("Transfer-Encoding"))
                {
                    request.Content ??= new StreamContent(context.Request.Body);
                    if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                    {
                        request.Content.Headers.ContentType =
                            MediaTypeHeaderValue.Parse(context.Request.ContentType);
                    }
                }

                using var response =
                    await upstream.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            context.RequestAborted)
                        .ConfigureAwait(false);

                var challengeSchemes =
                    response.Headers.WwwAuthenticate
                        .Select(value => value.Scheme)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                WriteProxyDiagnostic(
                    output,
                    logGate,
                    $"[INTERACTIVE SDK PROXY] Upstream Method='{context.Request.Method}', " +
                    $"Path='{context.Request.Path}', Status='{(int)response.StatusCode}', " +
                    $"AuthenticateChallenges='{(challengeSchemes.Length == 0 ? "(none)" : string.Join(",", challengeSchemes))}'.");

                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                foreach (var header in response.Content.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                context.Response.Headers.Remove("transfer-encoding");

                await response.Content
                    .CopyToAsync(context.Response.Body, context.RequestAborted)
                    .ConfigureAwait(false);
            });

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("The validation proxy did not expose a listening address.");
            var address = addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            return new GenericMcpNetworkProxy(app, upstream, address);
        }

        private static string ReadAuthorizationScheme(string authorization)
        {
            if (string.IsNullOrWhiteSpace(authorization))
            {
                return "(none)";
            }

            var separator = authorization.IndexOf(' ');
            return separator > 0
                ? authorization[..separator].Trim()
                : authorization.Trim();
        }

        private static void WriteProxyDiagnostic(
            ITestOutputHelper output,
            object logGate,
            string message)
        {
            lock (logGate)
            {
                output.WriteLine(message);
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await application.StopAsync(timeout.Token).ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
            upstream.Dispose();
        }
    }

    private static class ProcessHostPoolReflectionInventory
    {
        public static async Task<Inventory> BuildAsync(
            object cluster,
            IServiceProvider services,
            string poolId,
            string transport,
            string controlPlaneEndpoint,
            int expectedHostCount,
            int expectedRuntimeCountPerHost)
        {
            var hostObjects = FindHostObjects(cluster).ToArray();
            if (hostObjects.Length != expectedHostCount)
            {
                throw new InvalidOperationException(
                    $"ProcessHostPool bridge could not prove exact parent-host membership. " +
                    $"Expected='{expectedHostCount}', Reflected='{hostObjects.Length}'.");
            }

            var registry =
                services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var registrySnapshots =
                (await registry
                        .ListAsync(includeStopped: false)
                        .ConfigureAwait(false))
                    .Cast<object>()
                    .Where(
                        snapshot =>
                            StringComparer.Ordinal.Equals(
                                ReadOptionalString(snapshot, "PoolId"),
                                poolId))
                    .ToArray();

            var hosts = new List<HostInventory>(hostObjects.Length);
            foreach (var host in hostObjects)
            {
                var hostId = RequiredString(host, "HostId", "ProcessHostId");
                var hostRegistrySnapshots =
                    registrySnapshots
                        .Where(
                            snapshot =>
                                StringComparer.Ordinal.Equals(
                                    ReadOptionalString(snapshot, "HostId"),
                                    hostId))
                        .ToArray();

                hosts.Add(
                    BuildHost(
                        host,
                        hostRegistrySnapshots,
                        transport,
                        expectedRuntimeCountPerHost));
            }

            var runtimeIds =
                hosts
                    .SelectMany(host => host.RuntimeInstances)
                    .Select(runtime => runtime.RuntimeInstanceId)
                    .ToArray();

            if (runtimeIds.Length != expectedHostCount * expectedRuntimeCountPerHost)
            {
                throw new InvalidOperationException(
                    $"ProcessHostPool bridge could not prove exact child-runtime membership. " +
                    $"Expected='{expectedHostCount * expectedRuntimeCountPerHost}', Observed='{runtimeIds.Length}'.");
            }

            if (runtimeIds.Distinct(StringComparer.Ordinal).Count() != runtimeIds.Length)
            {
                throw new InvalidOperationException(
                    "ProcessHostPool bridge observed duplicate RuntimeInstanceId values across parent hosts.");
            }

            return new Inventory(
                1,
                "runtime-pool-diagnostics",
                Guid.NewGuid().ToString("N"),
                "ProcessHostPool",
                transport,
                poolId,
                controlPlaneEndpoint,
                hosts,
                DateTimeOffset.UtcNow);
        }

        private static IEnumerable<object> FindHostObjects(object cluster)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var value in MemberValues(cluster))
            {
                if (value is null || value is string)
                {
                    continue;
                }

                if (value is IEnumerable enumerable)
                {
                    foreach (var item in enumerable.Cast<object?>().Where(x => x is not null).Cast<object>())
                    {
                        if (seen.Add(item) && LooksLikeHost(item))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        private static bool LooksLikeHost(object value) =>
            HasAnyMember(value, "HostId", "ProcessHostId") &&
            (HasAnyMember(value, "ProcessId", "ParentProcessId", "Process") ||
             value.GetType().Name.Contains("HostProcess", StringComparison.OrdinalIgnoreCase));

        private static HostInventory BuildHost(
            object host,
            IReadOnlyList<object> registrySnapshots,
            string transport,
            int expectedRuntimeCount)
        {
            var hostId = RequiredString(host, "HostId", "ProcessHostId");
            var processId = RequiredProcessId(host);
            var stableEndpoint = RequiredString(
                host,
                "StableTransportEndpoint",
                "StableEndpoint",
                "TransportEndpoint",
                "Endpoint",
                "BaseAddress",
                "Address");

            var snapshotsByRuntimeId =
                registrySnapshots
                    .GroupBy(
                        snapshot => RequiredString(snapshot, "RuntimeInstanceId"),
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray(),
                        StringComparer.Ordinal);

            if (snapshotsByRuntimeId.Count != expectedRuntimeCount)
            {
                throw new InvalidOperationException(
                    $"Host '{hostId}' registry membership mismatch. " +
                    $"Expected='{expectedRuntimeCount}', DistinctRuntimeInstanceIds='{snapshotsByRuntimeId.Count}', " +
                    $"Ids='{string.Join(",", snapshotsByRuntimeId.Keys.OrderBy(value => value, StringComparer.Ordinal))}'.");
            }

            var runtimes =
                snapshotsByRuntimeId
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(
                        pair =>
                            BuildRuntime(
                                pair.Key,
                                pair.Value,
                                hostId,
                                stableEndpoint,
                                transport))
                    .ToArray();

            return new HostInventory(
                hostId,
                processId,
                stableEndpoint,
                runtimes);
        }

        private static RuntimeInventory BuildRuntime(
            string runtimeId,
            IReadOnlyList<object> registrySnapshots,
            string hostId,
            string stableEndpoint,
            string transport)
        {
            var snapshot = registrySnapshots[0];

            var snapshotHostId = RequiredString(snapshot, "HostId");
            if (!StringComparer.Ordinal.Equals(snapshotHostId, hostId))
            {
                throw new InvalidOperationException(
                    $"Runtime registry HostId mismatch. RuntimeInstanceId='{runtimeId}', ExpectedHostId='{hostId}', SnapshotHostId='{snapshotHostId}'.");
            }

            var status = RequiredString(snapshot, "Status", "RuntimeStatus");
            if (!StringComparer.OrdinalIgnoreCase.Equals(status, "Ready"))
            {
                throw new InvalidOperationException(
                    $"Runtime instance is not Ready. RuntimeInstanceId='{runtimeId}', Status='{status}'.");
            }

            var canAccept = RequiredBool(snapshot, "CanAcceptRun", "CanAcceptRuns");
            if (!canAccept)
            {
                throw new InvalidOperationException(
                    $"Runtime instance cannot accept work. RuntimeInstanceId='{runtimeId}'.");
            }

            var expectedTransport = transport == "HTTP" ? "http" : "grpc";
            var provider =
                ReadOptionalString(snapshot, "ProviderName")
                ?? ReadMetadataString(snapshot, "provider.name", "provider")
                ?? throw new InvalidOperationException(
                    $"Runtime provider metadata is missing. RuntimeInstanceId='{runtimeId}'.");

            if (!StringComparer.OrdinalIgnoreCase.Equals(provider, expectedTransport))
            {
                throw new InvalidOperationException(
                    $"Runtime provider mismatch. RuntimeInstanceId='{runtimeId}', Expected='{expectedTransport}', Actual='{provider}'.");
            }

            var transportName =
                ReadOptionalString(snapshot, "TransportName")
                ?? ReadMetadataString(snapshot, "transport.name")
                ?? throw new InvalidOperationException(
                    $"Runtime transport metadata is missing. RuntimeInstanceId='{runtimeId}'.");

            if (!StringComparer.OrdinalIgnoreCase.Equals(transportName, expectedTransport))
            {
                throw new InvalidOperationException(
                    $"Runtime transport mismatch. RuntimeInstanceId='{runtimeId}', Expected='{expectedTransport}', Actual='{transportName}'.");
            }

            var endpoint =
                ReadOptionalString(
                    snapshot,
                    "Endpoint",
                    "RuntimeEndpoint",
                    "TransportEndpoint",
                    "Address")
                ?? ReadMetadataString(
                    snapshot,
                    "transport.endpoint",
                    "runtime.endpoint",
                    "http.endpoint",
                    "grpc.endpoint")
                ?? throw new InvalidOperationException(
                    $"Runtime transport endpoint metadata is missing. RuntimeInstanceId='{runtimeId}'.");

            if (!SameEndpoint(endpoint, stableEndpoint))
            {
                throw new InvalidOperationException(
                    $"Runtime descriptor does not target its owning ProcessHost stable endpoint. " +
                    $"RuntimeInstanceId='{runtimeId}', DescriptorEndpoint='{endpoint}', StableEndpoint='{stableEndpoint}'.");
            }

            int processId;
            if (!TryReadProcessId(snapshot, out processId) &&
                !TryReadMetadataInt(
                    snapshot,
                    out processId,
                    "process.id",
                    "processId",
                    "runtime.process.id",
                    "runtime.processId",
                    "child.process.id",
                    "childProcessId"))
            {
                throw new InvalidOperationException(
                    $"Runtime child process identity is missing from registry metadata. RuntimeInstanceId='{runtimeId}'.");
            }

            var routeId =
                ReadOptionalString(
                    snapshot,
                    "RouteId",
                    "TransportRouteId",
                    "RoutingRouteId")
                ?? ReadMetadataString(
                    snapshot,
                    "route.id",
                    "routeId",
                    "runtime.route.id");

            return new RuntimeInventory(
                runtimeId,
                routeId,
                processId,
                status,
                canAccept,
                provider,
                transportName,
                endpoint);
        }

        private static bool SameEndpoint(string left, string right)
        {
            static string Normalize(string value) =>
                value.Trim().TrimEnd('/');

            return StringComparer.OrdinalIgnoreCase.Equals(
                Normalize(left),
                Normalize(right));
        }

        private static IEnumerable<object?> MemberValues(object instance)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in instance.GetType().GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? value = null;
                try
                {
                    value = property.GetValue(instance);
                }
                catch
                {
                }

                yield return value;
            }

            foreach (var field in instance.GetType().GetFields(flags))
            {
                object? value = null;
                try
                {
                    value = field.GetValue(instance);
                }
                catch
                {
                }

                yield return value;
            }
        }

        private static bool HasAnyMember(object value, params string[] names) =>
            names.Any(name => FindMember(value.GetType(), name) is not null);

        private static string DescribeMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var properties = type
                .GetProperties(flags)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => property.Name);
            var fields = type
                .GetFields(flags)
                .Select(field => field.Name);

            return string.Join(
                ",",
                properties
                    .Concat(fields)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal));
        }

        private static string RequiredString(object value, params string[] names) =>
            ReadOptionalString(value, names)
            ?? throw new InvalidOperationException(
                $"Required ProcessHostPool diagnostic member was not found on '{value.GetType().FullName}'. " +
                $"Candidates='{string.Join(",", names)}'. AvailableMembers='{DescribeMembers(value.GetType())}'.");

        private static string? ReadOptionalString(object value, params string[] names)
        {
            foreach (var name in names)
            {
                var member = FindMember(value.GetType(), name);
                if (member is null)
                {
                    continue;
                }

                var raw = ReadMember(value, member);
                if (raw is Uri uri)
                {
                    return uri.ToString();
                }

                var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static bool RequiredBool(object value, params string[] names)
        {
            foreach (var name in names)
            {
                var member = FindMember(value.GetType(), name);
                if (member is null)
                {
                    continue;
                }

                var raw = ReadMember(value, member);
                if (raw is bool boolean)
                {
                    return boolean;
                }

                if (bool.TryParse(
                        Convert.ToString(raw, CultureInfo.InvariantCulture),
                        out var parsed))
                {
                    return parsed;
                }
            }

            throw new InvalidOperationException(
                $"Required ProcessHostPool boolean member was not found on '{value.GetType().FullName}'. " +
                $"Candidates='{string.Join(",", names)}'. AvailableMembers='{DescribeMembers(value.GetType())}'.");
        }

        private static int RequiredProcessId(object value)
        {
            if (TryReadProcessId(value, out var processId))
            {
                return processId;
            }

            throw new InvalidOperationException(
                $"Required ProcessId could not be resolved from '{value.GetType().FullName}'. " +
                $"AvailableMembers='{DescribeMembers(value.GetType())}'.");
        }

        private static bool TryReadProcessId(
            object value,
            out int processId)
        {
            foreach (var memberName in new[]
            {
                "ProcessId",
                "ParentProcessId",
                "ChildProcessId"
            })
            {
                var member = FindMember(value.GetType(), memberName);
                if (member is null)
                {
                    continue;
                }

                if (int.TryParse(
                        Convert.ToString(
                            ReadMember(value, member),
                            CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out processId) &&
                    processId > 0)
                {
                    return true;
                }
            }

            var processMember = FindMember(value.GetType(), "Process");
            if (processMember is not null &&
                ReadMember(value, processMember) is Process process &&
                process.Id > 0)
            {
                processId = process.Id;
                return true;
            }

            processId = 0;
            return false;
        }

        private static string? ReadMetadataString(
            object value,
            params string[] keys)
        {
            var member = FindMember(value.GetType(), "Metadata");
            if (member is null)
            {
                return null;
            }

            var raw = ReadMember(value, member);
            if (raw is null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (raw is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(
                                Convert.ToString(entry.Key, CultureInfo.InvariantCulture),
                                key))
                        {
                            var text = Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }

                if (raw is IEnumerable enumerable)
                {
                    foreach (var item in enumerable.Cast<object?>().Where(item => item is not null).Cast<object>())
                    {
                        var itemKey = ReadOptionalString(item, "Key");
                        if (!StringComparer.OrdinalIgnoreCase.Equals(itemKey, key))
                        {
                            continue;
                        }

                        var itemValue = ReadOptionalString(item, "Value");
                        if (!string.IsNullOrWhiteSpace(itemValue))
                        {
                            return itemValue;
                        }
                    }
                }
            }

            return null;
        }

        private static bool TryReadMetadataInt(
            object value,
            out int processId,
            params string[] keys)
        {
            var text = ReadMetadataString(value, keys);
            if (int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out processId) &&
                processId > 0)
            {
                return true;
            }

            processId = 0;
            return false;
        }
    }

    private sealed record Inventory(
        int SchemaVersion,
        string Source,
        string CaptureId,
        string RuntimeProvider,
        string RuntimeTransport,
        string PoolId,
        string ControlPlaneEndpoint,
        IReadOnlyList<HostInventory> Hosts,
        DateTimeOffset CapturedAtUtc);

    private sealed record HostInventory(
        string HostId,
        int ProcessId,
        string StableEndpoint,
        IReadOnlyList<RuntimeInventory> RuntimeInstances);

    private sealed record RuntimeInventory(
        string RuntimeInstanceId,
        string? RouteId,
        int ProcessId,
        string Status,
        bool CanAcceptRun,
        string ProviderName,
        string TransportName,
        string? Endpoint);
}
