from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEMO = HERE.parent
REPO = HERE.parents[2]
PROJECT = (
    REPO
    / "implementations"
    / "dotnet"
    / "Tests"
    / "Multiplexed.AI.McpServer.Tests.Integration"
    / "Multiplexed.AI.McpServer.Tests.Integration.csproj"
)
BOOTSTRAP = DEMO / "scripts" / "bootstrap.py"
PATCHER = HERE / "apply_processhostpool_bridge.py"
ENV_FILE = DEMO / ".env"

DEFAULT_AUTH = {
    "AiMcpAuthentication__Issuer": "multiplexed-local",
    "AiMcpAuthentication__Audience": "multiplexed-ai-sdk",
    "AiMcpAuthentication__SymmetricSigningKey": "local-demo-only-signing-key-please-change-2026",
    "AI_DEMO_PROJECT": "rbac-demo",
    "AI_DEMO_NAMESPACE": "default",
    "AI_DEMO_USER_ID": "interactive-agent-demo",
    "AI_DEMO_TENANT_ID": "interactive-agent-tenant",
    "AI_DEMO_TENANT_GROUP_ID": "interactive-agent-group",
    "AI_DEMO_TOKEN_TTL_SECONDS": "3600",
}

# Only these values are allowed to flow from demo/.env into the server-side
# ProcessHostPool harness. Provider/auth secrets are never copied wholesale.
ENV_ALLOWLIST = {
    "OPENAI_API_KEY",
    "OPENAI_MODEL",
    "AI_DEMO_JWT_ISSUER",
    "AI_DEMO_JWT_AUDIENCE",
    "AI_DEMO_SIGNING_KEY",
    "AI_DEMO_PROJECT",
    "AI_DEMO_NAMESPACE",
    "AI_DEMO_USER_ID",
    "AI_DEMO_TENANT_ID",
    "AI_DEMO_TENANT_GROUP_ID",
    "AI_DEMO_TOKEN_TTL_SECONDS",
}


def _run(
    command: list[str],
    *,
    env: dict[str, str] | None = None,
    capture: bool = False,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=REPO,
        env=env,
        text=True,
        capture_output=capture,
        check=False,
    )


def _read_env_file(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key not in ENV_ALLOWLIST:
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
            value = value[1:-1]
        if value:
            values[key] = value
    return values


def _server_environment(base: dict[str, str]) -> dict[str, str]:
    env = dict(base)
    file_values = _read_env_file(ENV_FILE)

    # Explicit shell values always win. The file is a local convenience only.
    for key, value in file_values.items():
        env.setdefault(key, value)

    # Map the demo names to the native host authentication configuration.
    env.setdefault(
        "AiMcpAuthentication__Issuer",
        env.get("AI_DEMO_JWT_ISSUER", DEFAULT_AUTH["AiMcpAuthentication__Issuer"]),
    )
    env.setdefault(
        "AiMcpAuthentication__Audience",
        env.get("AI_DEMO_JWT_AUDIENCE", DEFAULT_AUTH["AiMcpAuthentication__Audience"]),
    )
    env.setdefault(
        "AiMcpAuthentication__SymmetricSigningKey",
        env.get("AI_DEMO_SIGNING_KEY", DEFAULT_AUTH["AiMcpAuthentication__SymmetricSigningKey"]),
    )

    for name, default in DEFAULT_AUTH.items():
        env.setdefault(name, default)

    if len(env["AiMcpAuthentication__SymmetricSigningKey"].encode("utf-8")) < 32:
        raise RuntimeError("JWT signing key must be at least 32 UTF-8 bytes.")

    if not env.get("OPENAI_API_KEY", "").strip():
        raise RuntimeError(
            "OPENAI_API_KEY is required by the server-side interactive agent runtime. "
            "Set it in demo/interactive-agent-sdk/.env or the current shell."
        )

    # Normalize the exact values consumed by the local JWT helper.
    env["AI_DEMO_JWT_ISSUER"] = env["AiMcpAuthentication__Issuer"]
    env["AI_DEMO_JWT_AUDIENCE"] = env["AiMcpAuthentication__Audience"]
    env["AI_DEMO_SIGNING_KEY"] = env["AiMcpAuthentication__SymmetricSigningKey"]
    return env


def _prepare(build_configuration: str, env: dict[str, str]) -> None:
    completed = _run([sys.executable, str(BOOTSTRAP)], env=env)
    if completed.returncode != 0:
        raise RuntimeError(f"Consumer bootstrap failed ({completed.returncode}).")

    host_project = (
        REPO
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI.McpServer.Host"
        / "Multiplexed.AI.McpServer.Host.csproj"
    )
    for project in (host_project, PROJECT):
        completed = _run(
            ["dotnet", "build", str(project), "-c", build_configuration, "--nologo"],
            env=env,
        )
        if completed.returncode != 0:
            raise RuntimeError(f"Build failed ({completed.returncode}): {project}")


def _list_tests(build_configuration: str, env: dict[str, str]) -> list[str]:
    completed = _run(
        [
            "dotnet",
            "test",
            str(PROJECT),
            "-c",
            build_configuration,
            "--no-build",
            "--list-tests",
            "--nologo",
        ],
        env=env,
        capture=True,
    )
    if completed.returncode != 0:
        raise RuntimeError("Unable to list ProcessHostPool integration tests.\n" + completed.stderr)
    tests: list[str] = []
    in_tests = False
    for raw in completed.stdout.splitlines():
        line = raw.strip()
        if line.startswith("The following Tests are available"):
            in_tests = True
            continue
        if in_tests and line and not line.startswith(("Test run for", "VSTest version")):
            tests.append(line)
    return tests


def _pick_test(tests: list[str], transport: str, override: str | None) -> str:
    if override:
        matches = [t for t in tests if override.lower() in t.lower()]
        if len(matches) != 1:
            raise RuntimeError(
                f"--test-filter matched {len(matches)} tests; expected exactly one. Matches={matches[:10]}"
            )
        return matches[0]

    provider = "Http" if transport == "HTTP" else "Grpc"
    candidates = [
        t for t in tests if "ProcessHostPool" in t and provider.lower() in t.lower()
    ]
    if not candidates:
        raise RuntimeError(f"No {provider} ProcessHostPool test was found in --list-tests output.")

    def score(name: str) -> tuple[int, int, str]:
        lower = name.lower()
        points = 0
        if "(" not in name:
            points += 150
        for token, weight in (
            ("matrix_baseline", 100),
            ("baseline", 80),
            ("bounded", 60),
            ("warm", 30),
        ):
            if token in lower:
                points += weight
        for token, weight in (
            ("continuation", -20),
            ("failure", -15),
            ("crash", -15),
            ("eventdriven", -10),
        ):
            if token in lower:
                points += weight
        return (-points, len(name), name)

    return sorted(candidates, key=score)[0]


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run the real production ProcessHostPool setup and the automated 3-client interactive SDK matrix."
    )
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    parser.add_argument(
        "--test-filter",
        default=None,
        help="Optional unique substring selecting one ProcessHostPool production test.",
    )
    parser.add_argument(
        "--build-configuration",
        choices=("Debug", "Release"),
        default="Debug",
        help="Build configuration for the runtime host and integration harness (default: Debug).",
    )
    parser.add_argument("--skip-prepare", action="store_true")
    parser.add_argument("--skip-patch", action="store_true")
    args = parser.parse_args()

    try:
        env = _server_environment(os.environ.copy())
        print(f"[processhost-launcher] build configuration: {args.build_configuration}")
        print("[processhost-launcher] server OpenAI credential: configured; value not displayed")

        if not args.skip_patch:
            patch = _run(
                [sys.executable, str(PATCHER), "--repo-root", str(REPO)],
                env=env,
            )
            if patch.returncode != 0:
                raise RuntimeError(f"Bridge patch failed ({patch.returncode}).")

        if not args.skip_prepare:
            _prepare(args.build_configuration, env)

        tests = _list_tests(args.build_configuration, env)
        selected = _pick_test(tests, args.runtime_transport, args.test_filter)
        print(f"[processhost-launcher] selected production harness: {selected}")

        slug = args.runtime_transport.lower()
        inventory = HERE / "inventory" / f"processhostpool-{slug}.json"
        evidence = HERE / "evidence" / "processhostpool" / slug
        inventory.parent.mkdir(parents=True, exist_ok=True)
        evidence.mkdir(parents=True, exist_ok=True)

        env.update(
            {
                "AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION": "1",
                "AI_INTERACTIVE_SDK_RUNTIME_TRANSPORT": args.runtime_transport,
                "AI_INTERACTIVE_SDK_REPOSITORY_ROOT": str(REPO),
                "AI_INTERACTIVE_SDK_INVENTORY_PATH": str(inventory),
                "AI_INTERACTIVE_SDK_EVIDENCE_DIR": str(evidence),
                "AI_INTERACTIVE_SDK_PYTHON": sys.executable,
                "MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE": "1",
            }
        )

        completed = _run(
            [
                "dotnet",
                "test",
                str(PROJECT),
                "-c",
                args.build_configuration,
                "--no-build",
                "--nologo",
                "--filter",
                f"FullyQualifiedName={selected.split('(')[0]}",
                "--logger",
                "console;verbosity=normal",
            ],
            env=env,
        )
        if completed.returncode != 0:
            raise RuntimeError(
                f"ProcessHostPool interactive validation test failed ({completed.returncode})."
            )
    except RuntimeError as exc:
        print(f"[processhost-launcher] FAILED: {exc}", file=sys.stderr)
        return 1

    print(f"[processhost-launcher] PASS: ProcessHostPool/{args.runtime_transport} 3/3")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
