import type { RuntimeAnalysisEvidenceInput } from "./RuntimeAnalysisType";

type RuntimeAnalysisEvidenceSelectionRule = {
  bucket: string;
  priority: number;
  maxItems: number;
};

type RuntimeAnalysisPrioritizedEvidence = {
  evidence: RuntimeAnalysisEvidenceInput;
  priority: number;
};

export class RuntimeAnalysisEvidenceSelector {
  private static readonly MaxEvidenceItems = 80;

  public static select(
    evidence: readonly RuntimeAnalysisEvidenceInput[]
  ): RuntimeAnalysisEvidenceInput[] {
    const buckets = new Map<
      string,
      {
        rule: RuntimeAnalysisEvidenceSelectionRule;
        items: RuntimeAnalysisEvidenceInput[];
      }
    >();

    for (const item of evidence) {
      const rule = this.ruleFor(item);
      const existing = buckets.get(rule.bucket);

      if (existing) {
        existing.items.push(item);
      } else {
        buckets.set(rule.bucket, {
          rule,
          items: [item],
        });
      }
    }

    const prioritized: RuntimeAnalysisPrioritizedEvidence[] = [];

    for (const bucket of buckets.values()) {
      const sampled = this.sampleEvenly(
        bucket.items,
        bucket.rule.maxItems
      );

      for (const item of sampled) {
        prioritized.push({
          evidence: item,
          priority: bucket.rule.priority,
        });
      }
    }

    const selected = prioritized
      .sort((left, right) => {
        if (left.priority !== right.priority) {
          return right.priority - left.priority;
        }

        return (
          Date.parse(left.evidence.timestampUtc) -
          Date.parse(right.evidence.timestampUtc)
        );
      })
      .slice(0, this.MaxEvidenceItems)
      .map((item) => item.evidence);

    return selected.sort(
      (left, right) =>
        Date.parse(left.timestampUtc) - Date.parse(right.timestampUtc)
    );
  }

  private static ruleFor(
    evidence: RuntimeAnalysisEvidenceInput
  ): RuntimeAnalysisEvidenceSelectionRule {
    if (this.isStructuredRuntimeEvidence(evidence)) {
      return {
        bucket: `runtime:${evidence.eventType}`,
        priority: 100,
        maxItems: 30,
      };
    }

    if (this.isCriticalFailure(evidence)) {
      return {
        bucket: `critical:${evidence.eventType}:${evidence.statusCode ?? ""}`,
        priority: 95,
        maxItems: 20,
      };
    }

    if (evidence.statusCode === 401 || evidence.statusCode === 403) {
      return {
        bucket: `authorization:${evidence.eventType}:${evidence.statusCode}`,
        priority: 92,
        maxItems: 20,
      };
    }

    if (
      evidence.statusCode === 429 ||
      this.containsToken(evidence, "concurrency")
    ) {
      return {
        bucket: "concurrency",
        priority: 90,
        maxItems: 20,
      };
    }

    if (evidence.eventType === "context.rotated") {
      return {
        bucket: "context:rotated",
        priority: 75,
        maxItems: 10,
      };
    }

    if (
      evidence.eventType === "context.attached" ||
      evidence.eventType === "context.released"
    ) {
      return {
        bucket: `context:${evidence.eventType}`,
        priority: 55,
        maxItems: 3,
      };
    }

    if (
      evidence.eventType === "request.completed" &&
      typeof evidence.statusCode === "number" &&
      evidence.statusCode < 400
    ) {
      return {
        bucket: "http:success",
        priority: 40,
        maxItems: 10,
      };
    }

    return {
      bucket: `other:${evidence.category}:${evidence.eventType}`,
      priority: 30,
      maxItems: 6,
    };
  }

  private static isStructuredRuntimeEvidence(
    evidence: RuntimeAnalysisEvidenceInput
  ): boolean {
    if (
      evidence.sharedRunId ||
      evidence.executionId ||
      evidence.dagId ||
      evidence.stepId ||
      evidence.childExecutionId ||
      evidence.policyKey
    ) {
      return true;
    }

    return (
      this.containsToken(evidence, "dag") ||
      this.containsToken(evidence, "step") ||
      this.containsToken(evidence, "child") ||
      this.containsToken(evidence, "policy") ||
      this.containsToken(evidence, "recovery") ||
      this.containsToken(evidence, "replay")
    );
  }

  private static isCriticalFailure(
    evidence: RuntimeAnalysisEvidenceInput
  ): boolean {
    if (
      typeof evidence.statusCode === "number" &&
      evidence.statusCode >= 500
    ) {
      return true;
    }

    return (
      this.containsToken(evidence, "failed") ||
      this.containsToken(evidence, "failure") ||
      this.containsToken(evidence, "error") ||
      this.containsToken(evidence, "exception") ||
      this.containsToken(evidence, "timeout") ||
      this.containsToken(evidence, "denied")
    );
  }

  private static containsToken(
    evidence: RuntimeAnalysisEvidenceInput,
    token: string
  ): boolean {
    return (
      evidence.category.toLowerCase().includes(token) ||
      evidence.eventType.toLowerCase().includes(token) ||
      evidence.message?.toLowerCase().includes(token) === true
    );
  }

  private static sampleEvenly(
    items: readonly RuntimeAnalysisEvidenceInput[],
    maxItems: number
  ): RuntimeAnalysisEvidenceInput[] {
    const ordered = [...items].sort(
      (left, right) =>
        Date.parse(left.timestampUtc) - Date.parse(right.timestampUtc)
    );

    if (ordered.length <= maxItems) {
      return ordered;
    }

    if (maxItems === 1) {
      return [ordered[ordered.length - 1]];
    }

    const sampled: RuntimeAnalysisEvidenceInput[] = [];
    const lastIndex = ordered.length - 1;

    for (let index = 0; index < maxItems; index += 1) {
      const position = Math.round(
        (index * lastIndex) / (maxItems - 1)
      );

      sampled.push(ordered[position]);
    }

    return sampled;
  }
}
