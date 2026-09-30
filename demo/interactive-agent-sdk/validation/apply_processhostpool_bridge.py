from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
BRIDGE_SOURCE = HERE / "processhostpool-bridge" / "ProcessHostPoolInteractiveSdkValidationHook.cs"

HOOK_FQN = (
    "Multiplexed.AI.McpServer.Tests.Integration.Validation.InteractiveSdk."
    "ProcessHostPoolInteractiveSdkValidationHook"
)


def _repo_root(start: Path) -> Path:
    current = start.resolve()
    for candidate in (current, *current.parents):
        if (candidate / "implementations" / "dotnet" / "Tests" / "Multiplexed.AI.McpServer.Tests.Integration").is_dir():
            return candidate
    raise RuntimeError("Repository root could not be resolved.")


def _find_one(project: Path, filename: str) -> Path:
    matches = list(project.rglob(filename))
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly one {filename}, found {len(matches)}: {matches}")
    return matches[0]


def _inject_runtime_settings(text: str) -> str:
    call = f"{HOOK_FQN}.ApplyRuntimeValidationSettings"
    old_call = f"{call}(controlPlaneSettings);"
    new_call = f"{call}(controlPlaneSettings, tenant.TenantId, tenant.TenantGroupId);"
    if old_call in text:
        return text.replace(old_call, new_call)
    if new_call in text:
        return text

    # Patch immediately after the controlPlaneSettings assignment. This is a much
    # more stable boundary than depending on the spelling/format of the later
    # ProcessHostPool cluster declaration, and it guarantees the replay-safe
    # settings are present before any external ProcessHost is started.
    pattern = re.compile(
        r"(?P<indent>^[ \t]*)(?:var|Dictionary<[^\n]+>|IReadOnlyDictionary<[^\n]+>|IDictionary<[^\n]+>)\s+controlPlaneSettings\s*=",
        re.MULTILINE,
    )
    match = pattern.search(text)
    if not match:
        # Fall back to any ordinary assignment to the canonical variable.
        pattern = re.compile(
            r"(?P<indent>^[ \t]*)controlPlaneSettings\s*=",
            re.MULTILINE,
        )
        match = pattern.search(text)

    if not match:
        raise RuntimeError(
            "Could not find the controlPlaneSettings assignment. No patch applied."
        )

    statement_end = text.find(";", match.end())
    if statement_end < 0:
        raise RuntimeError(
            "Could not find the end of the controlPlaneSettings assignment. No patch applied."
        )

    # Guard against accidentally matching an unrelated assignment far away from
    # the ProcessHostPool setup. The production base must reference the pool type
    # later in the same method.
    pool_marker = text.find("ProcessHostPoolProductionCluster", statement_end)
    if pool_marker < 0:
        raise RuntimeError(
            "controlPlaneSettings was found, but no later ProcessHostPoolProductionCluster setup exists. No patch applied."
        )

    indent = match.group("indent")
    insertion = f"\n\n{indent}{call}(controlPlaneSettings, tenant.TenantId, tenant.TenantGroupId);"
    return text[: statement_end + 1] + insertion + text[statement_end + 1 :]


def _inject_auth(text: str) -> str:
    if f"{HOOK_FQN}.ApplyAuthenticationSettings" in text:
        return text

    cluster_pos = text.find("ProcessHostPoolProductionCluster")
    if cluster_pos < 0:
        raise RuntimeError("Could not find the ProcessHostPoolProductionCluster setup marker. No patch applied.")

    pattern = re.compile(
        r"(?P<indent>^[ \t]*)await using var controlPlaneHost\s*=\s*\n"
        r"(?P<body>[ \t]*new GenericMcpServerTestHost\(controlPlaneSettings\);)",
        re.MULTILINE,
    )
    match = pattern.search(text, cluster_pos)
    if not match:
        raise RuntimeError(
            "Could not find the GenericMcpServerTestHost(controlPlaneSettings) construction marker. No patch applied."
        )

    indent = match.group("indent")
    insertion = f"{indent}{HOOK_FQN}.ApplyAuthenticationSettings(controlPlaneSettings);\n\n"
    return text[: match.start()] + insertion + text[match.start() :]


def _inject_hook(text: str) -> str:
    if HOOK_FQN in text and "TryRunAsync(" in text:
        return text

    cluster_pos = text.find("ProcessHostPoolProductionCluster")
    if cluster_pos < 0:
        raise RuntimeError("Could not find the ProcessHostPoolProductionCluster setup marker. No patch applied.")

    pattern = re.compile(
        r"(?P<indent>^[ \t]*)for \(var cycleNumber = 1;\s*\n"
        r"[ \t]*cycleNumber <= executionCycleCount;",
        re.MULTILINE,
    )
    match = pattern.search(text, cluster_pos)
    if not match:
        raise RuntimeError("Could not find the ProcessHostPool execution-cycle marker. No patch applied.")

    required_tokens = (
        "ProcessHostPoolProductionCluster",
        "controlPlaneHost",
        "controlPlaneSettings",
        "poolId",
        "controlPlaneId",
        "maximumProcessHostCount",
        "runtimeCountPerHost",
    )
    missing = [token for token in required_tokens if token not in text[: match.start()]]
    if missing:
        raise RuntimeError(
            "ProcessHostPool bridge marker found but required setup variables are not visible before it: "
            + ", ".join(missing)
        )

    indent = match.group("indent")
    block = f"""{indent}if (await {HOOK_FQN}
{indent}        .TryRunAsync(
{indent}            this.profile,
{indent}            cluster,
{indent}            controlPlaneHost,
{indent}            controlPlaneSettings,
{indent}            poolId,
{indent}            controlPlaneId,
{indent}            tenant,
{indent}            maximumProcessHostCount,
{indent}            runtimeCountPerHost,
{indent}            this.output)
{indent}        .ConfigureAwait(false))
{indent}{{
{indent}    return;
{indent}}}

"""
    return text[: match.start()] + block + text[match.start() :]


def _inject_real_jwt_auth_bypass(text: str) -> str:
    marker = "Tests:UseRealJwtAuthentication"
    if marker in text:
        return text

    # Locate the test-only FakeAuthHandler registration inside ConfigureTestServices.
    start = re.search(
        r"(?P<indent>^[ \t]*)services\s*\n[ \t]*\.AddAuthentication\(options =>",
        text,
        re.MULTILINE,
    )
    if not start:
        raise RuntimeError(
            "Could not find the GenericMcpServerTestHost FakeAuthHandler authentication block. No patch applied."
        )

    end = re.search(
        r"^[ \t]*services\.AddAuthorization\(\);",
        text[start.end() :],
        re.MULTILINE,
    )
    if not end:
        raise RuntimeError(
            "Could not find services.AddAuthorization() after FakeAuthHandler registration. No patch applied."
        )

    block_start = start.start()
    block_end = start.end() + end.end()
    original = text[block_start:block_end]
    indent = start.group("indent")

    # Indent the complete historical block under the default branch. This preserves
    # existing integration-test behavior byte-for-byte apart from indentation.
    nested_lines: list[str] = []
    for line in original.splitlines():
        if line.startswith(indent):
            line = line[len(indent) :]
        nested_lines.append(indent + "    " + line)
    nested = "\n".join(nested_lines)

    replacement = (
        f'{indent}var useRealJwtAuthentication =\n'
        f'{indent}    settings.TryGetValue("Tests:UseRealJwtAuthentication", out var useRealJwtAuthenticationValue) &&\n'
        f'{indent}    bool.TryParse(useRealJwtAuthenticationValue, out var parsedUseRealJwtAuthentication) &&\n'
        f'{indent}    parsedUseRealJwtAuthentication;\n\n'
        f'{indent}if (!useRealJwtAuthentication)\n'
        f'{indent}{{\n'
        f'{nested}\n'
        f'{indent}}}\n'
        f'{indent}else\n'
        f'{indent}{{\n'
        f'{indent}    // This path is used only by the external interactive SDK validation.\n'
        f'{indent}    // A dedicated real JwtBearer handler is installed by the follow-up patch.\n'
        f'{indent}    services.AddAuthorization();\n'
        f'{indent}    Console.WriteLine(\n'
        f'{indent}        "[TEST MCP HOST] Real JWT authentication preserved for interactive SDK validation.");\n'
        f'{indent}}}'
    )
    return text[:block_start] + replacement + text[block_end:]



def _inject_interactive_sdk_jwt_handler(text: str) -> str:
    marker = "Interactive SDK dedicated JwtBearer scheme"
    if marker in text:
        return text

    if "Tests:UseRealJwtAuthentication" not in text:
        raise RuntimeError(
            "The real-JWT test-host branch is missing. Apply the authentication bypass before the dedicated JWT scheme."
        )

    # Upgrade both the original real-JWT branch and the V11/V12 branch that only
    # changed AuthenticationOptions defaults. The latter proved insufficient:
    # a default scheme name is not evidence that a JwtBearerHandler is actually
    # registered for that scheme.
    v11_pattern = re.compile(
        r'(?P<indent>^[ \t]*)services\.AddAuthorization\(\);\s*\n'
        r'[ \t]*// Interactive SDK real JWT default scheme override\.[\s\S]*?'
        r'[ \t]*Console\.WriteLine\(\s*\n'
        r'[ \t]*"\[TEST MCP HOST\] Real JWT authentication preserved for interactive SDK validation\."\);',
        re.MULTILINE,
    )
    match = v11_pattern.search(text)

    if match is None:
        base_pattern = re.compile(
            r'(?P<indent>^[ \t]*)services\.AddAuthorization\(\);\s*\n'
            r'[ \t]*Console\.WriteLine\(\s*\n'
            r'[ \t]*"\[TEST MCP HOST\] Real JWT authentication preserved for interactive SDK validation\."\);',
            re.MULTILINE,
        )
        match = base_pattern.search(text)

    if match is None:
        raise RuntimeError(
            "Could not find the gated real-JWT GenericMcpServerTestHost branch. No patch applied."
        )

    indent = match.group("indent")
    i1 = indent + "    "
    i2 = indent + "        "
    i3 = indent + "            "
    block = (
        f'{indent}// Interactive SDK dedicated JwtBearer scheme.\n'
        f'{indent}// This test-only scheme is isolated from MatrixBearer and from historical\n'
        f'{indent}// FakeAuthHandler tests. It performs real signature/issuer/audience/lifetime validation.\n'
        f'{indent}const string interactiveSdkJwtScheme = "InteractiveSdkJwtBearer";\n'
        f'{indent}var interactiveSdkJwtIssuer = GetRequiredSetting(settings, "AiMcpAuthentication:Issuer");\n'
        f'{indent}var interactiveSdkJwtAudience = GetRequiredSetting(settings, "AiMcpAuthentication:Audience");\n'
        f'{indent}var interactiveSdkJwtSigningKey = GetRequiredSetting(settings, "AiMcpAuthentication:SymmetricSigningKey");\n\n'
        f'{indent}Microsoft.Extensions.DependencyInjection.JwtBearerExtensions.AddJwtBearer(\n'
        f'{i1}services.AddAuthentication(),\n'
        f'{i1}interactiveSdkJwtScheme,\n'
        f'{i1}options =>\n'
        f'{i1}{{\n'
        f'{i2}options.RequireHttpsMetadata = false;\n'
        f'{i2}options.SaveToken = false;\n'
        f'{i2}options.MapInboundClaims = false;\n'
        f'{i2}options.TokenValidationParameters =\n'
        f'{i3}new Microsoft.IdentityModel.Tokens.TokenValidationParameters\n'
        f'{i3}{{\n'
        f'{i3}    ValidateIssuer = true,\n'
        f'{i3}    ValidIssuer = interactiveSdkJwtIssuer,\n'
        f'{i3}    ValidateAudience = true,\n'
        f'{i3}    ValidAudience = interactiveSdkJwtAudience,\n'
        f'{i3}    ValidateIssuerSigningKey = true,\n'
        f'{i3}    IssuerSigningKey =\n'
        f'{i3}        new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(\n'
        f'{i3}            System.Text.Encoding.UTF8.GetBytes(interactiveSdkJwtSigningKey)),\n'
        f'{i3}    ValidateLifetime = true,\n'
        f'{i3}    RequireExpirationTime = true,\n'
        f'{i3}    RequireSignedTokens = true,\n'
        f'{i3}    ClockSkew = TimeSpan.FromSeconds(30),\n'
        f'{i3}    NameClaimType = "sub"\n'
        f'{i3}}};\n'
        f'{i1}}});\n\n'
        f'{indent}services.AddAuthorization();\n'
        f'{indent}services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>\n'
        f'{indent}{{\n'
        f'{i1}options.DefaultScheme = interactiveSdkJwtScheme;\n'
        f'{i1}options.DefaultAuthenticateScheme = interactiveSdkJwtScheme;\n'
        f'{i1}options.DefaultChallengeScheme = interactiveSdkJwtScheme;\n'
        f'{i1}options.DefaultForbidScheme = interactiveSdkJwtScheme;\n'
        f'{indent}}});\n'
        f'{indent}Console.WriteLine(\n'
        f'{i1}"[TEST MCP HOST] Dedicated interactive SDK JwtBearer authentication registered; MatrixBearer remains registered for historical matrix tests.");\n'
        f'{indent}Console.WriteLine(\n'
        f'{i1}"[TEST MCP HOST] Real JWT authentication preserved for interactive SDK validation.");'
    )

    upgraded = text[:match.start()] + block + text[match.end():]
    upgraded = upgraded.replace(
        "// Preserve the production authentication services registered by Program.\n",
        "// Register an isolated real JwtBearer handler for this validation path.\n",
    )
    upgraded = upgraded.replace(
        "// A dedicated real JwtBearer handler is installed by the follow-up patch.\n",
        "",
    )
    return upgraded

def _inject_processhost_runtime_roles(text: str) -> str:
    call = f"{HOOK_FQN}.ApplyProcessHostRuntimeValidationSettings"
    if call in text:
        return text

    marker = "            return settings;\n        }\n\n        private static void DisableLocalRuntimeCapacity"
    if marker not in text:
        raise RuntimeError(
            "Could not find the BuildProcessHostSettings return boundary. No patch applied."
        )

    insertion = (
        "            Multiplexed.AI.McpServer.Tests.Integration.Validation.InteractiveSdk."
        "ProcessHostPoolInteractiveSdkValidationHook.ApplyProcessHostRuntimeValidationSettings(\n"
        "                settings,\n"
        "                tenant.TenantId,\n"
        "                tenant.TenantGroupId,\n"
        "                controlPlaneId);\n\n"
        "            return settings;\n        }\n\n        private static void DisableLocalRuntimeCapacity"
    )
    return text.replace(marker, insertion, 1)


# Generated child tracing must be upgraded even when an earlier trace gate exists.
# Configuration values are deliberately excluded from the diagnostic allowlist.
_CHILD_DIAGNOSTIC_TERMS = (
    "plan", "openai", "exception", "error", "fail", "retry", "invocation",
    "MONGO SNAPSHOT STORE", "[AI SNAPSHOT]", "ai.execution.snapshot",
    "[AI DAG BATCH PHASE]",
)
_LEGACY_CHILD_DIAGNOSTIC_TERMS = {
    "plan", "openai", "exception", "error", "fail", "retry", "invocation",
    "MONGO SNAPSHOT STORE", "AiEngine__Snapshots", "ConnectionStrings__Mongo",
    "Mongo__DatabaseName",
}


def _upgrade_child_runtime_trace(text: str) -> str:
    """Upgrade known generated tracing in place; reject partial/unknown installations."""
    if text.count("private static async Task<string> DrainInteractiveTraceAsync(") != 1:
        raise RuntimeError("Child trace drain is missing or ambiguous. No diagnostic patch applied.")
    predicate = re.compile(
        r"(?P<indent>^[ \t]*)private static bool IsInteractiveDiagnosticLine\(string line\)"
        r"\s*\{(?P<body>[^{}]*)\}",
        re.MULTILINE,
    )
    matches = list(predicate.finditer(text))
    if len(matches) != 1:
        raise RuntimeError("Child trace predicate is missing or ambiguous. No diagnostic patch applied.")
    match = matches[0]
    term_pattern = re.compile(
        r'line\.Contains\("([^"\n]+)",\s*StringComparison\.OrdinalIgnoreCase\)'
    )
    terms = term_pattern.findall(match.group("body"))
    residual = term_pattern.sub("", match.group("body"))
    residual = re.sub(r"\breturn\b|\|\||;|\s+", "", residual)
    allowed = _LEGACY_CHILD_DIAGNOSTIC_TERMS | set(_CHILD_DIAGNOSTIC_TERMS)
    if not terms or residual or not set(terms).issubset(allowed):
        raise RuntimeError("Child trace predicate has unrecognized edits. No diagnostic patch applied.")
    indent = match.group("indent")
    statements = []
    for index, term in enumerate(_CHILD_DIAGNOSTIC_TERMS):
        prefix = "return " if index == 0 else "       "
        suffix = ";" if index == len(_CHILD_DIAGNOSTIC_TERMS) - 1 else " ||"
        statements.append(
            f'{indent}    {prefix}line.Contains("{term}", StringComparison.OrdinalIgnoreCase){suffix}'
        )
    replacement = (
        f"{indent}private static bool IsInteractiveDiagnosticLine(string line)\n"
        f"{indent}{{\n" + "\n".join(statements) + f"\n{indent}}}"
    )
    text = text[:match.start()] + replacement + text[match.end():]

    gate = re.compile(
        r'(?<![\w.])(?:System\.)?Environment\.GetEnvironmentVariable\('
        r'\s*"MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE"\s*\)'
    )
    if len(list(gate.finditer(text))) != 1:
        raise RuntimeError("Child trace environment gate is missing or ambiguous. No diagnostic patch applied.")
    text = gate.sub(
        'System.Environment.GetEnvironmentVariable("MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE")',
        text,
        count=1,
    )
    # This timestamp measures tee receipt, not the MongoDB commit timestamp.
    old_emitter = (
        '$"[INTERACTIVE CHILD TRACE] RuntimeInstanceId={runtimeInstanceId} Stream={stream} {line}"'
    )
    new_emitter = (
        '$"[INTERACTIVE CHILD TRACE] ObservedAtUtc={System.DateTimeOffset.UtcNow:O} '
        'RuntimeInstanceId={runtimeInstanceId} Stream={stream} {line}"'
    )
    if text.count(old_emitter) == 1 and new_emitter not in text:
        text = text.replace(old_emitter, new_emitter, 1)
    elif text.count(new_emitter) != 1 or old_emitter in text:
        raise RuntimeError("Child trace emitter is missing or ambiguous. No diagnostic patch applied.")
    return text


def _inject_child_runtime_trace(text: str) -> str:
    marker = "MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE"
    if marker in text:
        return _upgrade_child_runtime_trace(text)

    old = """            if (redirectOutput)
            {
                this.standardOutputDrain =
                    process.StandardOutput.ReadToEndAsync();

                this.standardErrorDrain =
                    process.StandardError.ReadToEndAsync();
            }"""
    new = """            if (redirectOutput)
            {
                var interactiveTrace =
                    string.Equals(
                        System.Environment.GetEnvironmentVariable("MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE"),
                        "1",
                        StringComparison.Ordinal);

                this.standardOutputDrain = interactiveTrace
                    ? DrainInteractiveTraceAsync(
                        process.StandardOutput,
                        "OUT",
                        request.RuntimeInstanceId)
                    : process.StandardOutput.ReadToEndAsync();

                this.standardErrorDrain = interactiveTrace
                    ? DrainInteractiveTraceAsync(
                        process.StandardError,
                        "ERR",
                        request.RuntimeInstanceId)
                    : process.StandardError.ReadToEndAsync();
            }"""
    if old not in text:
        raise RuntimeError("Could not find child stdout/stderr drain block. No diagnostic patch applied.")
    text = text.replace(old, new, 1)

    boundary = """        /// <summary>
        /// Observes process completion and creates one typed completion result.
        /// </summary>"""
    helper = """        private static async Task<string> DrainInteractiveTraceAsync(
            System.IO.StreamReader reader,
            string stream,
            string runtimeInstanceId)
        {
            var captured = new System.Text.StringBuilder();

            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                captured.AppendLine(line);

                if (IsInteractiveDiagnosticLine(line))
                {
                    Console.Error.WriteLine(
                        $"[INTERACTIVE CHILD TRACE] RuntimeInstanceId={runtimeInstanceId} Stream={stream} {line}");
                }
            }

            return captured.ToString();
        }

        private static bool IsInteractiveDiagnosticLine(string line)
        {
            return line.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("invocation", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("MONGO SNAPSHOT STORE", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[AI SNAPSHOT]", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("ai.execution.snapshot", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[AI DAG BATCH PHASE]", StringComparison.OrdinalIgnoreCase);
        }

"""
    if boundary not in text:
        raise RuntimeError("Could not find child completion boundary. No diagnostic patch applied.")
    return _upgrade_child_runtime_trace(
        text.replace(boundary, helper + boundary, 1)
    )


def _inject_parent_trace_forwarding(text: str) -> str:
    marker = "INTERACTIVE CHILD TRACE"
    if marker in text:
        return text

    needle = """            if (line is null)
            {
                return;
            }

            this.capturedLines.Enqueue("""
    replacement = """            if (line is null)
            {
                return;
            }

            if (line.Contains("[INTERACTIVE CHILD TRACE]", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"[PROCESSHOST {this.Ordinal:D2} {stream}] {line}");
            }

            this.capturedLines.Enqueue("""
    if needle not in text:
        raise RuntimeError("Could not find parent ProcessHost CaptureLine boundary. No diagnostic patch applied.")
    return text.replace(needle, replacement, 1)

def main() -> int:
    parser = argparse.ArgumentParser(
        description="Install the environment-gated ProcessHostPool interactive SDK bridge into the integration-test harness."
    )
    parser.add_argument("--repo-root", type=Path, default=None)
    args = parser.parse_args()

    root = args.repo_root.resolve() if args.repo_root else _repo_root(HERE)
    project = root / "implementations" / "dotnet" / "Tests" / "Multiplexed.AI.McpServer.Tests.Integration"
    base = _find_one(project, "ProcessHostPoolProductionScenarioTestsBase.cs")
    generic_host = _find_one(project, "GenericMcpServerTestHost.cs")
    composer = _find_one(project, "ProcessHostPoolProductionScenarioSettingsComposer.cs")
    parent_host = _find_one(project, "ProcessHostPoolProductionHostProcess.cs")
    runtime_project = root / "implementations" / "dotnet" / "src" / "Multiplexed.AI"
    child_process = _find_one(runtime_project, "SystemAiRuntimeProcessPoolChild.cs")
    target_dir = project / "Validation" / "InteractiveSdk"
    target = target_dir / BRIDGE_SOURCE.name

    base_original = base.read_text(encoding="utf-8")
    base_patched = _inject_hook(
        _inject_auth(
            _inject_runtime_settings(base_original)
        )
    )

    composer_original = composer.read_text(encoding="utf-8")
    composer_patched = _inject_processhost_runtime_roles(composer_original)

    parent_original = parent_host.read_text(encoding="utf-8")
    parent_patched = _inject_parent_trace_forwarding(parent_original)

    child_original = child_process.read_text(encoding="utf-8")
    child_patched = _inject_child_runtime_trace(child_original)

    generic_original = generic_host.read_text(encoding="utf-8")
    generic_patched = _inject_interactive_sdk_jwt_handler(
        _inject_real_jwt_auth_bypass(generic_original)
    )

    target_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BRIDGE_SOURCE, target)
    if base_patched != base_original:
        base.write_text(base_patched, encoding="utf-8")
    if composer_patched != composer_original:
        composer.write_text(composer_patched, encoding="utf-8")
    if generic_patched != generic_original:
        generic_host.write_text(generic_patched, encoding="utf-8")
    if parent_patched != parent_original:
        parent_host.write_text(parent_patched, encoding="utf-8")
    if child_patched != child_original:
        child_process.write_text(child_patched, encoding="utf-8")

    print("ProcessHostPool bridge install: PASS")
    print(f"- base harness: {base.relative_to(root)}")
    print(f"- ProcessHost settings composer: {composer.relative_to(root)}")
    print(f"- generic MCP test host: {generic_host.relative_to(root)}")
    print(f"- ProcessHost trace forwarding: {parent_host.relative_to(root)}")
    print(f"- child runtime diagnostic drain: {child_process.relative_to(root)}")
    print(f"- bridge source: {target.relative_to(root)}")
    print("- FakeAuthHandler remains the default for existing tests")
    print("- real JWT authentication is registered only when Tests:UseRealJwtAuthentication=true")
    print("- dedicated InteractiveSdkJwtBearer performs real JWT validation on that gated path")
    print("- MatrixBearer remains registered and unchanged for historical matrix tests")
    print("- child stdout/stderr diagnostic tee is active only when MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE=1")
    print("- installed child snapshot diagnostic predicate and System.Environment qualification: VERIFIED")
    print("- normal production tests remain unchanged unless AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION=1")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
