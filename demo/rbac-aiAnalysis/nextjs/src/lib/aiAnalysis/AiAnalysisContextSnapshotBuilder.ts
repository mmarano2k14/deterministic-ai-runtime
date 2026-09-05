import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import { RuntimeAnalysisRunReader } from "./RuntimeAnalysisRunReader";
import type { AiAnalysisContextSnapshot } from "./AiAnalysisType";

export class AiAnalysisContextSnapshotBuilder {
  public static build(
    model: BurstRuntime,
    logCount: number
  ): AiAnalysisContextSnapshot {
    const run = RuntimeAnalysisRunReader.read(model);

    return {
      title: run.dispatchMode,
      subtitle: `${run.state} · ${run.completed}/${run.total} requests completed`,
      metrics: [
        { label: "OK", value: String(run.ok) },
        { label: "403", value: String(run.forbidden) },
        { label: "429", value: String(run.tooManyRequests) },
        {
          label: "p50 / p95",
          value: `${this.formatMs(run.p50Ms)} / ${this.formatMs(run.p95Ms)}`,
        },
        { label: "Live logs", value: String(logCount) },
        { label: "In flight", value: String(run.inFlight) },
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
