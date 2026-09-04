import { JSX } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisChildDagResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiChildDagEvidenceCardProps = {
  childDag: RuntimeAnalysisChildDagResult | undefined;
  rootExecutionId: string;
};

type ChildDagProofState = "pass" | "pending" | "fail";

export function AiChildDagEvidenceCard(
  props: AiChildDagEvidenceCardProps
): JSX.Element | null {
  const { childDag, rootExecutionId } = props;

  if (!childDag) {
    return null;
  }

  const status =
    typeof childDag.status === "string" && childDag.status.trim().length > 0
      ? childDag.status.trim()
      : "Unknown";

  const relations = Array.isArray(childDag.relations)
    ? [...childDag.relations].sort(
        (left, right) => left.depth - right.depth
      )
    : [];

  const observedDepth = Number.isFinite(childDag.observedDepth)
    ? childDag.observedDepth
    : relations.length;

  const expectedDepth =
    Number.isFinite(childDag.expectedDepth) && childDag.expectedDepth > 0
      ? childDag.expectedDepth
      : null;

  const terminal =
    status === "Completed" || status === "Failed";

  return (
    <div
      className={styles.childDagEvidence}
      data-status={status.toLowerCase()}
    >
      <div className={styles.childDagHeader}>
        <div>
          <div className={styles.childDagEyebrow}>
            Current DAG / execution
          </div>
          <div className={styles.childDagTitle}>
            Native recursive Child DAG
          </div>
        </div>

        <div
          className={styles.childDagStatus}
          data-status={status.toLowerCase()}
        >
          {status}
        </div>
      </div>

      <div className={styles.childDagSummaryRow}>
        <div>
          <span>Observed depth</span>
          <strong>
            {observedDepth}
            {expectedDepth !== null ? ` / ${expectedDepth}` : ""}
          </strong>
        </div>
        <p>
          {childDag.summary ||
            "Runtime Child DAG evidence is being projected from the durable execution relation store."}
        </p>
      </div>

      <div className={styles.childDagTree}>
        <div className={styles.childDagRootNode}>
          <div className={styles.childDagNodeTopline}>
            <span className={styles.childDagNodeKind}>ROOT EXECUTION</span>
            <span className={styles.childDagDepthBadge}>Depth 0</span>
          </div>
          <strong title={rootExecutionId}>
            {shortId(rootExecutionId)}
          </strong>
          <span className={styles.childDagNodeMeta}>
            runtime-analysis
          </span>
        </div>

        {relations.length === 0 ? (
          <div className={styles.childDagNotStarted}>
            Child relations will appear here after the approved scenario
            reaches the native <code>execution.child-dag</code> step.
          </div>
        ) : (
          relations.map((relation) => (
            <ChildDagRelationNode
              key={`${relation.depth}:${relation.childInvocationKey}`}
              relation={relation}
            />
          ))
        )}
      </div>

      {relations.length > 0 ? (
        <div className={styles.childDagProofGrid}>
          <Proof
            label="Relations completed"
            state={proofState(
              childDag.allRelationsCompleted,
              terminal,
              relations.length
            )}
          />
          <Proof
            label="Continuations resumed"
            state={continuationProofState(
              childDag,
              relations
            )}
          />
          <Proof
            label="Invocation generation = 0"
            state={proofState(
              childDag.allInvocationGenerationsZero,
              terminal,
              relations.length
            )}
          />
          <Proof
            label="Child ExecutionIds unique"
            state={proofState(
              childDag.childExecutionIdsUnique,
              terminal,
              relations.length
            )}
          />
        </div>
      ) : null}
    </div>
  );
}

function ChildDagRelationNode(props: {
  relation: RuntimeAnalysisChildDagRelationResult;
}): JSX.Element {
  const { relation } = props;
  const depth = Math.min(Math.max(relation.depth, 1), 3);

  return (
    <div
      className={styles.childDagRelationNode}
      data-depth={String(depth)}
      data-relation-status={normalizeToken(relation.relationStatus)}
    >
      <div className={styles.childDagConnector} aria-hidden="true" />

      <div className={styles.childDagNodeTopline}>
        <span className={styles.childDagNodeKind}>
          CHILD DAG
        </span>
        <span className={styles.childDagDepthBadge}>
          Depth {relation.depth}
        </span>
      </div>

      <div className={styles.childDagNodeIdentity}>
        <div>
          <span>Child ExecutionId</span>
          <strong title={relation.childExecutionId ?? ""}>
            {relation.childExecutionId
              ? shortId(relation.childExecutionId)
              : "pending"}
          </strong>
        </div>
        <div>
          <span>Parent ExecutionId</span>
          <strong title={relation.parentExecutionId}>
            {shortId(relation.parentExecutionId)}
          </strong>
        </div>
      </div>

      <div className={styles.childDagNodeFacts}>
        <span
          className={styles.childDagFact}
          data-state={statusState(relation.relationStatus)}
        >
          Relation · {relation.relationStatus || "Unknown"}
        </span>
        <span
          className={styles.childDagFact}
          data-state={statusState(relation.continuationStatus)}
        >
          Continuation · {relation.continuationStatus || "Unknown"}
        </span>
        <span
          className={styles.childDagFact}
          data-state={relation.invocationGeneration === 0 ? "pass" : "fail"}
        >
          Generation · {relation.invocationGeneration}
        </span>
      </div>

      <div className={styles.childDagNodeDetails}>
        <div>
          <span>DAG</span>
          <strong title={relation.childDagId}>
            {relation.childDagId}
          </strong>
        </div>
        <div>
          <span>Invocation key</span>
          <strong title={relation.childInvocationKey}>
            {relation.childInvocationKey}
          </strong>
        </div>
      </div>

      {relation.childFailureReason ? (
        <div className={styles.childDagFailure}>
          {relation.childFailureReason}
        </div>
      ) : null}
    </div>
  );
}

function Proof(props: {
  label: string;
  state: ChildDagProofState;
}): JSX.Element {
  const { label, state } = props;

  return (
    <div className={styles.childDagProof} data-state={state}>
      <span>{label}</span>
      <strong>{proofLabel(state)}</strong>
    </div>
  );
}

function proofLabel(state: ChildDagProofState): string {
  switch (state) {
    case "pass":
      return "PASS";
    case "fail":
      return "FAIL";
    default:
      return "PENDING";
  }
}

function proofState(
  value: boolean,
  terminal: boolean,
  relationCount: number
): ChildDagProofState {
  if (relationCount === 0) {
    return "pending";
  }

  if (value) {
    return "pass";
  }

  return terminal ? "fail" : "pending";
}

function continuationProofState(
  childDag: RuntimeAnalysisChildDagResult,
  relations: RuntimeAnalysisChildDagRelationResult[]
): ChildDagProofState {
  if (relations.length === 0) {
    return "pending";
  }

  if (childDag.allContinuationsResumed) {
    return "pass";
  }

  const normalizedStatuses = relations.map((relation) =>
    normalizeToken(relation.continuationStatus)
  );

  if (normalizedStatuses.some((status) => status === "suppressed")) {
    return "fail";
  }

  // Pending and Scheduled are valid durable continuation states.
  // Scheduled means the continuation has been durably recorded and may be
  // safely re-enqueued; it is not proof of failure. Keep the aggregate
  // evidence pending until every relation reaches Resumed.
  if (
    normalizedStatuses.every(
      (status) =>
        status === "resumed" ||
        status === "scheduled" ||
        status === "pending" ||
        status === "none"
    )
  ) {
    return "pending";
  }

  return childDag.status === "Failed" ? "fail" : "pending";
}

function statusState(value: string): "pass" | "pending" | "fail" {
  const normalized = normalizeToken(value);

  if (
    normalized === "completed" ||
    normalized === "resumed" ||
    normalized === "consumed"
  ) {
    return "pass";
  }

  if (
    normalized === "failed" ||
    normalized === "rejected" ||
    normalized === "faulted"
  ) {
    return "fail";
  }

  return "pending";
}

function normalizeToken(value: string): string {
  return typeof value === "string"
    ? value.trim().toLowerCase()
    : "unknown";
}

function shortId(value: string): string {
  const normalized = value.trim();

  if (normalized.length <= 18) {
    return normalized || "—";
  }

  return `${normalized.slice(0, 9)}…${normalized.slice(-7)}`;
}
