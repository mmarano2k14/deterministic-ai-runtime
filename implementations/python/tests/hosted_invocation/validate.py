"""Run the Python suite and opt-in .NET/Python integration without silent skips."""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python-only", action="store_true", help="Run Python only; this does not validate the .NET integration.")
    args = parser.parse_args()
    if sys.implementation.name != "cpython" or sys.version_info[:2] not in ((3, 12), (3, 13)):
        print("Use a stable CPython 3.12.x or 3.13.x interpreter.", file=sys.stderr)
        return 2
    root = Path(__file__).resolve().parents[4]
    tests = Path(__file__).resolve().parent
    result = subprocess.run([sys.executable, "-B", "-m", "unittest", "discover", "-s", str(tests),
                             "-p", "test_*.py", "-v"], cwd=root, check=False)
    if result.returncode != 0:
        return result.returncode
    if args.python_only:
        print("Python tests completed. .NET build and integration tests were NOT executed.", flush=True)
        return 0
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        print("The .NET SDK is unavailable. .NET validation did not run.", file=sys.stderr)
        return 2
    project = root / "implementations/dotnet/Tests/Multiplexed.AI.Tests/Multiplexed.AI.Tests.csproj"
    environment = dict(os.environ)
    # Opt in with this exact interpreter. Neither production profiles nor host configuration are changed.
    environment["MULTIPLEXED_PYTHON_EXECUTABLE"] = str(Path(sys.executable).absolute())
    with tempfile.TemporaryDirectory(prefix="hosted-python-validation-") as output:
        result = subprocess.run([dotnet, "test", str(project), "--filter",
                                 "FullyQualifiedName~Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python",
                                 "--logger", "trx;LogFileName=hosted-python.trx", "--results-directory", output],
                                cwd=root, env=environment, check=False)
        if result.returncode != 0:
            return result.returncode
        path = Path(output, "hosted-python.trx")
        if not path.is_file():
            print("No fresh .NET test report was produced; validation failed.", file=sys.stderr)
            return 2
        document = ET.parse(path).getroot()
        counters = document.find(".//{*}Counters")
        if counters is None:
            print("No .NET test counters were returned.", file=sys.stderr)
            return 2
        total = int(counters.get("total", "0"))
        passed = int(counters.get("passed", "0"))
        executed = int(counters.get("executed", "0"))
        required = ("Published_Synchronous_Function_Returns_Real_Computed_Data",
                    "Unstarted_Python_Function_Executes_Original_Code_After_Republication",
                    "Python_Exception_Retains_Uncertain_Invocation_Without_Manufacturing_Business_Failure")
        names = [r.get("testName", "") for r in document.findall(".//{*}UnitTestResult") if r.get("outcome") == "Passed"]
        if total <= 0 or passed != total or executed != total or not all(any(n in name for name in names) for n in required):
            print("Incomplete .NET/Python coverage: zero tests, skipped tests, failures or missing live scenarios.", file=sys.stderr)
            return 2
        print(f"Validated: {passed} .NET cases passed, including real Python and pinned local DAG scenarios; no skipped cases.", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
