import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { AiAnalysisScope } from "./AiAnalysisType";

export class RuntimeAnalysisLogScopeSelector {
  private static readonly RecentWindowMs = 30_000;
  private static readonly CompletedRunEvidenceGraceMs = 1_000;

  public static select(
    scope: AiAnalysisScope,
    model: BurstRuntime,
    logs: readonly ConsoleLogEntry[]
  ): readonly ConsoleLogEntry[] {
    if (scope === "last-30s") {
      return this.selectLast30Seconds(logs);
    }

    if (scope === "current-run") {
      return this.selectCurrentRun(model, logs);
    }

    return [];
  }

  private static selectCurrentRun(
    model: BurstRuntime,
    logs: readonly ConsoleLogEntry[]
  ): readonly ConsoleLogEntry[] {
    const timing = model.report?.timing;
    const startedAt = timing?.startedAt;

    if (startedAt === undefined) {
      return [];
    }

    const finishedAt = timing?.finishedAt;

    const evidenceFinishedAt =
      finishedAt === undefined
        ? Date.now()
        : finishedAt + this.CompletedRunEvidenceGraceMs;

    return logs.filter((log) => {
      const timestamp = this.readTimestamp(log.t);

      return (
        timestamp !== undefined &&
        timestamp >= startedAt &&
        timestamp <= evidenceFinishedAt
      );
    });
  }

  private static selectLast30Seconds(
    logs: readonly ConsoleLogEntry[]
  ): readonly ConsoleLogEntry[] {
    const now = Date.now();
    const threshold = now - this.RecentWindowMs;

    return logs.filter((log) => {
      const timestamp = this.readTimestamp(log.t);

      return (
        timestamp !== undefined &&
        timestamp >= threshold &&
        timestamp <= now
      );
    });
  }

  private static readTimestamp(value: string): number | undefined {
    const timestamp = Date.parse(value);

    return Number.isNaN(timestamp) ? undefined : timestamp;
  }
}
