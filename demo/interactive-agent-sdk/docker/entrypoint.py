from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen

APP_ROOT = Path(os.environ.get("AI_DEMO_PACKAGED_ROOT", "/opt/interactive-agent"))


def _wait_for_runtime() -> None:
    endpoint = os.getenv(
        "AI_RUNTIME_HEALTH_ENDPOINT",
        "http://runtime:8081/health",
    ).strip()
    timeout_seconds = int(os.getenv("AI_DEMO_RUNTIME_WAIT_SECONDS", "120"))
    deadline = time.monotonic() + max(timeout_seconds, 1)

    print(f"[demo] Waiting for runtime health: {endpoint}")
    last_error = "not ready"
    while time.monotonic() < deadline:
        try:
            request = Request(endpoint, method="GET")
            with urlopen(request, timeout=3) as response:
                if 200 <= response.status < 300:
                    print("[demo] Runtime is ready.")
                    return
                last_error = f"HTTP {response.status}"
        except (OSError, URLError) as exc:
            last_error = str(exc)
        time.sleep(1)

    raise RuntimeError(
        f"Runtime did not become healthy within {timeout_seconds}s: {last_error}"
    )


def _ensure_token() -> None:
    if os.getenv("AI_RUNTIME_TOKEN", "").strip():
        return

    helper = APP_ROOT / "scripts" / "create-local-jwt.py"
    result = subprocess.run(
        [sys.executable, str(helper)],
        check=True,
        capture_output=True,
        text=True,
        env=os.environ,
    )
    token = result.stdout.strip()
    if not token:
        raise RuntimeError("Local JWT helper returned an empty token.")

    os.environ["AI_RUNTIME_TOKEN"] = token
    print("[demo] Local JWT created for the Docker demo. Token not displayed.")


def main() -> int:
    _wait_for_runtime()
    _ensure_token()

    launcher = APP_ROOT / "launcher" / "launcher.py"
    os.execvpe(
        sys.executable,
        [sys.executable, str(launcher), *sys.argv[1:]],
        os.environ,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"demo-entrypoint: {exc}", file=sys.stderr)
        raise SystemExit(1)
