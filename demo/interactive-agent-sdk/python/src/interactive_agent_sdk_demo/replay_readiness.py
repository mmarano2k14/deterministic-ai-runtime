from __future__ import annotations

import asyncio
import math
from dataclasses import dataclass
from typing import TYPE_CHECKING, Awaitable, Callable

if TYPE_CHECKING:
    from multiplexed_ai_sdk import AiSdkExecutionReplayResponse

SNAPSHOT_MISSING_REASON = "Snapshot not found."
DEFAULT_TIMEOUT_SECONDS = 30.0
DEFAULT_POLL_SECONDS = 0.25


@dataclass(frozen=True)
class ReplayReadinessOutcome:
    response: AiSdkExecutionReplayResponse
    attempts: int
    snapshot_missing_count: int
    elapsed_ms: int


async def replay_after_snapshot_visibility(
    execution_id: str,
    execution_completed: bool,
    replay: Callable[[], Awaitable[AiSdkExecutionReplayResponse]],
    *,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
    poll_seconds: float = DEFAULT_POLL_SECONDS,
    trace: Callable[[str], None] | None = None,
) -> ReplayReadinessOutcome:
    """Retry only an audit response reporting a not-yet-visible snapshot.

    The callback must use the same public audit-only replay request/ExecutionId.
    It must never submit a new execution or request restoration. Every returned
    response is preserved; neither determinism nor success is manufactured here.
    """
    if not execution_id or not execution_id.strip():
        raise ValueError("execution_id is required")
    if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise ValueError("timeout_seconds must be finite and positive")
    if not math.isfinite(poll_seconds) or poll_seconds <= 0:
        raise ValueError("poll_seconds must be finite and positive")

    loop = asyncio.get_running_loop()
    started = loop.time()
    attempts = 0
    missing_count = 0
    budget = asyncio.timeout(timeout_seconds)
    try:
        async with budget:
            while True:
                if loop.time() - started >= timeout_seconds:
                    raise TimeoutError(f"Replay snapshot readiness timed out for {execution_id}.")
                attempts += 1
                response = await replay()
                if loop.time() - started >= timeout_seconds:
                    raise TimeoutError(f"Replay snapshot readiness timed out for {execution_id}.")
                if response.execution_id != execution_id:
                    raise RuntimeError("Replay response changed the durable ExecutionId.")
                pending = (
                    execution_completed
                    and response.succeeded is False
                    and response.deterministic is not True
                    and response.failure_reason == SNAPSHOT_MISSING_REASON
                )
                elapsed_ms = int((loop.time() - started) * 1000)
                outcome = (
                    "SnapshotPending" if pending else
                    "Ready" if response.succeeded and response.deterministic is True else
                    "ReplayRejected"
                )
                if trace is not None:
                    trace(
                        f"[REPLAY READINESS] ExecutionId={execution_id}; "
                        f"Attempt={attempts}; Outcome={outcome}; ElapsedMs={elapsed_ms}"
                    )
                if not pending:
                    return ReplayReadinessOutcome(
                        response, attempts, missing_count, elapsed_ms
                    )
                missing_count += 1
                await asyncio.sleep(poll_seconds)
    except TimeoutError as error:
        if not budget.expired():
            raise  # A transport timeout is not a retryable snapshot miss.
        raise TimeoutError(
            f"Replay snapshot readiness timed out after {timeout_seconds:g}s "
            f"for {execution_id}; attempts={attempts}; snapshotMisses={missing_count}."
        ) from error
