import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { AiAnalysisContextSnapshot } from "./AiAnalysisType";

export class AiAnalysisContextSnapshotBuilder {
  public static build(
    model: BurstRuntime,
    logCount: number
  ): AiAnalysisContextSnapshot {
    const report = model.report;
    const counters = report?.counters;
    const progress = report?.progress;
    const stats = report?.stats;

    const dispatchMode = report?.config.dispatchMode ?? "No scenario yet";
    const completed = progress?.completed ?? 0;
    const total = progress?.total ?? report?.config.total ?? 0;

    return {
      title: dispatchMode,
      subtitle: `${model.state} · ${completed}/${total} requests completed`,
      metrics: [
        { label: "OK", value: String(counters?.ok ?? 0) },
        { label: "403", value: String(counters?.forbidden ?? 0) },
        { label: "429", value: String(counters?.rejected ?? 0) },
        {
          label: "p50 / p95",
          value: `${this.formatMs(stats?.p50ms)} / ${this.formatMs(stats?.p95ms)}`,
        },
        { label: "Live logs", value: String(logCount) },
        { label: "In flight", value: String(progress?.inFlight ?? 0) },
      ],
    };
  }

  private static formatMs(value?: number): string {
    if (value === undefined || Number.isNaN(value)) {
      return "—";
    }

    return `${Math.round(value)} ms`;
  }
}
