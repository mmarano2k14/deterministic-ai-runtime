import { RequestSpec } from "@/lib/infrastructure/transport/http/HttpClientType";

export type BurstState = "Idle" | "Running" | "Stopping" | "Completed" | "Error";

export type BurstPlanKey = "read" | "refund";
/**
 * Explicit dispatch modes.
 * - single-burst: all requests are fired immediately
 * - maintained-concurrency: keep N requests in flight until total is reached
 * - wave-batches: send fixed-size batches, optionally with a pause between waves
 */
export type BurstDispatchModeKey =
  | "single-burst"
  | "maintained-concurrency"
  | "wave-batches"
  | "wave-batches-staggered";
/**
 * Shared fields across all burst modes.
 */
export type BurstConfigBase = {
  total: number;
  delayMs: number;
  planKey: BurstPlanKey;
  dispatchMode: BurstDispatchModeKey;
};

/**
 * Single burst:
 * all requests are sent immediately.
 * No concurrency field is needed in this mode.
 */
export type SingleBurstConfig = BurstConfigBase & {
  dispatchMode: "single-burst";
};
/**
 * Maintained concurrency:
 * keep "concurrency" requests in flight until total is reached.
 */
export type MaintainedConcurrencyConfig = BurstConfigBase & {
  dispatchMode: "maintained-concurrency";
  concurrency: number;
};

/**
 * Wave batches:
 * send fixed-size batches and wait for each wave to finish.
 */
export type WaveBatchesConfig = BurstConfigBase & {
  dispatchMode: "wave-batches";
  batchSize: number;
  wavePauseMs: number;
};
/**
 * Wave batches:
 * send fixed-size batches and wait for each wave to finish, ms between  each request in batch
 */
export type WaveBatchesStaggeredConfig = BurstConfigBase & {
  dispatchMode: "wave-batches-staggered";
  batchSize: number;
  wavePauseMs: number;
};

/**
 * Discriminated union used by the machine, controller and UI.
 */
export type BurstConfig =
  | SingleBurstConfig
  | MaintainedConcurrencyConfig
  | WaveBatchesConfig
  | WaveBatchesStaggeredConfig;
export type BurstCounters = {
  ok: number;
  unauthorized: number; // 401
  forbidden: number; // 403
  rejected: number; // 499
  other: number; // other HTTP
  errors: number; // network/proxy/throw
};

export type BurstProgress = {
  started: number;
  completed: number;
  inFlight: number;
  total:number;
};

export type BurstTiming = {
  startedAt?: number;
  finishedAt?: number;
};

export type BurstStats = {
  durationsMs: number[];
  p50ms?: number;
  p95ms?: number;
};
export type BurstReport = {
  config: BurstConfig;
  progress: BurstProgress;
  counters: BurstCounters;
  timing: BurstTiming;
  stats: BurstStats;
  error?: string;
};

export type BurstRuntime = {
  state: BurstState;
  report: BurstReport | null;
  // "stop requested" signal (also mirrored by controller abort token)
  stopRequested: boolean;
};

export type BurstPlan = {
  key: BurstPlanKey;
  displayName: string;
  makeRequest(i: number): RequestSpec;
};
export type BurstEvent =
  | { type: "Configure"; config: BurstConfig } // updates config even if not running
  | { type: "Start"; config: BurstConfig; startedAt: number } // transitions -> Running, initializes report
  | { type: "Stop" } // transitions -> Stopping (soft)
  | { type: "TickStart"; count: number } // when N new requests start
  | { type: "TickComplete"; count: number } // when N requests complete (any outcome)
  | { type: "ResultOk"; durationMs: number }
  | { type: "ResultHttp"; status: number; durationMs: number }
  | { type: "ResultError"; message: string }
  | { type: "Finish" } // transitions -> Completed (or Idle)
  | { type: "Fail"; message: string }
  | { type: "Reset" };
