from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEMO = HERE.parent
REPO = HERE.parents[2]
ENV_FILE = DEMO / ".env"
RUNNER = REPO / "implementations" / "matrix" / "runtime" / "kubernetes" / "run-kubernetes-pool-sdk-execution.ps1"

DEFAULT_AUTH = {
    "AiMcpAuthentication__Issuer": "multiplexed-local",
    "AiMcpAuthentication__Audience": "multiplexed-ai-sdk",
    "AiMcpAuthentication__SymmetricSigningKey": "local-demo-only-signing-key-please-change-2026",
    "AI_DEMO_PROJECT": "matrix",
    "AI_DEMO_NAMESPACE": "default",
    "AI_DEMO_USER_ID": "interactive-agent-demo",
    "AI_DEMO_TENANT_ID": "matrix-tenant",
    "AI_DEMO_TENANT_GROUP_ID": "matrix-group",
    "AI_DEMO_TOKEN_TTL_SECONDS": "3600",
}

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


def _environment() -> dict[str, str]:
    env = os.environ.copy()
    for key, value in _read_env_file(ENV_FILE).items():
        env.setdefault(key, value)

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
    for key, value in DEFAULT_AUTH.items():
        env.setdefault(key, value)

    # Kubernetes external-SDK profile is intentionally scoped to the existing matrix tenant.
    # Pin local JWT claims to that exact scope instead of relying on the generic demo defaults.
    env["AI_DEMO_PROJECT"] = "matrix"
    env["AI_DEMO_NAMESPACE"] = "default"
    env["AI_DEMO_TENANT_ID"] = "matrix-tenant"
    env["AI_DEMO_TENANT_GROUP_ID"] = "matrix-group"
    env["AI_DEMO_JWT_ISSUER"] = env["AiMcpAuthentication__Issuer"]
    env["AI_DEMO_JWT_AUDIENCE"] = env["AiMcpAuthentication__Audience"]
    env["AI_DEMO_SIGNING_KEY"] = env["AiMcpAuthentication__SymmetricSigningKey"]
    env["AI_RUNTIME_ENDPOINT"] = "http://127.0.0.1:8081/mcp"
    env["AI_DEMO_VALIDATION_TIMEOUT_SECONDS"] = env.get("AI_DEMO_VALIDATION_TIMEOUT_SECONDS", "300")

    if len(env["AiMcpAuthentication__SymmetricSigningKey"].encode("utf-8")) < 32:
        raise RuntimeError("JWT signing key must contain at least 32 UTF-8 bytes.")
    if not env.get("OPENAI_API_KEY", "").strip():
        raise RuntimeError(
            "OPENAI_API_KEY is required by the KubernetesPool runtime. "
            "Set it in demo/interactive-agent-sdk/.env or the current shell."
        )
    if not env.get("OPENAI_MODEL", "").strip():
        raise RuntimeError(
            "OPENAI_MODEL is required by the interactive consumers. "
            "Set it in demo/interactive-agent-sdk/.env or the current shell."
        )
    return env


def _powershell() -> str:
    candidates = ("pwsh", "powershell.exe", "powershell")
    for name in candidates:
        resolved = shutil.which(name)
        if resolved:
            return resolved
    raise RuntimeError("PowerShell was not found on PATH (pwsh/powershell.exe).")


def main() -> int:
    parser = argparse.ArgumentParser(description="Launch the real KubernetesPool/HTTP interactive SDK 3-client validation.")
    parser.add_argument("--runtime-image", default="multiplexed-ai-runtime:matrix-sdk")
    parser.add_argument("--skip-image-build", action="store_true")
    parser.add_argument("--require-immutable-image", action="store_true")
    parser.add_argument("--skip-infrastructure-preflight", action="store_true")
    args = parser.parse_args()

    try:
        env = _environment()
        powershell = _powershell()
        command = [
            powershell,
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RUNNER),
            "-InteractiveAgent",
            "-RuntimeImage",
            args.runtime_image,
        ]
        if args.skip_image_build:
            command.append("-SkipImageBuild")
        if args.require_immutable_image:
            command.append("-RequireImmutableImage")
        if args.skip_infrastructure_preflight:
            command.append("-SkipInfrastructurePreflight")

        print("[kubernetes-launcher] topology: KubernetesPool/HTTP")
        print("[kubernetes-launcher] external SDK clients: .NET, TypeScript, Python")
        print("[kubernetes-launcher] server OpenAI credential: configured; value not displayed")
        completed = subprocess.run(command, cwd=REPO, env=env, check=False)
        if completed.returncode != 0:
            raise RuntimeError(f"KubernetesPool interactive validation failed ({completed.returncode}).")
    except RuntimeError as exc:
        print(f"[kubernetes-launcher] FAILED: {exc}", file=sys.stderr)
        return 1

    print("[kubernetes-launcher] PASS: KubernetesPool/HTTP 3/3")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
