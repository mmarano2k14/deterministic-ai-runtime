import type {
  BurstConfig,
  BurstRuntime,
  BurstState,
} from "@/lib/console/burst/runtime/BurstMachineType";

export type RuntimeAnalysisRunFacts = {
  state: BurstState;
  config?: BurstConfig;
  dispatchMode: string;
  completed: number;
  total: number;
  inFlight: number;
  ok: number;
  unauthorized: number;
  forbidden: number;
  tooManyRequests: number;
  otherHttp: number;
  errors: number;
  p50Ms?: number;
  p95Ms?: number;
  elapsedMs?: number;
};

export class RuntimeAnalysisRunReader {
  public static read(model: BurstRuntime): RuntimeAnalysisRunFacts {
    const report = model.report;
    const progress = report?.progress;
    const counters = report?.counters;
    const stats = report?.stats;

    return {
      state: model.state,
      config: report?.config,
      dispatchMode: report?.config.dispatchMode ?? "No scenario yet",
      completed: progress?.completed ?? 0,
      total: progress?.total ?? report?.config.total ?? 0,
      inFlight: progress?.inFlight ?? 0,
      ok: counters?.ok ?? 0,
      unauthorized: counters?.unauthorized ?? 0,
      forbidden: counters?.forbidden ?? 0,
      tooManyRequests: counters?.rejected ?? 0,
      otherHttp: counters?.other ?? 0,
      errors: counters?.errors ?? 0,
      p50Ms: stats?.p50ms,
      p95Ms: stats?.p95ms,
      elapsedMs: this.readElapsedMs(model),
    };
  }

  private static readElapsedMs(model: BurstRuntime): number | undefined {
    const startedAt = model.report?.timing.startedAt;

    if (startedAt === undefined) {
      return undefined;
    }

    const finishedAt = model.report?.timing.finishedAt ?? Date.now();

    return Math.max(0, finishedAt - startedAt);
  }
}
