import type {
  BurstRuntime,
} from "@/lib/console/burst/runtime/BurstMachineType";
import type {
  RuntimeAnalysisScenarioExecutionObservation,
} from "./RuntimeAnalysisType";

export class RuntimeAnalysisScenarioObservationBuilder {
  public static build(
    model: BurstRuntime
  ): RuntimeAnalysisScenarioExecutionObservation {
    const report = model.report;

    if (!report) {
      throw new Error(
        "Burst report is unavailable for approved scenario observation."
      );
    }

    const startedAt = report.timing.startedAt;

    if (startedAt === undefined) {
      throw new Error(
        "Approved scenario burst has no start timestamp."
      );
    }

    const finishedAt =
      report.timing.finishedAt ?? Date.now();

    return {
      clientState: model.state,
      startedAtUtc: new Date(startedAt).toISOString(),
      finishedAtUtc: new Date(finishedAt).toISOString(),
      completed: report.progress.completed,
      inFlight: report.progress.inFlight,
      ok: report.counters.ok,
      unauthorized: report.counters.unauthorized,
      forbidden: report.counters.forbidden,
      tooManyRequests: report.counters.rejected,
      otherHttp: report.counters.other,
      errors: report.counters.errors,
      p50Ms: report.stats.p50ms ?? null,
      p95Ms: report.stats.p95ms ?? null,
      elapsedMs: Math.max(0, finishedAt - startedAt),
      error: report.error ?? null,
    };
  }
}
