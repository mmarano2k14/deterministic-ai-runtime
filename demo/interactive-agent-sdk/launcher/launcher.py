from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

SDK_ENVIRONMENT_REFS = {
    "dotnet": "AI_RUNTIME_DOTNET_ENVIRONMENT_REF",
    "typescript": "AI_RUNTIME_TYPESCRIPT_ENVIRONMENT_REF",
    "python": "AI_RUNTIME_PYTHON_ENVIRONMENT_REF",
}

DISPLAY_NAMES = {
    "dotnet": ".NET",
    "typescript": "TypeScript",
    "python": "Python",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Interactive external-SDK demo launcher."
    )
    parser.add_argument(
        "--sdk",
        choices=tuple(SDK_ENVIRONMENT_REFS),
        help="Skip the menu and launch one SDK consumer directly.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print the selected command without starting the consumer.",
    )
    return parser.parse_args()


def choose_sdk() -> str:
    print("==================================================")
    print(" Deterministic AI Runtime - Interactive SDK Agent")
    print("==================================================")
    print()
    print("Choose SDK:")
    print()
    print("  1. .NET")
    print("  2. TypeScript")
    print("  3. Python")
    print()

    choices = {"1": "dotnet", "2": "typescript", "3": "python"}
    while True:
        selection = input("> ").strip()
        sdk = choices.get(selection)
        if sdk is not None:
            return sdk
        print("Choose 1, 2, or 3.")


def require_configuration(sdk: str) -> None:
    required = {
        "AI_RUNTIME_ENDPOINT": os.getenv("AI_RUNTIME_ENDPOINT", "").strip(),
        SDK_ENVIRONMENT_REFS[sdk]: os.getenv(SDK_ENVIRONMENT_REFS[sdk], "").strip(),
    }
    missing = [name for name, value in required.items() if not value]
    if missing:
        names = ", ".join(missing)
        raise SystemExit(
            f"Missing required demo configuration for {DISPLAY_NAMES[sdk]}: {names}"
        )


def command_for(sdk: str) -> list[str]:
    if sdk == "dotnet":
        return [
            "dotnet",
            "run",
            "--project",
            str(ROOT / "dotnet" / "InteractiveAgentSdkDemo.csproj"),
            "--configuration",
            "Release",
            "--no-build",
        ]

    if sdk == "typescript":
        return ["node", str(ROOT / "typescript" / "dist" / "index.js")]

    if sdk == "python":
        if os.name == "nt":
            python = ROOT / "python" / ".venv" / "Scripts" / "python.exe"
        else:
            python = ROOT / "python" / ".venv" / "bin" / "python"
        return [str(python), "-m", "interactive_agent_sdk_demo"]

    raise ValueError(f"Unsupported SDK '{sdk}'.")


def main() -> int:
    args = parse_args()
    sdk = args.sdk or choose_sdk()
    require_configuration(sdk)

    command = command_for(sdk)
    print()
    print(f"Selected SDK: {DISPLAY_NAMES[sdk]}")
    print("Consumer command:")
    print("  " + " ".join(command))
    print()

    if args.dry_run:
        return 0

    completed = subprocess.run(command, cwd=ROOT, check=False)
    return completed.returncode


if __name__ == "__main__":
    raise SystemExit(main())
