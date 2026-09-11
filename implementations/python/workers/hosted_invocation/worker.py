"""One published Python function per private-pipe assignment; no runtime imports.

This loader executes trusted, host-approved Python code. It is not an OS sandbox.
Published Python modules are hash-checked and loaded from memory, never installed.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import datetime as dt
import hashlib
import importlib
import importlib.abc
import importlib.machinery
import inspect
import json
import keyword
import math
import os
import re
import sys
import threading
import time
from types import MappingProxyType
from typing import Any, BinaryIO

MAX_REQUEST_BYTES = 50_331_648
MAX_INLINE_BYTES = 262_144
MAX_FILE_BYTES = 4_194_304
MAX_BUNDLE_BYTES = 33_554_432
MAX_FILES = 512
MAX_DEPENDENCIES = 64
HASH = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\Z", re.ASCII)
VERSION = re.compile(r"3\.(12|13)\.(0|[1-9][0-9]*)\Z", re.ASCII)
EXACT_VERSION = re.compile(r"[0-9][A-Za-z0-9_.+\-]{0,127}\Z", re.ASCII)
IDENTITY_FIELDS = ("requestId", "operationId", "effectIdempotencyKey", "workerId",
                   "tenantId", "executionId", "stepName")
TARGET_FIELDS = ("pipelineName", "pipelineVersion", "definitionSha256", "publicationRef",
                 "publicationSha256", "implementationRef", "implementationSha256",
                 "executionLanguage", "environmentRef", "environmentSha256")
RUNTIME_FIELDS = ("reference", "executionLanguage", "runtimeVersion", "runtimeSha256")


class ContractError(ValueError):
    """Invalid configuration, published material or portable function contract."""


def _require(condition: bool, reason: str) -> None:
    if not condition:
        raise ContractError(reason)


def _object(value: Any, keys: tuple[str, ...] | set[str]) -> dict[str, Any]:
    _require(type(value) is dict and set(value) == set(keys), "Unexpected object fields.")
    return value


def _text(value: Any) -> str:
    _require(type(value) is str and 0 < len(value) <= 512 and value.strip() != "" and
             not any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in value), "Invalid identifier.")
    value.encode("utf-8", "strict")
    return value


def _hash(value: Any) -> str:
    _require(type(value) is str and HASH.fullmatch(value) is not None, "Invalid digest.")
    return value


def _integer(value: Any, low: int, high: int) -> int:
    _require(type(value) is int and low <= value <= high, "Invalid integer.")
    return value


def _json_value(value: Any, depth: int = 0) -> None:
    # JSON-only values; no custom encoder, NaN, coercion of mapping keys or cycles.
    _require(depth <= 32, "JSON nesting exceeds the inline contract.")
    if value is None or type(value) in (bool, int):
        return
    if type(value) is float:
        _require(math.isfinite(value), "JSON numbers must be finite.")
    elif type(value) is str:
        value.encode("utf-8", "strict")
    elif type(value) is list:
        _require(depth < 32, "JSON nesting exceeds the inline contract.")
        for item in value:
            _json_value(item, depth + 1)
    elif type(value) is dict:
        _require(depth < 32, "JSON nesting exceeds the inline contract.")
        for key, item in value.items():
            _require(type(key) is str, "JSON keys must be strings.")
            key.encode("utf-8", "strict")
            _json_value(item, depth + 1)
    else:
        raise ContractError("Only plain JSON values are supported.")


def _encode(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=True, allow_nan=False,
                      separators=(",", ":")).encode("ascii")


def _unique_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for name, value in pairs:
        _require(name not in result, "Duplicate JSON field.")
        result[name] = value
    return result


def _invalid_constant(_: str) -> Any:
    raise ContractError("Nonfinite JSON constant.")


def _read_request(stream: BinaryIO) -> dict[str, Any]:
    line = stream.readline(MAX_REQUEST_BYTES + 2)
    _require(line.endswith(b"\n") and len(line) - 1 <= MAX_REQUEST_BYTES,
             "A bounded newline-terminated request is required.")
    _require(stream.read(1) == b"", "Only one request is allowed per process.")
    value = json.loads(line.decode("utf-8", "strict"), object_pairs_hook=_unique_pairs,
                       parse_constant=_invalid_constant)
    _require(type(value) is dict, "The request must be a JSON object.")
    return value


def _deadline(value: Any) -> dt.datetime:
    _text(value)
    _require(re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
                         r"(?:\.[0-9]{1,7})?(?:Z|\+00:00)", value) is not None,
             "Deadline must be an explicit UTC timestamp.")
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    _require(parsed.utcoffset() == dt.timedelta(0), "Deadline must be UTC.")
    return parsed


def _path(value: Any) -> str:
    _require(type(value) is str and 0 < len(value) <= 240, "Invalid source path.")
    parts = value.split("/")
    for part in parts:
        _require(part not in ("", ".", "..") and not part.endswith(".") and
                 re.fullmatch(r"[A-Za-z0-9_.\-]+", part) is not None,
                 "Source paths must be portable relative paths.")
        stem = part.split(".")[0].upper()
        _require(stem not in ("CON", "PRN", "AUX", "NUL") and
                 re.fullmatch(r"(?:COM|LPT)[1-9]", stem) is None, "Device path is forbidden.")
    _require(parts[-1].endswith(".py"), "Only Python source files are supported.")
    return value


def _file(value: Any) -> tuple[str, bytes]:
    item = _object(value, ("path", "sha256", "sizeBytes", "base64Url"))
    path = _path(item["path"])
    digest = _hash(item["sha256"])
    size = _integer(item["sizeBytes"], 0, MAX_FILE_BYTES)
    encoded = item["base64Url"]
    _require(type(encoded) is str and len(encoded) <= (MAX_FILE_BYTES * 4 + 2) // 3 and
             re.fullmatch(r"[A-Za-z0-9_\-]*", encoded) is not None and len(encoded) % 4 != 1,
             "Invalid base64url content.")
    raw = base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4))
    _require(base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii") == encoded,
             "Noncanonical base64url content.")
    _require(len(raw) == size and hashlib.sha256(raw).hexdigest() == digest,
             "Source content integrity mismatch.")
    raw.decode("utf-8", "strict")
    return path, raw


class _PublishedModules(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    """Exact, in-memory source closure with explicit regular-package boundaries."""

    def __init__(self, bundle: dict[str, Any]) -> None:
        self._modules: dict[str, tuple[str, bool, Any]] = {}
        self._by_path: dict[str, str] = {}
        seen_paths: set[str] = set()
        seen_modules: set[str] = set()
        owners: dict[str, str] = {}
        groups: list[tuple[str, Any]] = [("sources", bundle["sources"])]
        dependencies = bundle["dependencies"]
        _require(type(dependencies) is list and len(dependencies) <= MAX_DEPENDENCIES,
                 "Invalid dependency list.")
        seen_dependencies: set[str] = set()
        for dependency in dependencies:
            item = _object(dependency, ("name", "version", "files"))
            name = _text(item["name"])
            _require(re.fullmatch(r"[A-Za-z0-9_.\-]+", name) is not None and
                     name.casefold() not in seen_dependencies, "Ambiguous dependency name.")
            _require(type(item["version"]) is str and EXACT_VERSION.fullmatch(item["version"]) is not None,
                     "An exact dependency version is required.")
            seen_dependencies.add(name.casefold())
            groups.append(("dependency:" + name, item["files"]))
        reserved = set(sys.stdlib_module_names) | {name.split(".")[0] for name in sys.modules}
        reserved |= {"__main__", "__pycache__", "sitecustomize", "usercustomize"}
        reserved = {name.casefold() for name in reserved}
        total = 0
        count = 0
        for owner, files in groups:
            _require(type(files) is list and 0 < len(files) <= MAX_FILES, "An explicit file list is required.")
            for item in files:
                path, raw = _file(item)
                count += 1
                total += len(raw)
                _require(count <= MAX_FILES and total <= MAX_BUNDLE_BYTES, "Source closure exceeds its bound.")
                _require(path.casefold() not in seen_paths, "Duplicate or case-ambiguous source path.")
                seen_paths.add(path.casefold())
                parts = path[:-3].split("/")
                is_package = parts[-1] == "__init__"
                if is_package:
                    parts.pop()
                _require(bool(parts) and all(IDENTIFIER.fullmatch(p) and not keyword.iskeyword(p) and
                         p != "__pycache__" for p in parts), "Invalid Python module path.")
                name = ".".join(parts)
                _require(name.casefold() not in seen_modules and parts[0].casefold() not in reserved,
                         "A module name collides with another module or the host standard library.")
                seen_modules.add(name.casefold())
                root = parts[0].casefold()
                _require(root not in owners or owners[root] == owner, "A package cannot span source/dependency owners.")
                owners[root] = owner
                origin = "publication://" + bundle["target"]["implementationSha256"] + "/" + path
                # Validate every file before importing any published module or running top-level code.
                code = compile(raw.decode("utf-8"), origin, "exec", dont_inherit=True, optimize=0)
                self._modules[name] = (origin, is_package, code)
                if owner == "sources":
                    self._by_path[path] = name
        for name in self._modules:
            parts = name.split(".")
            for length in range(1, len(parts)):
                parent = self._modules.get(".".join(parts[:length]))
                _require(parent is not None and parent[1], "Every package needs an explicit __init__.py.")
        entry = _path(bundle["entryPointPath"])
        _require(entry in self._by_path, "Entry point must be in the published source files.")
        self.entry_module = self._by_path[entry]
        symbol = bundle["entryPointSymbol"]
        _require(type(symbol) is str and IDENTIFIER.fullmatch(symbol) is not None and
                 not keyword.iskeyword(symbol), "A simple Python entry-point symbol is required.")
        self.entry_symbol = symbol

    def find_spec(self, fullname: str, path: Any = None, target: Any = None) -> Any:
        entry = self._modules.get(fullname)
        if entry is None:
            return None
        origin, is_package, _ = entry
        return importlib.machinery.ModuleSpec(fullname, self, origin=origin, is_package=is_package)

    def create_module(self, spec: Any) -> Any:
        return None  # Use the interpreter's ordinary module allocation and recursive import handling.

    def exec_module(self, module: Any) -> None:
        origin, _, code = self._modules[module.__name__]
        module.__file__ = origin
        exec(code, module.__dict__)


def _validate(request: Any, runtime: dict[str, str]) -> tuple[_PublishedModules, dt.datetime]:
    _object(request, (*IDENTITY_FIELDS, "protocolVersion", "type", "epoch", "generation",
                      "deadlineUtc", "traceParent", "inputs", "code"))
    _require(type(request["protocolVersion"]) is int and request["protocolVersion"] == 1 and
             request["type"] == "invoke", "Unsupported protocol.")
    for name in IDENTITY_FIELDS:
        _text(request[name])
    _integer(request["epoch"], 1, 9_223_372_036_854_775_807)
    _integer(request["generation"], 0, 2_147_483_647)
    if request["traceParent"] is not None:
        _text(request["traceParent"])
    deadline = _deadline(request["deadlineUtc"])
    _require(deadline > dt.datetime.now(dt.timezone.utc), "Invocation deadline has elapsed.")
    _require(type(request["inputs"]) is dict, "Inputs must be a JSON object.")
    _json_value(request["inputs"])
    _require(len(_encode(request["inputs"])) <= MAX_INLINE_BYTES, "Inputs exceed their inline limit.")
    bundle = _object(request["code"], ("target", "runtime", "entryPointPath", "entryPointSymbol", "sources", "dependencies"))
    target = _object(bundle["target"], TARGET_FIELDS)
    for name in TARGET_FIELDS:
        _hash(target[name]) if name.endswith("Sha256") else _text(target[name])
    _require(target["executionLanguage"] == "python", "This worker executes Python only.")
    _object(bundle["runtime"], RUNTIME_FIELDS)
    _require(bundle["runtime"] == runtime, "The exact configured Python runtime is required.")
    return _PublishedModules(bundle), deadline


class _Emitter:
    """Serialize liveness and one terminal result on the host's original output pipe."""

    def __init__(self, stream: BinaryIO, request: dict[str, Any]) -> None:
        self._stream = stream
        self._lock = threading.Lock()
        self._terminal = False
        self._identity = {name: request[name] for name in ("protocolVersion", "requestId", "operationId", "workerId", "epoch")}

    def emit(self, kind: str, result: dict[str, Any] | None = None) -> None:
        with self._lock:
            if self._terminal:
                return
            frame = dict(self._identity, type=kind)
            if kind == "result":
                _require(result is not None, "Missing terminal result.")
                frame.update(result)
                self._terminal = True
            encoded = _encode(frame) + b"\n"
            _require(len(encoded) <= 1_048_576, "Protocol frame exceeds its bound.")
            # BufferedIOBase.write may be short. Never acknowledge a partially written frame.
            offset = 0
            while offset < len(encoded):
                written = self._stream.write(encoded[offset:])
                if written is None or written <= 0:
                    raise BrokenPipeError("The worker protocol pipe is closed.")
                offset += written
            self._stream.flush()


class _Liveness:
    """Liveness only. The server alone renews leases and accepts durable results."""

    def __init__(self, emitter: _Emitter, deadline: dt.datetime, interval: float) -> None:
        self._emitter = emitter
        self._deadline = deadline
        self._monotonic_deadline = time.monotonic() + (deadline - dt.datetime.now(dt.timezone.utc)).total_seconds()
        self._interval = interval
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="invocation-liveness", daemon=True)

    def remaining(self) -> float:
        return min(self._monotonic_deadline - time.monotonic(),
                   (self._deadline - dt.datetime.now(dt.timezone.utc)).total_seconds())

    def start(self) -> None:
        self._thread.start()

    def close(self) -> None:
        self._stop.set()
        self._thread.join(timeout=1)
        _require(not self._thread.is_alive(), "Liveness channel did not stop.")

    def _run(self) -> None:
        try:
            while True:
                remaining = self.remaining()
                if remaining <= 0:
                    os._exit(124)
                if self._stop.wait(min(self._interval, remaining)):
                    return
                if self.remaining() <= 0:
                    os._exit(124)
                self._emitter.emit("heartbeat")
        except BaseException:
            os._exit(74)  # No durable result exists merely because the output pipe failed.


def _context(request: dict[str, Any]) -> MappingProxyType:
    values = {key: request[key] for key in (*IDENTITY_FIELDS, "epoch", "generation", "deadlineUtc", "traceParent")}
    values["target"] = MappingProxyType(dict(request["code"]["target"]))
    return MappingProxyType(values)


def _result(value: Any) -> dict[str, Any]:
    result = _object(value, ("success", "payload"))
    _require(type(result["success"]) is bool, "Success must be an explicit boolean.")
    _json_value(result["payload"])
    _require(len(_encode(result["payload"])) <= MAX_INLINE_BYTES, "Result exceeds the inline payload limit.")
    return result


def _execute(modules: _PublishedModules, request: dict[str, Any]) -> dict[str, Any]:
    sys.meta_path.insert(0, modules)
    module = importlib.import_module(modules.entry_module)
    function = getattr(module, modules.entry_symbol, None)
    _require(inspect.isfunction(function) and function.__module__ == module.__name__,
             "The entry point must be a function defined by its published module.")
    # Fixed portable ABI; bad signatures are technical errors, not business failures.
    inspect.signature(function).bind(request["inputs"], _context(request))
    value = function(request["inputs"], _context(request))
    if inspect.isawaitable(value):
        async def await_value() -> Any:
            return await value
        value = asyncio.run(await_value())
    return _result(value)


def _configuration() -> tuple[dict[str, str], float]:
    parser = argparse.ArgumentParser(description="Host-controlled published Python function worker", allow_abbrev=False)
    parser.add_argument("--runtime-reference", required=True)
    parser.add_argument("--runtime-version", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    parser.add_argument("--heartbeat-ms", type=int, default=1000)
    args = parser.parse_args()
    _text(args.runtime_reference)
    _require(args.runtime_reference.lower() != "latest" and "://" not in args.runtime_reference,
             "An exact runtime reference is required.")
    _hash(args.runtime_sha256)
    _require(VERSION.fullmatch(args.runtime_version) is not None and sys.implementation.name == "cpython" and
             sys.version_info.releaselevel == "final" and
             args.runtime_version == ".".join(map(str, sys.version_info[:3])), "The installed CPython version differs from the profile.")
    _require(sys.flags.isolated == 1 and sys.flags.no_site == 1 and sys.flags.dont_write_bytecode == 1 and
             sys.flags.utf8_mode == 1, "Required isolated interpreter options are missing.")
    _integer(args.heartbeat_ms, 50, 5000)
    return {"reference": args.runtime_reference, "executionLanguage": "python",
            "runtimeVersion": args.runtime_version, "runtimeSha256": args.runtime_sha256}, args.heartbeat_ms / 1000


def _protocol_pipe() -> BinaryIO:
    # Reserve a private descriptor. Even print(), sys.__stdout__ or os.write(1, ...)
    # from cooperative published code go to stderr instead of forging protocol frames.
    sys.stdout.flush()
    descriptor = os.dup(sys.stdout.fileno())
    os.set_inheritable(descriptor, False)
    if os.name == "nt":
        import msvcrt
        msvcrt.setmode(descriptor, os.O_BINARY)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    return os.fdopen(descriptor, "wb", buffering=0)


def main() -> int:
    liveness: _Liveness | None = None
    protocol: BinaryIO | None = None
    try:
        runtime, interval = _configuration()
        request = _read_request(sys.stdin.buffer)
        modules, deadline = _validate(request, runtime)
        protocol = _protocol_pipe()
        emitter = _Emitter(protocol, request)
        liveness = _Liveness(emitter, deadline, interval)
        if liveness.remaining() <= 0:
            return 124
        emitter.emit("ready")
        liveness.start()
        result = _execute(modules, request)
        liveness.close()
        if liveness.remaining() <= 0:
            return 124
        emitter.emit("result", result)
        return 0
    except (ContractError, UnicodeError, json.JSONDecodeError, SyntaxError, RecursionError):
        os.write(2, b"python-worker: invalid_contract\n")
        return 65
    except BaseException:
        # Source paths, inputs, exception text and tracebacks must not leak to host logs.
        os.write(2, b"python-worker: technical_failure\n")
        return 70
    finally:
        if liveness is not None:
            liveness._stop.set()
        if protocol is not None:
            protocol.close()


if __name__ == "__main__":
    # A completed function must not keep the assignment alive through user threads or
    # atexit callbacks. No source files/workspaces need cleanup: imports are in memory.
    os._exit(main())
