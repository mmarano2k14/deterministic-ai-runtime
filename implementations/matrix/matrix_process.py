from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path


def run_scenario_process(command: list[str], *, cwd: Path, log_path: Path) -> None:
    """Run once, preserve merged native output, and fail with its actual exit status.

    No retries, credentials, manifest contents or success evidence are synthesized here.
    stderr shares the stdout pipe so neither stream can fill while the other is drained.
    """
    log_path.parent.mkdir(parents=True, exist_ok=True)
    print(f"[matrix-client] full output: {log_path}", flush=True)
    with log_path.open("w", encoding="utf-8", newline="\n") as log:
        try:
            process = subprocess.Popen(
                command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, encoding="utf-8", errors="replace", bufsize=1,
            )
        except OSError as exception:
            log.write(f"[matrix-client] START FAILED {type(exception).__name__}: {exception}\n")
            raise
        try:
            assert process.stdout is not None
            for line in process.stdout:
                log.write(line)
                log.flush()
                try:
                    sys.stdout.write(line)
                    sys.stdout.flush()
                except (OSError, UnicodeError):
                    # A console encoding/pipe failure must not discard the saved native output.
                    pass
            return_code = process.wait()
            status = f"[matrix-client] EXIT code={return_code} hex=0x{return_code & 0xffffffff:08X}"
            log.write(status + "\n")
            log.flush()
        finally:
            if process.poll() is None:
                # Only stop the exact process tree started by this invocation.
                if os.name == "nt":
                    try:
                        subprocess.run(
                            ["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                            check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            timeout=10,
                        )
                    except (OSError, subprocess.TimeoutExpired):
                        pass
                if process.poll() is None:
                    process.kill()
                process.wait()
            if process.stdout is not None:
                process.stdout.close()
    print(status, flush=True)
    if return_code != 0:
        print(f"[matrix-client] FAILED; native exception retained in {log_path}", file=sys.stderr, flush=True)
        raise subprocess.CalledProcessError(return_code, command)
