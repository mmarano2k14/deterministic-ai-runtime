import type { AiSdkExecutionReplayResponse } from "@multiplexed/ai-sdk";

export const SNAPSHOT_MISSING_REASON = "Snapshot not found.";
export const DEFAULT_REPLAY_READINESS_TIMEOUT_MS = 30_000;
export const DEFAULT_REPLAY_READINESS_POLL_MS = 250;

export interface ReplayReadinessOutcome {
  readonly response: AiSdkExecutionReplayResponse;
  readonly attempts: number;
  readonly snapshotMissingCount: number;
  readonly elapsedMs: number;
}

export interface ReplayReadinessOptions {
  readonly timeoutMs?: number;
  readonly pollMs?: number;
  readonly signal?: AbortSignal;
  readonly trace?: (line: string) => void;
}

/** Wait only for snapshot visibility through the existing public audit operation.
 * The callback must reuse the same request/ExecutionId and must never submit or
 * restore an execution. Other responses/exceptions are not retried.
 */
export async function replayAfterSnapshotVisibility(
  executionId: string,
  executionCompleted: boolean,
  replay: (signal: AbortSignal) => Promise<AiSdkExecutionReplayResponse>,
  options: ReplayReadinessOptions = {},
): Promise<ReplayReadinessOutcome> {
  if (!executionId.trim()) throw new Error("executionId is required");
  const timeoutMs = options.timeoutMs ?? DEFAULT_REPLAY_READINESS_TIMEOUT_MS;
  const pollMs = options.pollMs ?? DEFAULT_REPLAY_READINESS_POLL_MS;
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0 || timeoutMs > 2_147_483_647) {
    throw new Error("timeoutMs must be a positive, supported timer duration");
  }
  if (!Number.isFinite(pollMs) || pollMs <= 0 || pollMs > 2_147_483_647) {
    throw new Error("pollMs must be a positive, supported timer duration");
  }
  const started = performance.now();
  const controller = new AbortController();
  let attempts = 0;
  let missingCount = 0;
  const forwardCancellation = () => controller.abort(options.signal?.reason);
  options.signal?.addEventListener("abort", forwardCancellation, { once: true });
  if (options.signal?.aborted) forwardCancellation();
  const timeoutError = () => new Error(
    `Replay snapshot readiness timed out after ${timeoutMs}ms for ${executionId}; ` +
    `attempts=${attempts}; snapshotMisses=${missingCount}.`,
  );
  const timer = setTimeout(() => controller.abort(timeoutError()), timeoutMs);
  try {
    while (true) {
      controller.signal.throwIfAborted();
      if (performance.now() - started >= timeoutMs) throw timeoutError();
      attempts += 1;
      // Enforce the budget even if a transport fails to observe its AbortSignal.
      const response = await withAbort(replay(controller.signal), controller.signal);
      controller.signal.throwIfAborted();
      if (performance.now() - started >= timeoutMs) throw timeoutError();
      if (response.executionId !== executionId) {
        throw new Error("Replay response changed the durable ExecutionId.");
      }
      const pending = executionCompleted && response.succeeded === false &&
        response.deterministic !== true && response.failureReason === SNAPSHOT_MISSING_REASON;
      const elapsedMs = Math.floor(performance.now() - started);
      const outcome = pending ? "SnapshotPending" :
        response.succeeded && response.deterministic === true ? "Ready" : "ReplayRejected";
      options.trace?.(
        `[REPLAY READINESS] ExecutionId=${executionId}; Attempt=${attempts}; ` +
        `Outcome=${outcome}; ElapsedMs=${elapsedMs}`,
      );
      if (!pending) return { response, attempts, snapshotMissingCount: missingCount, elapsedMs };
      missingCount += 1;
      await abortableDelay(Math.min(pollMs, Math.max(1, timeoutMs - (performance.now() - started))), controller.signal);
    }
  } finally {
    clearTimeout(timer);
    options.signal?.removeEventListener("abort", forwardCancellation);
  }
}

function withAbort<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const aborted = () => reject(signal.reason);
    signal.addEventListener("abort", aborted, { once: true });
    // Always attach both handlers, including when cancellation already happened.
    void work.then(
      (value) => { signal.removeEventListener("abort", aborted); resolve(value); },
      (error: unknown) => { signal.removeEventListener("abort", aborted); reject(error); },
    );
    if (signal.aborted) {
      signal.removeEventListener("abort", aborted);
      aborted();
    }
  });
}

function abortableDelay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const aborted = () => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", aborted);
      resolve();
    }, ms);
    signal.addEventListener("abort", aborted, { once: true });
    if (signal.aborted) {
      signal.removeEventListener("abort", aborted);
      aborted();
    }
  });
}
