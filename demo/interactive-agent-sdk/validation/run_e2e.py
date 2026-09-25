from __future__ import annotations

import argparse
import os
import subprocess
import sys
import json
from pathlib import Path
from typing import Mapping
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
LAUNCHER = ROOT / "launcher" / "launcher.py"
VALIDATOR = Path(__file__).with_name("validate_evidence.py")
JWT_HELPER = ROOT / "scripts" / "create-local-jwt.py"
BOOTSTRAP = ROOT / "scripts" / "bootstrap.py"
SDKS = ("dotnet", "typescript", "python")

# Only these local-demo values may be read from the demo .env file by the
# validation orchestrator. Provider credentials such as OPENAI_API_KEY are
# deliberately excluded and are never injected into external SDK consumers.
LOCAL_AUTH_ENV_KEYS = (
    "AI_DEMO_JWT_ISSUER",
    "AI_DEMO_JWT_AUDIENCE",
    "AI_DEMO_SIGNING_KEY",
    "AI_DEMO_PROJECT",
    "AI_DEMO_NAMESPACE",
    "AI_DEMO_USER_ID",
    "AI_DEMO_TENANT_ID",
    "AI_DEMO_TENANT_GROUP_ID",
    "AI_DEMO_TOKEN_TTL_SECONDS",
)

DEFAULT_LOCAL_AUTH = {
    "AI_DEMO_JWT_ISSUER": "multiplexed-local",
    "AI_DEMO_JWT_AUDIENCE": "multiplexed-ai-sdk",
    "AI_DEMO_SIGNING_KEY": "local-demo-only-signing-key-please-change-2026",
    "AI_DEMO_PROJECT": "rbac-demo",
    "AI_DEMO_NAMESPACE": "default",
    "AI_DEMO_USER_ID": "interactive-agent-demo",
    "AI_DEMO_TENANT_ID": "interactive-agent-tenant",
    "AI_DEMO_TENANT_GROUP_ID": "interactive-agent-group",
    "AI_DEMO_TOKEN_TTL_SECONDS": "3600",
}


def _read_env_file(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.is_file():
        return values

    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key not in LOCAL_AUTH_ENV_KEYS:
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ('"', "'"):
            value = value[1:-1]
        values[key] = value
    return values


def _first_nonempty(*values: str | None) -> str | None:
    for value in values:
        if value is not None and value.strip():
            return value.strip()
    return None


def _local_auth_settings(env_file: Path) -> dict[str, str]:
    file_values = _read_env_file(env_file)
    current = os.environ

    values: dict[str, str] = {}
    for key in LOCAL_AUTH_ENV_KEYS:
        values[key] = _first_nonempty(
            current.get(key),
            file_values.get(key),
            DEFAULT_LOCAL_AUTH.get(key),
        ) or ""

    # Native host names take precedence when explicitly configured.
    values["AI_DEMO_JWT_ISSUER"] = _first_nonempty(
        current.get("AiMcpAuthentication__Issuer"),
        values["AI_DEMO_JWT_ISSUER"],
    ) or ""
    values["AI_DEMO_JWT_AUDIENCE"] = _first_nonempty(
        current.get("AiMcpAuthentication__Audience"),
        values["AI_DEMO_JWT_AUDIENCE"],
    ) or ""
    values["AI_DEMO_SIGNING_KEY"] = _first_nonempty(
        current.get("AiMcpAuthentication__SymmetricSigningKey"),
        values["AI_DEMO_SIGNING_KEY"],
    ) or ""
    return values




def _docker_runtime_auth_settings() -> dict[str, str] | None:
    """Return effective JWT auth settings from the running local demo runtime container.

    Values are kept in-process only and are never printed or forwarded to SDK consumers.
    """
    compose_file = ROOT / "compose.yaml"
    try:
        ps = subprocess.run(
            ["docker", "compose", "-f", str(compose_file), "ps", "-q", "runtime"],
            cwd=ROOT,
            text=True,
            capture_output=True,
            check=False,
        )
    except (OSError, FileNotFoundError):
        return None

    container_id = ps.stdout.strip() if ps.returncode == 0 else ""
    if not container_id:
        return None

    inspect = subprocess.run(
        ["docker", "inspect", container_id, "--format", "{{json .Config.Env}}"],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    if inspect.returncode != 0 or not inspect.stdout.strip():
        return None

    try:
        entries = json.loads(inspect.stdout)
    except json.JSONDecodeError:
        return None

    env: dict[str, str] = {}
    for entry in entries if isinstance(entries, list) else []:
        if isinstance(entry, str) and "=" in entry:
            key, value = entry.split("=", 1)
            env[key] = value

    issuer = env.get("AiMcpAuthentication__Issuer", "").strip()
    audience = env.get("AiMcpAuthentication__Audience", "").strip()
    signing_key = env.get("AiMcpAuthentication__SymmetricSigningKey", "")
    if not issuer or not audience or len(signing_key.encode("utf-8")) < 32:
        return None

    return {
        "AI_DEMO_JWT_ISSUER": issuer,
        "AI_DEMO_JWT_AUDIENCE": audience,
        "AI_DEMO_SIGNING_KEY": signing_key,
    }

def _create_local_token(env_file: Path, *, prefer_running_docker_runtime: bool = False) -> tuple[str, str]:
    settings = _local_auth_settings(env_file)
    source = "local demo auth configuration"

    if prefer_running_docker_runtime:
        docker_settings = _docker_runtime_auth_settings()
        if docker_settings is not None:
            settings.update(docker_settings)
            source = "running Docker runtime auth configuration"
    signing_key = settings["AI_DEMO_SIGNING_KEY"]
    if len(signing_key.encode("utf-8")) < 32:
        raise RuntimeError(
            "Local JWT signing key is missing or shorter than 32 UTF-8 bytes. "
            "Set AI_DEMO_SIGNING_KEY in demo/interactive-agent-sdk/.env, "
            "or provide AI_RUNTIME_TOKEN explicitly."
        )

    helper_env = os.environ.copy()
    helper_env.update(
        {
            "AiMcpAuthentication__Issuer": settings["AI_DEMO_JWT_ISSUER"],
            "AiMcpAuthentication__Audience": settings["AI_DEMO_JWT_AUDIENCE"],
            "AiMcpAuthentication__SymmetricSigningKey": signing_key,
            "AI_DEMO_PROJECT": settings["AI_DEMO_PROJECT"],
            "AI_DEMO_NAMESPACE": settings["AI_DEMO_NAMESPACE"],
            "AI_DEMO_USER_ID": settings["AI_DEMO_USER_ID"],
            "AI_DEMO_TENANT_ID": settings["AI_DEMO_TENANT_ID"],
            "AI_DEMO_TENANT_GROUP_ID": settings["AI_DEMO_TENANT_GROUP_ID"],
            "AI_DEMO_TOKEN_TTL_SECONDS": settings["AI_DEMO_TOKEN_TTL_SECONDS"],
        }
    )

    token = subprocess.check_output(
        [sys.executable, str(JWT_HELPER)],
        cwd=ROOT,
        env=helper_env,
        text=True,
    ).strip()
    if not token:
        raise RuntimeError("Local JWT helper returned an empty token.")
    return token, source


def _runtime_endpoint() -> str:
    return os.getenv("AI_RUNTIME_ENDPOINT", "http://localhost:8081/mcp").strip()


def _is_local_runtime_endpoint(endpoint: str) -> bool:
    try:
        host = (urlparse(endpoint).hostname or "").lower()
    except ValueError:
        return False
    return host in {"localhost", "127.0.0.1", "::1"}


def _resolve_token(auth_mode: str, env_file: Path) -> tuple[str, str]:
    configured = os.getenv("AI_RUNTIME_TOKEN", "").strip()

    if auth_mode == "token":
        if not configured:
            raise RuntimeError(
                "--auth-mode token requires AI_RUNTIME_TOKEN to be set."
            )
        return configured, "AI_RUNTIME_TOKEN (explicit token mode)"

    if auth_mode == "local-jwt":
        token, source = _create_local_token(
            env_file,
            prefer_running_docker_runtime=_is_local_runtime_endpoint(_runtime_endpoint()),
        )
        return token, f"local JWT helper ({source}; explicit local-jwt mode)"

    endpoint = _runtime_endpoint()
    if _is_local_runtime_endpoint(endpoint):
        # Local validation must not silently reuse a stale bearer inherited from
        # the parent shell. Generate a fresh token against the local demo auth
        # configuration on every run.
        token, source = _create_local_token(
            env_file,
            prefer_running_docker_runtime=True,
        )
        return token, f"local JWT helper ({source}; auto/local endpoint)"

    if configured:
        return configured, "AI_RUNTIME_TOKEN (auto/remote endpoint)"

    raise RuntimeError(
        "Automatic authentication for a non-local runtime requires AI_RUNTIME_TOKEN. "
        "Set the token or use --auth-mode local-jwt explicitly when the remote runtime "
        "is intentionally configured with the same local-demo signing authority."
    )




def _prepare_consumers() -> None:
    env = os.environ.copy()
    for secret_name in (
        "OPENAI_API_KEY",
        "AiMcpAuthentication__SymmetricSigningKey",
        "AI_DEMO_SIGNING_KEY",
        "AI_RUNTIME_TOKEN",
        "AI_RUNTIME_ACCESS_CONTEXT",
    ):
        env.pop(secret_name, None)

    print("[validation] preparing local SDK packages and consumer artifacts")
    completed = subprocess.run(
        [sys.executable, str(BOOTSTRAP)],
        cwd=ROOT,
        env=env,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"Consumer preparation failed with exit code {completed.returncode}."
        )
    print("[validation] consumer artifacts: READY")

def _consumer_environment(base: Mapping[str, str], token: str) -> dict[str, str]:
    env = dict(base)

    # External SDK consumers must never inherit server/provider secrets from the
    # shell running the validation harness.
    for secret_name in (
        "OPENAI_API_KEY",
        "AiMcpAuthentication__SymmetricSigningKey",
        "AI_DEMO_SIGNING_KEY",
    ):
        env.pop(secret_name, None)

    env["AI_RUNTIME_TOKEN"] = token
    return env


def main() -> int:
    parser = argparse.ArgumentParser(description="Run automated interactive-agent public-SDK E2E validation.")
    parser.add_argument("--sdk", choices=(*SDKS, "all"), default="all")
    parser.add_argument("--runtime-provider", choices=("ProcessHostPool", "KubernetesPool"), required=True)
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    parser.add_argument("--evidence-dir", type=Path, default=ROOT / "validation" / "evidence")
    parser.add_argument("--prompt", default="Validate the interactive agent topology through the public SDK boundary.")
    parser.add_argument(
        "--auth-mode",
        choices=("auto", "token", "local-jwt"),
        default="auto",
        help=(
            "auto: generate a fresh local JWT for localhost/127.0.0.1/::1; "
            "for non-local endpoints require AI_RUNTIME_TOKEN; token: require and use "
            "AI_RUNTIME_TOKEN; local-jwt: always generate a local demo JWT"
        ),
    )
    parser.add_argument(
        "--env-file",
        type=Path,
        default=ROOT / ".env",
        help="Local demo .env used only for JWT issuer/audience/signing settings.",
    )
    parser.add_argument(
        "--skip-prepare",
        action="store_true",
        help="Reuse already prepared consumer artifacts instead of rebuilding local SDK packages/consumers.",
    )
    args = parser.parse_args()

    selected = SDKS if args.sdk == "all" else (args.sdk,)
    args.evidence_dir.mkdir(parents=True, exist_ok=True)

    try:
        if not args.skip_prepare:
            _prepare_consumers()
        token, token_source = _resolve_token(args.auth_mode, args.env_file)
    except (RuntimeError, subprocess.CalledProcessError) as exc:
        print(f"[validation] authentication setup FAILED: {exc}", file=sys.stderr)
        return 1

    print(f"[validation] authentication: {token_source}; token not displayed")

    failures = 0
    for sdk in selected:
        filename = f"{args.runtime_provider.lower()}-{args.runtime_transport.lower()}-{sdk}.json"
        evidence = args.evidence_dir / filename
        env = _consumer_environment(os.environ, token)
        env.update({
            "AI_DEMO_AUTOMATED_VALIDATION": "1",
            "AI_DEMO_EVIDENCE_PATH": str(evidence),
            "AI_VALIDATION_RUNTIME_PROVIDER": args.runtime_provider,
            "AI_VALIDATION_RUNTIME_TRANSPORT": args.runtime_transport,
            "AI_DEMO_VALIDATION_PROMPT": args.prompt,
        })

        print(f"[validation] {sdk}: {args.runtime_provider}/{args.runtime_transport}")
        run = subprocess.run(
            [sys.executable, str(LAUNCHER), "--sdk", sdk],
            cwd=ROOT,
            env=env,
        )
        if run.returncode != 0:
            print(f"[validation] {sdk}: execution FAILED ({run.returncode})")
            failures += 1
            continue
        if not evidence.exists():
            print(f"[validation] {sdk}: evidence file missing: {evidence}")
            failures += 1
            continue

        check = subprocess.run([sys.executable, str(VALIDATOR), str(evidence)])
        if check.returncode != 0:
            failures += 1
        else:
            print(f"[validation] {sdk}: PASS -> {evidence}")

    return 0 if failures == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
