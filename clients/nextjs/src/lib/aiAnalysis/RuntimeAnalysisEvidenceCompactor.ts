import type { RuntimeAnalysisEvidenceInput } from "./RuntimeAnalysisType";

type RuntimeAnalysisEvidenceGroup = {
  firstTimestampUtc: string;
  lastTimestampUtc: string;
  evidence: RuntimeAnalysisEvidenceInput;
  occurrences: number;
};

export class RuntimeAnalysisEvidenceCompactor {
  public static compact(
    evidence: readonly RuntimeAnalysisEvidenceInput[]
  ): RuntimeAnalysisEvidenceInput[] {
    const groups = new Map<string, RuntimeAnalysisEvidenceGroup>();
    const passthrough: RuntimeAnalysisEvidenceInput[] = [];

    for (const item of evidence) {
      if (this.hasCorrelationIdentity(item)) {
        passthrough.push(item);
        continue;
      }

      const key = this.groupKey(item);
      const existing = groups.get(key);

      if (!existing) {
        groups.set(key, {
          firstTimestampUtc: item.timestampUtc,
          lastTimestampUtc: item.timestampUtc,
          evidence: item,
          occurrences: 1,
        });
        continue;
      }

      existing.occurrences += 1;

      if (
        Date.parse(item.timestampUtc) <
        Date.parse(existing.firstTimestampUtc)
      ) {
        existing.firstTimestampUtc = item.timestampUtc;
      }

      if (
        Date.parse(item.timestampUtc) >
        Date.parse(existing.lastTimestampUtc)
      ) {
        existing.lastTimestampUtc = item.timestampUtc;
        existing.evidence = item;
      }
    }

    const compacted = Array.from(groups.values()).map((group) =>
      this.toCompactedEvidence(group)
    );

    return [...passthrough, ...compacted];
  }

  private static toCompactedEvidence(
    group: RuntimeAnalysisEvidenceGroup
  ): RuntimeAnalysisEvidenceInput {
    if (group.occurrences === 1) {
      return group.evidence;
    }

    return {
      ...group.evidence,
      metadata: {
        ...(group.evidence.metadata ?? {}),
        occurrences: String(group.occurrences),
        firstSeenUtc: group.firstTimestampUtc,
        lastSeenUtc: group.lastTimestampUtc,
      },
    };
  }

  private static hasCorrelationIdentity(
    evidence: RuntimeAnalysisEvidenceInput
  ): boolean {
    return Boolean(
      evidence.correlationId ||
        evidence.sharedRunId ||
        evidence.executionId ||
        evidence.dagId ||
        evidence.stepId ||
        evidence.childExecutionId ||
        evidence.policyKey
    );
  }

  private static groupKey(
    evidence: RuntimeAnalysisEvidenceInput
  ): string {
    return [
      evidence.category,
      evidence.eventType,
      evidence.statusCode ?? "",
      evidence.message ?? "",
      evidence.metadata?.level ?? "",
    ]
      .join("|")
      .toLowerCase();
  }
}
