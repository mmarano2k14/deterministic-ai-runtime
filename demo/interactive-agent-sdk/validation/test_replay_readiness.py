"""Offline regression tests; no engine, SDK installation, or database is required."""
import asyncio
import importlib.util
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

SOURCE = Path(__file__).resolve().parents[1] / "python/src/interactive_agent_sdk_demo/replay_readiness.py"
spec = importlib.util.spec_from_file_location("replay_readiness_under_test", SOURCE)
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)
run = module.replay_after_snapshot_visibility
ID = "published-test-execution"


def response(**changes):
    fields = dict(execution_id=ID, succeeded=True, deterministic=True, failure_reason=None)
    fields.update(changes)
    return SimpleNamespace(**fields)


def missing(**changes):
    return response(succeeded=False, deterministic=False, failure_reason="Snapshot not found.", **changes)


class ReadinessTests(unittest.IsolatedAsyncioTestCase):
    async def sequence(self, values, completed=True):
        calls = []
        async def replay():
            calls.append(len(calls) + 1)
            return values[min(len(calls) - 1, len(values) - 1)]
        result = await run(ID, completed, replay, timeout_seconds=1, poll_seconds=.001)
        return result, calls

    async def test_immediate_success_is_unchanged(self):
        item = response()
        outcome, calls = await self.sequence([item])
        self.assertIs(outcome.response, item)
        self.assertEqual((outcome.attempts, outcome.snapshot_missing_count, len(calls)), (1, 0, 1))

    async def test_missing_then_actual_success(self):
        item = response()
        outcome, calls = await self.sequence([missing(), missing(), item])
        self.assertIs(outcome.response, item)
        self.assertEqual((outcome.attempts, outcome.snapshot_missing_count, len(calls)), (3, 2, 3))

    async def test_other_failure_is_not_retried(self):
        item = response(succeeded=False, deterministic=False, failure_reason="Snapshot invalid.")
        outcome, calls = await self.sequence([item, response()])
        self.assertIs(outcome.response, item)
        self.assertEqual(len(calls), 1)

    async def test_determinism_failure_is_not_retried(self):
        item = response(succeeded=True, deterministic=False, failure_reason="Fingerprint mismatch.")
        outcome, calls = await self.sequence([item, response()])
        self.assertIs(outcome.response, item)
        self.assertEqual(len(calls), 1)

    async def test_failed_execution_does_not_wait(self):
        item = missing()
        outcome, calls = await self.sequence([item, response()], completed=False)
        self.assertIs(outcome.response, item)
        self.assertEqual(len(calls), 1)

    async def test_nonexact_missing_reason_is_not_retried(self):
        item = response(succeeded=False, deterministic=False, failure_reason="Snapshot not found. Details")
        outcome, calls = await self.sequence([item, response()])
        self.assertIs(outcome.response, item)
        self.assertEqual(len(calls), 1)

    async def test_replay_id_mismatch_fails(self):
        with self.assertRaisesRegex(RuntimeError, "changed the durable ExecutionId"):
            await self.sequence([missing(execution_id="other")])

    async def test_transport_error_propagates_once(self):
        error = RuntimeError("unauthorized transport")
        calls = 0
        async def replay():
            nonlocal calls
            calls += 1
            raise error
        with self.assertRaises(RuntimeError) as caught:
            await run(ID, True, replay)
        self.assertIs(caught.exception, error)
        self.assertEqual(calls, 1)

    async def test_transport_timeout_is_not_retried(self):
        error = TimeoutError("transport timeout")
        async def replay():
            raise error
        with self.assertRaises(TimeoutError) as caught:
            await run(ID, True, replay)
        self.assertIs(caught.exception, error)

    async def test_permanent_absence_times_out(self):
        async def replay(): return missing()
        with self.assertRaisesRegex(TimeoutError, "readiness timed out"):
            await run(ID, True, replay, timeout_seconds=.025, poll_seconds=.005)

    async def test_inflight_call_is_cancelled_at_deadline(self):
        cancelled = False
        async def replay():
            nonlocal cancelled
            try:
                await asyncio.sleep(5)
            finally:
                cancelled = True
            return response()
        with self.assertRaisesRegex(TimeoutError, "readiness timed out"):
            await run(ID, True, replay, timeout_seconds=.025)
        self.assertTrue(cancelled)

    async def test_caller_cancellation_propagates(self):
        entered = asyncio.Event()
        async def replay():
            entered.set()
            await asyncio.sleep(5)
            return response()
        task = asyncio.create_task(run(ID, True, replay))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task

    async def test_late_success_is_not_accepted(self):
        async def replay():
            try:
                await asyncio.sleep(5)
            except asyncio.CancelledError:
                return response()  # Deliberately misbehaving callback.
        with self.assertRaisesRegex(TimeoutError, "readiness timed out"):
            await run(ID, True, replay, timeout_seconds=.025)

    async def test_invalid_durations_fail_before_call(self):
        async def replay(): self.fail("callback must not run")
        for kwargs in ({"timeout_seconds": 0}, {"poll_seconds": 0}, {"timeout_seconds": float("nan")}):
            with self.assertRaises(ValueError):
                await run(ID, True, replay, **kwargs)

    async def test_trace_records_each_attempt_without_faking_gates(self):
        entries = []
        values = iter([missing(), response()])
        async def replay(): return next(values)
        outcome = await run(ID, True, replay, poll_seconds=.001, trace=entries.append)
        self.assertEqual(outcome.attempts, 2)
        self.assertIn("Outcome=SnapshotPending", entries[0])
        self.assertIn("Outcome=Ready", entries[1])


if __name__ == "__main__":
    unittest.main(verbosity=2)
