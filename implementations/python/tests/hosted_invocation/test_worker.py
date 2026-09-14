"""Standard-library tests for the real published-source worker, not a protocol probe."""
from __future__ import annotations

import base64
import copy
import datetime as dt
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

WORKER = Path(__file__).resolve().parents[2] / "workers" / "hosted_invocation" / "worker.py"
_spec = importlib.util.spec_from_file_location("_hosted_worker_test_target", WORKER)
assert _spec and _spec.loader
loader = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = loader
_spec.loader.exec_module(loader)
VERSION = ".".join(map(str, sys.version_info[:3]))
RUNTIME = {"reference": "python-fixed", "executionLanguage": "python", "runtimeVersion": VERSION,
           "runtimeSha256": "a" * 64}
SIMPLE = 'def run(inputs, context):\n    return {"success": True, "payload": {"value": inputs["amount"] * 2}}\n'


def file(path: str, source: str | bytes) -> dict:
    raw = source.encode("utf-8") if isinstance(source, str) else source
    return {"path": path, "sha256": hashlib.sha256(raw).hexdigest(), "sizeBytes": len(raw),
            "base64Url": base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")}


def request(source: str = SIMPLE) -> dict:
    return {"protocolVersion": 1, "type": "invoke", "requestId": "req-a", "operationId": "op-a",
            "effectIdempotencyKey": "effect-a", "workerId": "worker-a", "epoch": 1, "tenantId": "tenant-a",
            "executionId": "execution-a", "stepName": "analyze", "generation": 0,
            "deadlineUtc": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=20)).isoformat(),
            "traceParent": None, "inputs": {"amount": 21}, "code": {
                "target": {"pipelineName": "analysis", "pipelineVersion": "1", "definitionSha256": "b" * 64,
                           "publicationRef": "publication-a", "publicationSha256": "c" * 64,
                           "implementationRef": "implementation-a", "implementationSha256": "d" * 64,
                           "executionLanguage": "python", "environmentRef": "env-a", "environmentSha256": "e" * 64},
                "runtime": dict(RUNTIME), "entryPointPath": "main.py", "entryPointSymbol": "run",
                "sources": [file("main.py", source)], "dependencies": []}}


def execute(value: dict | bytes, *, version: str = VERSION, heartbeat: int = 100,
            cwd: str | None = None, extra_environment: dict | None = None,
            flags: list[str] | None = None, timeout: float = 8) -> subprocess.CompletedProcess:
    arguments = [sys.executable, *(flags if flags is not None else ["-I", "-S", "-B", "-u", "-X", "utf8"]),
                 str(WORKER), "--runtime-reference", RUNTIME["reference"], "--runtime-version", version,
                 "--runtime-sha256", RUNTIME["runtimeSha256"], "--heartbeat-ms", str(heartbeat)]
    environment = {"SystemRoot": os.environ["SystemRoot"]} if os.name == "nt" else {}
    environment.update(extra_environment or {})
    body = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=True, allow_nan=False).encode() + b"\n"
    return subprocess.run(arguments, input=body, capture_output=True, timeout=timeout,
                          env=environment, cwd=cwd, check=False)


def frames(result: subprocess.CompletedProcess) -> list[dict]:
    return [json.loads(line) for line in result.stdout.splitlines()]


class WorkerExecutionTests(unittest.TestCase):
    def assert_success(self, result: subprocess.CompletedProcess) -> dict:
        self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
        output = frames(result)
        self.assertEqual("ready", output[0]["type"])
        self.assertEqual("result", output[-1]["type"])
        self.assertEqual(1, sum(x["type"] == "result" for x in output))
        self.assertTrue(all(x["type"] == "heartbeat" for x in output[1:-1]))
        return output[-1]

    def assert_technical(self, result: subprocess.CompletedProcess) -> None:
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any(x["type"] == "result" for x in frames(result)))

    def test_real_source_executes_in_a_child(self) -> None:
        value = self.assert_success(execute(request()))
        self.assertEqual({"value": 42}, value["payload"])
        self.assertTrue(value["success"])

    def test_async_function_is_awaited(self) -> None:
        source = 'import asyncio\nasync def run(inputs, context):\n    await asyncio.sleep(0.03)\n    return {"success": True, "payload": inputs["amount"] + 1}\n'
        self.assertEqual(22, self.assert_success(execute(request(source)))["payload"])

    def test_regular_package_and_relative_import(self) -> None:
        value = request()
        value["code"]["entryPointPath"] = "app/main.py"
        value["code"]["sources"] = [file("app/__init__.py", ""), file("app/main.py",
            'from .numbers import transform\ndef run(inputs, context):\n    return {"success": True, "payload": transform(inputs["amount"])}'),
            file("app/numbers.py", 'def transform(n):\n    return n * 2')]
        self.assertEqual(42, self.assert_success(execute(value))["payload"])

    def test_vendored_dependency_bytes_are_executed(self) -> None:
        value = request('from vendored_rules import transform\ndef run(inputs, context):\n    return {"success": True, "payload": transform(inputs["amount"])}')
        value["code"]["dependencies"] = [{"name": "rules", "version": "2.0.1", "files": [
            file("vendored_rules/__init__.py", "from .maths import transform"),
            file("vendored_rules/maths.py", "def transform(n):\n    return n * 3")]}]
        self.assertEqual(63, self.assert_success(execute(value))["payload"])

    def test_explicit_business_failure_is_a_result(self) -> None:
        value = self.assert_success(execute(request('def run(inputs, context):\n    return {"success": False, "payload": {"reason": "limit"}}')))
        self.assertFalse(value["success"])
        self.assertEqual({"reason": "limit"}, value["payload"])

    def test_null_payload_is_preserved(self) -> None:
        self.assertIsNone(self.assert_success(execute(request('def run(inputs, context):\n    return {"success": True, "payload": None}')))["payload"])

    def test_nested_input_and_unicode_are_preserved(self) -> None:
        value = request('def run(inputs, context):\n    return {"success": True, "payload": inputs}')
        value["inputs"] = {"name": "Liège ไทย 😀", "values": [None, False, 2.5]}
        self.assertEqual(value["inputs"], self.assert_success(execute(value))["payload"])

    def test_all_stdout_diagnostics_are_outside_the_protocol(self) -> None:
        source = 'import os, sys\nprint("import diagnostic")\ndef run(inputs, context):\n    print("function diagnostic")\n    sys.__stdout__.write("original stdout diagnostic\\n")\n    os.write(1, b"descriptor diagnostic\\n")\n    return {"success": True, "payload": 42}'
        value = execute(request(source))
        self.assert_success(value)
        for word in (b"import diagnostic", b"function diagnostic", b"original stdout diagnostic", b"descriptor diagnostic"):
            self.assertIn(word, value.stderr)
            self.assertNotIn(word, value.stdout)

    def test_context_contains_only_portable_metadata(self) -> None:
        source = 'def run(inputs, context):\n    value = dict(context)\n    value["target"] = dict(value["target"])\n    return {"success": True, "payload": value}'
        value = request(source)
        context = self.assert_success(execute(value))["payload"]
        self.assertEqual(value["operationId"], context["operationId"])
        self.assertEqual(value["effectIdempotencyKey"], context["effectIdempotencyKey"])
        self.assertEqual(value["code"]["target"], context["target"])
        self.assertNotIn("lease", context)
        self.assertNotIn("permissions", context)
        self.assertNotIn("code", context)

    def test_context_is_read_only(self) -> None:
        source = 'def run(inputs, context):\n    context["tenantId"] = "other"\n    return {"success": True, "payload": 0}'
        self.assert_technical(execute(request(source)))

    def test_global_state_does_not_leak_between_assignments(self) -> None:
        source = 'counter = 0\ndef run(inputs, context):\n    global counter\n    counter += 1\n    return {"success": True, "payload": counter}'
        self.assertEqual(1, self.assert_success(execute(request(source)))["payload"])
        self.assertEqual(1, self.assert_success(execute(request(source)))["payload"])

    def test_heartbeat_frames_are_correlated_while_function_runs(self) -> None:
        value = request('import time\ndef run(inputs, context):\n    time.sleep(0.4)\n    return {"success": True, "payload": 1}')
        result = execute(value, heartbeat=50)
        self.assert_success(result)
        output = frames(result)
        self.assertGreaterEqual(sum(x["type"] == "heartbeat" for x in output), 3)
        for frame in output:
            for key in ("requestId", "operationId", "workerId", "epoch"):
                self.assertEqual(value[key], frame[key])

    def test_sync_deadline_prevents_late_result(self) -> None:
        value = request('def run(inputs, context):\n    while True:\n        pass')
        value["deadlineUtc"] = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=1)).isoformat()
        started = time.monotonic()
        result = execute(value, heartbeat=50)
        self.assertEqual(124, result.returncode)
        self.assertLess(time.monotonic() - started, 5)
        self.assertFalse(any(x["type"] == "result" for x in frames(result)))

    def test_async_deadline_prevents_late_result(self) -> None:
        value = request('import asyncio\nasync def run(inputs, context):\n    await asyncio.sleep(10)\n    return {"success": True, "payload": 1}')
        value["deadlineUtc"] = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=1)).isoformat()
        result = execute(value, heartbeat=50)
        self.assertEqual(124, result.returncode)
        self.assertFalse(any(x["type"] == "result" for x in frames(result)))

    def test_source_exception_is_not_a_business_failure(self) -> None:
        result = execute(request('def run(inputs, context):\n    raise ValueError("SECRET_INPUT")'))
        self.assert_technical(result)
        self.assertNotIn(b"SECRET_INPUT", result.stderr)
        self.assertNotIn(b"Traceback", result.stderr)

    def test_system_exit_cannot_be_mistaken_for_a_result(self) -> None:
        self.assert_technical(execute(request('import sys\ndef run(inputs, context):\n    sys.exit(0)')))

    def test_wrong_signature_is_technical(self) -> None:
        self.assert_technical(execute(request('def run():\n    return {"success": True, "payload": 1}')))

    def test_missing_symbol_is_technical(self) -> None:
        self.assert_technical(execute(request('def other(inputs, context):\n    return {"success": True, "payload": 1}')))

    def test_imported_function_cannot_replace_declared_entry(self) -> None:
        value = request("from helper import run")
        value["code"]["sources"].append(file("helper.py", SIMPLE))
        self.assert_technical(execute(value))

    def test_unknown_dependency_does_not_trigger_installation(self) -> None:
        self.assert_technical(execute(request('import missing_published_dependency_xyz\n' + SIMPLE)))

    def test_site_and_pythonpath_are_not_used(self) -> None:
        with tempfile.TemporaryDirectory() as path:
            Path(path, "ambient_dependency.py").write_text("value = 42")
            source = 'import ambient_dependency\n' + SIMPLE
            self.assert_technical(execute(request(source), cwd=path,
                                         extra_environment={"PYTHONPATH": path, "PYTHONHOME": "missing"}))

    def test_no_module_files_are_extracted_or_cached(self) -> None:
        with tempfile.TemporaryDirectory() as path:
            value = request('def run(inputs, context):\n    return {"success": True, "payload": __file__}')
            origin = self.assert_success(execute(value, cwd=path))["payload"]
            self.assertTrue(origin.startswith("publication://"))
            self.assertEqual([], list(Path(path).rglob("*")))

    def test_reassignment_preserves_effect_identity_not_worker_identity(self) -> None:
        source = 'def run(inputs, context):\n    return {"success": True, "payload": {"effect": context["effectIdempotencyKey"], "worker": context["workerId"], "epoch": context["epoch"]}}'
        first = request(source)
        second = copy.deepcopy(first)
        second.update(workerId="worker-b", epoch=2, requestId="req-b")
        a = self.assert_success(execute(first))["payload"]
        b = self.assert_success(execute(second))["payload"]
        self.assertEqual(a["effect"], b["effect"])
        self.assertNotEqual(a["worker"], b["worker"])
        self.assertEqual(2, b["epoch"])

    def test_no_atexit_callback_after_terminal_result(self) -> None:
        source = 'import atexit\natexit.register(lambda: print("late callback"))\n' + SIMPLE
        value = execute(request(source))
        self.assert_success(value)
        self.assertNotIn(b"late callback", value.stderr)

    def test_missing_isolation_flags_fail_closed(self) -> None:
        self.assert_technical(execute(request(), flags=["-B", "-u", "-X", "utf8"]))

    def test_wrong_installed_version_fails_before_ready(self) -> None:
        result = execute(request(), version="3.13.999")
        self.assert_technical(result)
        self.assertEqual(b"", result.stdout)


class WorkerContractTests(unittest.TestCase):
    def test_duplicate_request_fields_are_rejected(self) -> None:
        with self.assertRaises(loader.ContractError):
            loader._read_request(io.BytesIO(b'{"a":1,"a":2}\n'))

    def test_nested_duplicate_fields_are_rejected(self) -> None:
        with self.assertRaises(loader.ContractError):
            loader._read_request(io.BytesIO(b'{"a":{"b":1,"b":2}}\n'))

    def test_nonfinite_input_constants_are_rejected(self) -> None:
        for spelling in ("NaN", "Infinity", "-Infinity"):
            with self.subTest(spelling=spelling), self.assertRaises(loader.ContractError):
                loader._read_request(io.BytesIO(('{"a":' + spelling + '}\n').encode()))

    def test_multiple_and_unterminated_requests_are_rejected(self) -> None:
        for value in (b'{}', b'{}\n{}\n', b'{}\n '):
            with self.subTest(value=value), self.assertRaises(loader.ContractError):
                loader._read_request(io.BytesIO(value))

    def test_request_corruption_is_refused_before_code_executes(self) -> None:
        changes = {"protocolVersion": True, "type": "resume", "epoch": 0, "generation": -1,
                   "tenantId": "", "deadlineUtc": "2020-01-01T00:00:00Z", "inputs": []}
        for name, invalid in changes.items():
            value = request()
            value[name] = invalid
            with self.subTest(name=name), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_target_and_runtime_cannot_change_language_or_identity(self) -> None:
        for name in ("reference", "executionLanguage", "runtimeVersion", "runtimeSha256"):
            value = request()
            value["code"]["runtime"][name] = "wrong"
            with self.subTest(name=name), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)
        value = request()
        value["code"]["target"]["executionLanguage"] = "typescript"
        with self.assertRaises(loader.ContractError):
            loader._validate(value, RUNTIME)

    def test_corrupt_bytes_size_and_digest_are_rejected(self) -> None:
        for name, invalid in (("sha256", "f" * 64), ("sizeBytes", 0), ("base64Url", "YQ=="), ("base64Url", "YR")):
            value = request()
            value["code"]["sources"][0][name] = invalid
            with self.subTest(name=name, invalid=invalid), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_unsafe_and_unsupported_paths_are_rejected(self) -> None:
        for path in ("../main.py", "/main.py", "C:/main.py", "a\\main.py", "a//main.py", "NUL.py", "CON/x.py",
                     "COM1.py", "main.pyc", "dependency.whl", "binary.pyd", "data.json", "dash-name.py", "a.b.py"):
            value = request()
            value["code"]["sources"][0]["path"] = path
            value["code"]["entryPointPath"] = path
            with self.subTest(path=path), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_case_collisions_and_module_package_collisions_are_rejected(self) -> None:
        for paths in (("main.py", "Main.py"), ("a.py", "a/__init__.py")):
            value = request()
            value["code"]["sources"] = [file(path, SIMPLE) for path in paths]
            with self.subTest(paths=paths), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_stdlib_shadowing_is_rejected(self) -> None:
        for path in ("json.py", "os.py", "asyncio/__init__.py", "JSON.py", "__main__.py"):
            value = request()
            value["code"]["sources"].append(file(path, ""))
            with self.subTest(path=path), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_package_split_between_source_and_dependency_is_rejected(self) -> None:
        value = request()
        value["code"]["sources"].append(file("app/__init__.py", ""))
        value["code"]["dependencies"] = [{"name": "rules", "version": "1.0.0", "files": [file("app/rules.py", "")]}]
        with self.assertRaises(loader.ContractError):
            loader._validate(value, RUNTIME)

    def test_namespace_package_without_init_is_rejected(self) -> None:
        value = request()
        value["code"]["sources"].append(file("app/util.py", ""))
        with self.assertRaises(loader.ContractError):
            loader._validate(value, RUNTIME)

    def test_entry_must_be_in_sources_not_dependencies(self) -> None:
        value = request()
        value["code"]["entryPointPath"] = "external.py"
        value["code"]["dependencies"] = [{"name": "rules", "version": "1.0.0", "files": [file("external.py", SIMPLE)]}]
        with self.assertRaises(loader.ContractError):
            loader._validate(value, RUNTIME)

    def test_dependency_ranges_and_duplicate_names_are_rejected(self) -> None:
        for version in ("latest", ">=1.0", "*", "https://server"):
            value = request()
            value["code"]["dependencies"] = [{"name": "rules", "version": version, "files": [file("rules.py", "")]}]
            with self.subTest(version=version), self.assertRaises(loader.ContractError):
                loader._validate(value, RUNTIME)

    def test_invalid_syntax_anywhere_prevents_all_execution(self) -> None:
        value = request('print("SHOULD_NOT_EXECUTE")\n' + SIMPLE)
        value["code"]["sources"].append(file("invalid.py", "def broken("))
        result = execute(value)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(b"", result.stdout)
        self.assertNotIn(b"SHOULD_NOT_EXECUTE", result.stderr)

    def test_invalid_unicode_source_is_refused(self) -> None:
        value = request()
        value["code"]["sources"] = [file("main.py", b"\xff")]
        with self.assertRaises(UnicodeError):
            loader._validate(value, RUNTIME)

    def test_ambiguous_or_lifecycle_results_are_rejected(self) -> None:
        for value in (True, 42, None, {}, {"success": 1, "payload": None},
                      {"success": True, "payload": None, "park": True}, {"success": True},
                      {"success": True, "payload": {1: "a"}}, {"success": True, "payload": float("nan")},
                      {"success": True, "payload": {"x": (1, 2)}}):
            with self.subTest(value=value), self.assertRaises(loader.ContractError):
                loader._result(value)

    def test_large_result_is_not_truncated_or_accepted(self) -> None:
        with self.assertRaises(loader.ContractError):
            loader._result({"success": True, "payload": "x" * loader.MAX_INLINE_BYTES})

    def test_recursive_result_fails_with_bounded_validation(self) -> None:
        value = []
        value.append(value)
        with self.assertRaises(loader.ContractError):
            loader._result({"success": True, "payload": value})

    def test_file_count_and_aggregate_bounds_are_enforced(self) -> None:
        value = request()
        value["code"]["sources"] += [file("module_" + str(i) + ".py", "") for i in range(loader.MAX_FILES)]
        with self.assertRaises(loader.ContractError):
            loader._validate(value, RUNTIME)

    def test_read_only_target_metadata(self) -> None:
        context = loader._context(request())
        with self.assertRaises(TypeError):
            context["target"]["publicationRef"] = "other"

    def test_no_worker_specific_packages_are_imported(self) -> None:
        source = WORKER.read_text(encoding="utf-8")
        self.assertNotIn("import Multiplexed", source)
        self.assertNotIn("subprocess", source)
        self.assertNotIn("pip install", source)
        self.assertNotIn("tempfile", source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
