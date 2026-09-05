import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { AiAnalysisScope } from "./AiAnalysisType";
import { RuntimeAnalysisEvidenceBuilder } from "./RuntimeAnalysisEvidenceBuilder";
import { RuntimeAnalysisRunReader } from "./RuntimeAnalysisRunReader";
import type {
  RuntimeAnalysisScenarioInput,
  RuntimeAnalysisSnapshotRequest,
} from "./RuntimeAnalysisType";

export type RuntimeAnalysisSnapshotBuildInput = {
  scope: AiAnalysisScope;
  model: BurstRuntime;
  logs: readonly ConsoleLogEntry[];
  maxInFlight: string;
  rotationOverlapMs: string;
};

export class RuntimeAnalysisSnapshotRequestBuilder {
  public static build(
    input: RuntimeAnalysisSnapshotBuildInput
  ): RuntimeAnalysisSnapshotRequest {
    const run = RuntimeAnalysisRunReader.read(input.model);

    return {
      scope: input.scope,
      capturedAtUtc: new Date().toISOString(),
      scenario: this.buildScenario(
        input.model,
        input.maxInFlight,
        input.rotationOverlapMs
      ),
      metrics: {
        completed: run.completed,
        inFlight: run.inFlight,
        ok: run.ok,
        unauthorized: run.unauthorized,
        forbidden: run.forbidden,
        tooManyRequests: run.tooManyRequests,
        otherHttp: run.otherHttp,
        errors: run.errors,
        p50Ms: run.p50Ms,
        p95Ms: run.p95Ms,
        elapsedMs: run.elapsedMs,
        liveLogCount: input.logs.length,
      },
      evidence: RuntimeAnalysisEvidenceBuilder.build(
        input.scope,
        input.model,
        input.logs
      ),
    };
  }

  private static buildScenario(
    model: BurstRuntime,
    maxInFlight: string,
    rotationOverlapMs: string
  ): RuntimeAnalysisScenarioInput | undefined {
    const config = model.report?.config;

    if (!config) {
      return undefined;
    }

    return {
      name: config.dispatchMode,
      dispatchMode: config.dispatchMode,
      planKey: config.planKey,
      totalRequests: config.total,
      concurrency:
        "concurrency" in config ? config.concurrency : undefined,
      batchSize: "batchSize" in config ? config.batchSize : undefined,
      delayMs: config.delayMs,
      wavePauseMs:
        "wavePauseMs" in config ? config.wavePauseMs : undefined,
      maxInFlight: this.parseNonNegativeInteger(maxInFlight),
      rotationOverlapMs: this.parseNonNegativeInteger(rotationOverlapMs),
    };
  }

  private static parseNonNegativeInteger(value: string): number | undefined {
    const parsed = Number.parseInt(value, 10);

    if (!Number.isFinite(parsed) || parsed < 0) {
      return undefined;
    }

    return parsed;
  }
}
