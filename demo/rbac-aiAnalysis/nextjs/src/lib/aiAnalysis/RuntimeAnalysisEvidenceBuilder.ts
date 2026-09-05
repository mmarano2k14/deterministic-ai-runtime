import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { AiAnalysisScope } from "./AiAnalysisType";
import { RuntimeAnalysisEvidenceCompactor } from "./RuntimeAnalysisEvidenceCompactor";
import { RuntimeAnalysisEvidenceMapper } from "./RuntimeAnalysisEvidenceMapper";
import { RuntimeAnalysisEvidenceSelector } from "./RuntimeAnalysisEvidenceSelector";
import { RuntimeAnalysisLogScopeSelector } from "./RuntimeAnalysisLogScopeSelector";
import type { RuntimeAnalysisEvidenceInput } from "./RuntimeAnalysisType";

export class RuntimeAnalysisEvidenceBuilder {
  public static build(
    scope: AiAnalysisScope,
    model: BurstRuntime,
    logs: readonly ConsoleLogEntry[]
  ): RuntimeAnalysisEvidenceInput[] {
    const scopedLogs = RuntimeAnalysisLogScopeSelector.select(
      scope,
      model,
      logs
    );

    const mapped = scopedLogs
      .map((log) => RuntimeAnalysisEvidenceMapper.map(log))
      .filter(
        (evidence): evidence is RuntimeAnalysisEvidenceInput =>
          evidence !== null
      );

    const compacted =
      RuntimeAnalysisEvidenceCompactor.compact(mapped);

    return RuntimeAnalysisEvidenceSelector.select(compacted);
  }
}
