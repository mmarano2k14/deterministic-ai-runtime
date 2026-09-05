import { BurstRuntime } from "../runtime/BurstMachineType";
import { BurstRun } from "./BurstRun";

export class BurstRunFactory {
  static fromRuntime(runtime: BurstRuntime, basedOnRunId?: string): BurstRun | null {
    if (!runtime.report) return null;

    return {
      id: crypto.randomUUID(),
      createdAt: new Date().toISOString(),
      report: structuredClone(runtime.report),
      basedOnRunId,
    };
  }
}