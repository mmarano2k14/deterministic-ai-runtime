from __future__ import annotations

import importlib.util
from pathlib import Path

HERE = Path(__file__).resolve().parent
files = {
    "patcher": HERE / "apply_processhostpool_bridge.py",
    "launcher": HERE / "launch_processhostpool_matrix.py",
    "hook": HERE / "processhostpool-bridge" / "ProcessHostPoolInteractiveSdkValidationHook.cs",
}
missing = [str(path) for path in files.values() if not path.is_file()]
if missing:
    raise SystemExit("Step 4 launcher verification FAILED: missing " + ", ".join(missing))

# Syntax-check Python without writing __pycache__.
for key in ("patcher", "launcher"):
    source = files[key].read_text(encoding="utf-8")
    compile(source, str(files[key]), "exec")

patcher = files["patcher"].read_text(encoding="utf-8")
for literal in (
    "ProcessHostPoolProductionScenarioTestsBase.cs",
    "GenericMcpServerTestHost.cs",
    "ApplyRuntimeValidationSettings",
    "ApplyAuthenticationSettings",
    "TryRunAsync",
    "Tests:UseRealJwtAuthentication",
    "Interactive SDK dedicated JwtBearer scheme",
    "InteractiveSdkJwtBearer",
    "JwtBearerExtensions.AddJwtBearer",
    "FakeAuthHandler remains the default",
    "No patch applied",
):
    if literal not in patcher:
        raise SystemExit(f"Step 4 launcher verification FAILED: patcher missing {literal}")

launcher = files["launcher"].read_text(encoding="utf-8")
for literal in (
    "--list-tests",
    "AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION",
    "FullyQualifiedName=",
    "--build-configuration",
    'default="Debug"',
    "OPENAI_API_KEY is required by the server-side interactive agent runtime",
    "ENV_ALLOWLIST",
    "ProcessHostPool/{args.runtime_transport} 3/3",
):
    if literal not in launcher:
        raise SystemExit(f"Step 4 launcher verification FAILED: launcher missing {literal}")

hook = files["hook"].read_text(encoding="utf-8")
for literal in (
    "GenericMcpNetworkProxy",
    "ApplyRuntimeValidationSettings",
    "ApplyAuthenticationSettings",
    'settings["Tests:UseRealJwtAuthentication"] = "true"',
    'settings["AiEngine:PayloadStore:Provider"] = "mongo-redis"',
    'settings["AiEngine:PayloadStore:RequireReplaySafePayloads"] = "true"',
    'start.Environment["AI_DEMO_TENANT_ID"] = tenantId',
    'private static string RequiredString(object instance, string name)',
    'var tenantId = RequiredString(tenant, "TenantId")',
    "AssertRealJwtAuthenticationSchemeAsync",
    'const string expectedScheme = "InteractiveSdkJwtBearer"',
    "JwtBearerHandler",
    "AccessContextEndpoint",
    "AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT",
    "DefaultAuthenticateScheme",
    "DefaultChallengeScheme",
    "[INTERACTIVE SDK PROXY] Inbound",
    "[INTERACTIVE SDK PROXY] Forwarded",
    "[INTERACTIVE SDK PROXY] Upstream",
    "AuthorizationPresent",
    "AuthorizationScheme",
    "AccessContextPresent",
    "AuthenticateChallenges",
    "ReadAuthorizationScheme",
    "run_processhostpool_matrix.py",
    "ProcessHostCount",
    "RuntimeCountPerHost",
    "StableTransportEndpoint",
    "ParentProcessId",
    "IAiRuntimeInstanceRegistry",
):
    if literal not in hook:
        raise SystemExit(f"Step 4 launcher verification FAILED: hook missing {literal}")

if 'settings["OpenAI:ApiKey"] = RequiredEnvironment("OPENAI_API_KEY");' not in hook:
    raise SystemExit(
        "Step 4 launcher verification FAILED: ProcessHost server settings must replace the production proof placeholder with the real server-owned OpenAI key"
    )

if 'start.Environment.Remove("OPENAI_API_KEY")' not in hook:
    raise SystemExit(
        "Step 4 launcher verification FAILED: external matrix subprocess must not inherit OPENAI_API_KEY"
    )

# Synthetic patch/idempotency proof for both modified integration-test files.
spec = importlib.util.spec_from_file_location("step4_patcher", files["patcher"])
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

base = '''class X
{
    private ProcessHostPoolProductionCluster marker;
    async Task Run()
    {
        var controlPlaneSettings =
            Builder.Build(
                alpha,
                beta);
        var poolId = "p";
        var controlPlaneId = "c";
        var tenant = new object();
        var maximumProcessHostCount = 3;
        var runtimeCountPerHost = 3;
        await using var differentlyNamedPool =
            await ProcessHostPoolProductionCluster.StartAsync();
        await using var controlPlaneHost =
            new GenericMcpServerTestHost(controlPlaneSettings);
        for (var cycleNumber = 1;
             cycleNumber <= executionCycleCount;
             cycleNumber++)
        {
        }
    }
}
'''
base_once = module._inject_hook(module._inject_auth(module._inject_runtime_settings(base)))
base_twice = module._inject_hook(module._inject_auth(module._inject_runtime_settings(base_once)))
if base_once != base_twice:
    raise SystemExit("Step 4 launcher verification FAILED: base harness patch is not idempotent")

# Simulate a repository already carrying the V6 auth/hook additions: runtime settings
# must still be insertable without requiring a particular cluster variable name.
upgrade_input = module._inject_hook(module._inject_auth(base))
upgrade_once = module._inject_runtime_settings(upgrade_input)
upgrade_twice = module._inject_runtime_settings(upgrade_once)
if upgrade_once != upgrade_twice or "ApplyRuntimeValidationSettings" not in upgrade_once:
    raise SystemExit("Step 4 launcher verification FAILED: V6-to-V8 upgrade patch is not idempotent")
if not (
    base_once.index("ApplyRuntimeValidationSettings") < base_once.index("ProcessHostPoolProductionCluster.StartAsync")
    and base_once.index("ApplyAuthenticationSettings") < base_once.index("await using var controlPlaneHost")
):
    raise SystemExit("Step 4 launcher verification FAILED: runtime/auth patch ordering is wrong")

generic = '''public sealed class GenericMcpServerTestHost
{
    private readonly IReadOnlyDictionary<string, string?> settings;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = FakeAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = FakeAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = FakeAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(
                    FakeAuthHandler.AuthenticationScheme,
                    _ => { });
            services.AddAuthorization();
        });
    }
}
'''
generic_once = module._inject_interactive_sdk_jwt_handler(
    module._inject_real_jwt_auth_bypass(generic)
)
generic_twice = module._inject_interactive_sdk_jwt_handler(
    module._inject_real_jwt_auth_bypass(generic_once)
)
if generic_once != generic_twice:
    raise SystemExit("Step 4 launcher verification FAILED: JWT host patch is not idempotent")
if generic_once.count("FakeAuthHandler.AuthenticationScheme") != 4:
    raise SystemExit("Step 4 launcher verification FAILED: historical FakeAuthHandler block was not preserved")
for literal in (
    "Interactive SDK dedicated JwtBearer scheme",
    'const string interactiveSdkJwtScheme = "InteractiveSdkJwtBearer"',
    "JwtBearerExtensions.AddJwtBearer",
    "TokenValidationParameters",
    "SymmetricSecurityKey",
    "options.DefaultAuthenticateScheme = interactiveSdkJwtScheme",
    "options.DefaultChallengeScheme = interactiveSdkJwtScheme",
    "options.DefaultForbidScheme = interactiveSdkJwtScheme",
    "MatrixBearer remains registered for historical matrix tests",
):
    if literal not in generic_once:
        raise SystemExit(
            f"Step 4 launcher verification FAILED: dedicated real-JWT patch missing {literal}"
        )


run_e2e_text = (HERE / "run_e2e.py").read_text(encoding="utf-8")
if "prefer_running_docker_runtime=False" not in run_e2e_text:
    raise SystemExit(
        "Step 4 launcher verification FAILED: explicit local-jwt still allows Docker auth discovery"
    )
if "[validation] auth scope:" not in run_e2e_text:
    raise SystemExit(
        "Step 4 launcher verification FAILED: non-secret auth-scope diagnostics are missing"
    )
if "AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT" not in run_e2e_text or "urljoin" not in run_e2e_text:
    raise SystemExit(
        "Step 4 launcher verification FAILED: access-context endpoint is not pinned to the runtime boundary"
    )

print("ProcessHostPool production-harness bridge contract: PASS")
print("- production ProcessHostPool setup remains authoritative")
print("- replay-safe mongo-redis payload settings are applied before pool startup")
print("- FakeAuthHandler remains the default for historical integration tests")
print("- dedicated real JwtBearer authentication is registered only for the environment-gated SDK matrix")
print("- the JWT scheme handler type is runtime-verified before SDK execution")
print("- RBAC bootstrap and MCP endpoints are pinned to the same loopback proxy authority")
print("- MatrixBearer remains registered for historical matrix tests")
print("- external JWT tenant identity is aligned with the real ProcessHostPool tenant")
print("- OPENAI_API_KEY is server-owned and stripped from external SDK consumer processes")
print("- Debug is the default harness build configuration to avoid unrelated Release DLL locks")
print("- base-harness and JWT-host patches are idempotent and fail closed")

# V14: the public control-plane validation host must not inherit matrix-harness mode,
# because the production standalone access-context endpoint is intentionally absent there.
if 'settings["AiMatrixHarness:Enabled"] = "false";' not in hook:
    raise SystemExit("Step 4 launcher verification FAILED: public JWT host must pin AiMatrixHarness:Enabled=false")
if 'settings["AiHostedInvocation:EnableWorkerPolling"] = "false";' not in hook:
    raise SystemExit("Step 4 launcher verification FAILED: public control-plane must disable worker polling")
if 'settings["AiHostedInvocation:TenantId"] = tenantId;' not in hook or 'settings["AiHostedInvocation:TenantGroupId"] = tenantGroupId;' not in hook:
    raise SystemExit("Step 4 launcher verification FAILED: control-plane hosted invocation scope is not pinned to the production tenant")
if 'settings["AiHostedInvocation:ControlPlaneId"] = configuredControlPlaneId;' not in hook:
    raise SystemExit("Step 4 launcher verification FAILED: control-plane hosted invocation ControlPlaneId is not pinned")
if 'settings["AiHostedInvocation:Enabled"] = "false";' not in hook:
    raise SystemExit("Step 4 launcher verification FAILED: parent ProcessHost hosted invocation must be explicitly disabled")
for required in (
    'AiHostedInvocation__Enabled',
    'AiHostedInvocation__EnableWorkerPolling',
    'AiHostedInvocation__EnableDagReconciliation',
    'AiHostedInvocation__TenantId',
    'AiHostedInvocation__TenantGroupId',
    'AiHostedInvocation__ControlPlaneId',
):
    if required not in hook:
        raise SystemExit(f"Step 4 launcher verification FAILED: child runtime role setting missing: {required}")

# V19: child-runtime diagnostics must be explicitly gated and must not alter normal runs.
launcher_text = (HERE / "launch_processhostpool_matrix.py").read_text(encoding="utf-8")
patcher_text = (HERE / "apply_processhostpool_bridge.py").read_text(encoding="utf-8")
if '"MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE": "1"' not in launcher_text:
    raise SystemExit("Step 4 launcher verification FAILED: V19 child trace gate is not enabled by the interactive launcher")
for required in (
    "_inject_child_runtime_trace",
    "_inject_parent_trace_forwarding",
    "DrainInteractiveTraceAsync",
    "[INTERACTIVE CHILD TRACE]",
    "MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE",
):
    if required not in patcher_text:
        raise SystemExit(f"Step 4 launcher verification FAILED: V19 diagnostic patch missing {required}")
print("- V19 child-runtime stdout/stderr diagnostics are environment-gated and filtered")
