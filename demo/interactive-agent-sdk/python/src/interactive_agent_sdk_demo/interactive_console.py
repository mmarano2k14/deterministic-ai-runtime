from __future__ import annotations

import asyncio
import json
import sys
import threading
from uuid import uuid4

from multiplexed_ai_sdk import (
    AiSdkClient,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionControlRequest,
    AiSdkExecutionInputSubmissionRequest,
    AiSdkExecutionObservation,
    AiSdkExecutionReplayRequest,
    AiSdkExecutionResult,
    AiSdkExecutionStatus,
    AiSdkExecutionStepStatus,
    AiSdkExecutionWatchEvent,
    AiSdkExecutionWatchEventKind,
    AiSdkExecutionWatchRequest,
)

_OBSERVATION_INTERVAL_SECONDS = 0.4


class ConsoleInputPump:
    def __init__(self) -> None:
        self._loop = asyncio.get_running_loop()
        self._queue: asyncio.Queue[str | None] = asyncio.Queue()
        self._closed = False
        self._thread = threading.Thread(target=self._reader, daemon=True)
        self._thread.start()

    async def read(self) -> str | None:
        return await self._queue.get()

    async def prompt(self, text: str) -> str | None:
        print(text, end="", flush=True)
        return await self.read()

    def close(self) -> None:
        self._closed = True

    def _reader(self) -> None:
        while not self._closed:
            line = sys.stdin.readline()
            if line == "":
                self._loop.call_soon_threadsafe(self._queue.put_nowait, None)
                return
            self._loop.call_soon_threadsafe(
                self._queue.put_nowait,
                line.rstrip("\r\n"),
            )


class InteractiveExecutionConsole:
    def __init__(
        self,
        client: AiSdkClient,
        execution_id: str,
        waiting_key: str,
        waiting_step_name: str,
        input_pump: ConsoleInputPump,
    ) -> None:
        self._client = client
        self._execution_id = execution_id
        self._waiting_key = waiting_key
        self._waiting_step_name = waiting_step_name
        self._input = input_pump
        self._waiting_announced = False

    async def run(self) -> AiSdkExecutionResult | None:
        watch_task = asyncio.create_task(self._watch())
        _print_commands()

        try:
            while True:
                observation = await self._client.observe_execution(self._execution_id)
                self._announce_input_boundary(observation)

                if _is_terminal(observation.status):
                    watch_task.cancel()
                    await _ignore_task_termination(watch_task)
                    return await self._client.get_execution_result(self._execution_id)

                try:
                    command = await asyncio.wait_for(
                        self._input.read(),
                        timeout=_OBSERVATION_INTERVAL_SECONDS,
                    )
                except TimeoutError:
                    continue

                normalized = None if command is None else command.strip().lower()
                if normalized == "q":
                    watch_task.cancel()
                    await _ignore_task_termination(watch_task)
                    return None

                await self._handle_command(normalized, observation)
        finally:
            watch_task.cancel()
            await _ignore_task_termination(watch_task)

    async def run_post_terminal_commands(
        self,
        terminal_status: AiSdkExecutionStatus,
    ) -> None:
        print()
        print("Post-terminal commands:")
        print("  [x] deterministic replay validation")
        print("  [q] exit")

        while True:
            command = await self._input.prompt("> ")
            normalized = None if command is None else command.strip().lower()

            if normalized == "x":
                await self._replay()
                continue
            if normalized in {"q", "", None}:
                return
            print(
                f"Execution is already {terminal_status.value}. Use 'x' or 'q'."
            )

    async def _watch(self) -> None:
        try:
            async for item in self._client.watch_execution(
                AiSdkExecutionWatchRequest(
                    execution_id=self._execution_id,
                    include_initial_snapshot=True,
                )
            ):
                _print_watch_item(item)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            print(f"[watch] stopped: {exc}")

    def _announce_input_boundary(
        self,
        observation: AiSdkExecutionObservation,
    ) -> None:
        if self._waiting_announced:
            return

        waiting_step = next(
            (
                step
                for step in observation.steps
                if step.name == self._waiting_step_name
            ),
            None,
        )
        if (
            waiting_step is None
            or waiting_step.status is not AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL
        ):
            return

        self._waiting_announced = True
        print()
        print("Human input required.")
        print(
            f"Step '{self._waiting_step_name}' is durably parked. "
            "Enter 'i' to approve/reject and add feedback."
        )
        print()

    async def _handle_command(
        self,
        command: str | None,
        observation: AiSdkExecutionObservation,
    ) -> None:
        if command == "p":
            await self._pause()
        elif command == "r":
            await self._resume()
        elif command == "i":
            await self._submit_input(observation)
        elif command == "c":
            await self._cancel()
        elif command == "s":
            _print_snapshot(None, observation)
        elif command == "x":
            print("Replay validation is available after terminal convergence.")
        elif command in {"", None}:
            return
        else:
            print("Unknown command. Use p, r, i, c, s, or q.")

    async def _pause(self) -> None:
        print()
        print(f"SDK command: sdk.execution.pause({self._execution_id})")
        response = await self._client.pause_execution(
            self._execution_id,
            AiSdkExecutionControlRequest(
                reason="interactive-agent-console-pause",
            ),
        )
        state = response.state.status.value if response.state is not None else "unknown"
        print(f"Pause accepted={response.accepted}; controlState={state}")
        print(f"ExecutionId unchanged: {response.execution_id}")
        print()

    async def _resume(self) -> None:
        print()
        print(f"SDK command: sdk.execution.resume({self._execution_id})")
        response = await self._client.resume_execution(
            self._execution_id,
            AiSdkExecutionControlRequest(
                reason="interactive-agent-console-resume",
            ),
        )
        state = response.state.status.value if response.state is not None else "unknown"
        print(f"Resume accepted={response.accepted}; controlState={state}")
        print(f"ExecutionId unchanged: {response.execution_id}")
        print()

    async def _submit_input(
        self,
        observation: AiSdkExecutionObservation,
    ) -> None:
        waiting_step = next(
            (
                step
                for step in observation.steps
                if step.name == self._waiting_step_name
            ),
            None,
        )
        if (
            waiting_step is None
            or waiting_step.status is not AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL
        ):
            print("The execution is not currently parked at the human-input boundary.")
            return

        approved = await self._read_approval()
        feedback = (await self._input.prompt("Feedback (optional): ") or "").strip()

        print()
        print(f"SDK command: sdk.execution.input.submit({self._execution_id})")
        response = await self._client.submit_execution_input(
            self._execution_id,
            AiSdkExecutionInputSubmissionRequest(
                waiting_key=self._waiting_key,
                waiting_step_name=self._waiting_step_name,
                reason="interactive-agent-human-review",
                correlation_id=f"interactive-agent-input-{uuid4().hex}",
                input={"approved": approved, "feedback": feedback},
            ),
        )
        state = response.state.status.value if response.state is not None else "unknown"
        print(f"Input accepted={response.accepted}; controlState={state}")
        print(f"ExecutionId unchanged: {response.execution_id}")
        print()

    async def _read_approval(self) -> bool:
        while True:
            answer = (
                (await self._input.prompt("Approve the agent plan? [y/n]: ") or "")
                .strip()
                .lower()
            )
            if answer in {"y", "yes"}:
                return True
            if answer in {"n", "no"}:
                return False
            print("Enter 'y' or 'n'.")

    async def _cancel(self) -> None:
        print()
        print(f"SDK command: sdk.execution.cancel({self._execution_id})")
        response = await self._client.cancel_execution(
            self._execution_id,
            AiSdkExecutionCancellationRequest(
                reason="interactive-agent-console-cancel",
                correlation_id=f"interactive-agent-cancel-{uuid4().hex}",
            ),
        )
        print(
            f"Cancellation requested={response.cancellation_requested}; "
            f"status={response.status.value}"
        )
        print()

    async def _replay(self) -> None:
        print()
        print(f"SDK command: sdk.execution.replay({self._execution_id})")
        replay = await self._client.replay_execution(
            self._execution_id,
            AiSdkExecutionReplayRequest(
                strict_determinism=True,
                include_diagnostics=True,
                reason="interactive-agent-console-replay",
                correlation_id=f"interactive-agent-replay-{uuid4().hex}",
            ),
        )

        print(f"Replay succeeded: {replay.succeeded}")
        print(
            "Deterministic: "
            + ("unknown" if replay.deterministic is None else str(replay.deterministic))
        )
        if replay.message:
            print(f"Message: {replay.message}")
        if replay.failure_reason:
            print(f"Failure: {replay.failure_reason}")
        for diagnostic in replay.diagnostics:
            print(f"  {diagnostic}")
        print(
            "Replay validates the existing durable execution; "
            "it does not create a second execution."
        )
        print()


def print_terminal_result(result: AiSdkExecutionResult) -> None:
    print()
    print("==================================================")
    print(" Terminal execution result")
    print("==================================================")
    print(f"ExecutionId: {result.execution_id}")
    print(f"Status: {result.status.value}")
    print(f"CompletedAtUtc: {result.completed_at_utc}")
    print()
    print("Agent response:")
    if isinstance(result.output, (dict, list)):
        print(json.dumps(result.output, indent=2, ensure_ascii=False))
    elif result.output is None:
        print("(none)")
    else:
        print(result.output)

    if result.failure is not None:
        print()
        print(f"Failure: {result.failure.code}: {result.failure.message}")


def _print_commands() -> None:
    print("Commands while the execution is active:")
    print("  [p] pause")
    print("  [r] resume")
    print("  [i] submit human input")
    print("  [c] cancel")
    print("  [s] status")
    print("  [q] detach local console without cancelling")
    print()


def _print_watch_item(item: AiSdkExecutionWatchEvent) -> None:
    if (
        item.kind is AiSdkExecutionWatchEventKind.SNAPSHOT
        and item.snapshot is not None
    ):
        _print_snapshot(item.sequence, item.snapshot)
    elif item.kind is AiSdkExecutionWatchEventKind.EVENT:
        channel = item.channel.value if item.channel is not None else "event"
        print(f"[watch #{item.sequence or '-'}] {channel} {item.event_type or 'event'}")
    elif item.kind is AiSdkExecutionWatchEventKind.RESYNC_REQUIRED:
        reason = (
            item.resync_required.reason.value
            if item.resync_required is not None
            else "unknown"
        )
        print(f"[watch] resync required: {reason}")


def _print_snapshot(
    sequence: int | None,
    snapshot: AiSdkExecutionObservation,
) -> None:
    steps = ", ".join(
        f"{step.name}={step.status.value}" for step in snapshot.steps
    )
    print(
        f"[snapshot #{sequence if sequence is not None else '-'}] "
        f"execution={snapshot.status.value}; {steps}"
    )


def _is_terminal(status: AiSdkExecutionStatus) -> bool:
    return status in {
        AiSdkExecutionStatus.COMPLETED,
        AiSdkExecutionStatus.FAILED,
        AiSdkExecutionStatus.CANCELLED,
    }


async def _ignore_task_termination(task: asyncio.Task[object]) -> None:
    try:
        await task
    except asyncio.CancelledError:
        return
    except Exception:
        return
