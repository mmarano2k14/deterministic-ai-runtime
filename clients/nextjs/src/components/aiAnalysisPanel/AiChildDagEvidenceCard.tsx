"use client";

import { JSX, useEffect, useState } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisChildDagResult,
  RuntimeAnalysisHumanApprovalDecision,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiChildReanalysisCard } from "./AiChildReanalysisCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiChildDagEvidenceCardProps = {
  childDag: RuntimeAnalysisChildDagResult | undefined;
  rootExecutionId: string;
  decidingChildExecutionId: string | null;
  childApprovalError: string | null;
  onChildApprovalDecision: (
    relation: RuntimeAnalysisChildDagRelationResult,
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
  executingChildExecutionId: string | null;
  childScenarioExecutionError: string | null;
  onExecuteChildScenario: (
    relation: RuntimeAnalysisChildDagRelationResult
  ) => void;
};

type ChildDagProofState = "pass" | "pending" | "fail";

export function AiChildDagEvidenceCard(
  props: AiChildDagEvidenceCardProps
): JSX.Element | null {
  const {
    childDag,
    rootExecutionId,
    decidingChildExecutionId,
    childApprovalError,
    onChildApprovalDecision,
    executingChildExecutionId,
    childScenarioExecutionError,
    onExecuteChildScenario,
  } = props;

  const relations = Array.isArray(childDag?.relations)
    ? [...childDag.relations].sort(
        (left, right) => left.depth - right.depth
      )
    : [];

  const latestRelationKey =
    relations.length > 0
      ? relationKey(relations[relations.length - 1])
      : null;

  const [selectedRelationKey, setSelectedRelationKey] =
    useState<string | null>(latestRelationKey);

  useEffect(() => {
    // Select the deepest child on first load and only move the selection when
    // a genuinely new durable child appears. Poll refreshes for the same
    // topology do not steal a manual selection.
    if (latestRelationKey === null) {
      setSelectedRelationKey(null);
      return;
    }

    setSelectedRelationKey(latestRelationKey);
  }, [latestRelationKey]);

  if (!childDag) {
    return null;
  }

  const status =
    typeof childDag.status === "string" && childDag.status.trim().length > 0
      ? childDag.status.trim()
      : "Unknown";

  const observedDepth = Number.isFinite(childDag.observedDepth)
    ? childDag.observedDepth
    : relations.length;

  const terminal =
    status === "Completed" || status === "Failed";

  const selectedRelation =
    relations.find(
      (relation) => relationKey(relation) === selectedRelationKey
    )
    ?? relations[relations.length - 1]
    ?? null;

  return (
    <div
      className={styles.childDagEvidence}
      data-status={status.toLowerCase()}
    >
      <div className={styles.childDagHeader}>
        <div>
          <div className={styles.childDagEyebrow}>
            Investigation
          </div>
          <div className={styles.childDagTitle}>
            Approval-driven Child DAG
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
          <span>Current depth</span>
          <strong>{observedDepth}</strong>
        </div>
        <p>
          {childDag.summary ||
            "Approval-driven Child DAG evidence is projected from the durable execution relation store."}
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
            One approved decision creates one durable child execution.
            Additional depth requires another re-analysis, policy validation,
            and human approval.
          </div>
        ) : (
          relations.map((relation, index) => {
            const key = relationKey(relation);
            const selected = selectedRelationKey === key;
            const latest = index === relations.length - 1;

            return (
              <ChildDagTreeNode
                key={`${relation.depth}:${relation.childInvocationKey}`}
                relation={relation}
                selected={selected}
                latest={latest}
                onSelect={() => setSelectedRelationKey(key)}
              />
            );
          })
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

      {selectedRelation ? (
        <SelectedChildDetails
          relation={selectedRelation}
          latest={
            relationKey(selectedRelation) === latestRelationKey
          }
          isDecidingApproval={
            decidingChildExecutionId === selectedRelation.childExecutionId
          }
          approvalError={
            selectedRelation.humanApproval?.status === "Pending"
              ? childApprovalError
              : null
          }
          onApprovalDecision={(decision) =>
            onChildApprovalDecision(selectedRelation, decision)
          }
          isExecutingScenario={
            executingChildExecutionId === selectedRelation.childExecutionId
          }
          scenarioExecutionError={
            selectedRelation.scenarioExecution?.status === "Pending"
              ? childScenarioExecutionError
              : null
          }
          onExecuteScenario={() =>
            onExecuteChildScenario(selectedRelation)
          }
        />
      ) : null}
    </div>
  );
}

function ChildDagTreeNode(props: {
  relation: RuntimeAnalysisChildDagRelationResult;
  selected: boolean;
  latest: boolean;
  onSelect: () => void;
}): JSX.Element {
  const {
    relation,
    selected,
    latest,
    onSelect,
  } = props;

  const depth = Math.min(Math.max(relation.depth, 1), 5);

  return (
    <div
      className={styles.childDagRelationNode}
      data-depth={String(depth)}
      data-selected={selected}
      data-relation-status={normalizeToken(relation.relationStatus)}
    >
      <div className={styles.childDagConnector} aria-hidden="true" />

      <button
        type="button"
        className={styles.childDagTreeNodeButton}
        aria-pressed={selected}
        onClick={onSelect}
        title={`Show details for Child DAG depth ${relation.depth}`}
      >
        <div className={styles.childDagNodeTopline}>
          <span className={styles.childDagNodeToggleTitle}>
            <span className={styles.childDagNodeKind}>
              CHILD DAG
            </span>
            {latest ? (
              <span className={styles.childDagLatestBadge}>
                Latest
              </span>
            ) : null}
          </span>

          <span className={styles.childDagNodeToggleRight}>
            <span className={styles.childDagDepthBadge}>
              Depth {relation.depth}
            </span>
            <span
              className={styles.childDagTreeSelectIcon}
              aria-hidden="true"
            >
              ›
            </span>
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

        <div className={styles.childDagCompactOutcome}>
          {relation.reanalysis ? (
            <span>
              AI · {relation.reanalysis.conclusion.replaceAll("_", " ")}
            </span>
          ) : (
            <span>AI · working</span>
          )}

          {relation.verification ? (
            <span>
              Verification · {relation.verification.status}
            </span>
          ) : null}

          {relation.humanApproval?.status === "Pending" ? (
            <span className={styles.childDagNeedsAction}>
              Approval required
            </span>
          ) : null}
        </div>
      </button>
    </div>
  );
}

function SelectedChildDetails(props: {
  relation: RuntimeAnalysisChildDagRelationResult;
  latest: boolean;
  isDecidingApproval: boolean;
  approvalError: string | null;
  onApprovalDecision: (
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
  isExecutingScenario: boolean;
  scenarioExecutionError: string | null;
  onExecuteScenario: () => void;
}): JSX.Element {
  const {
    relation,
    latest,
    isDecidingApproval,
    approvalError,
    onApprovalDecision,
    isExecutingScenario,
    scenarioExecutionError,
    onExecuteScenario,
  } = props;

  return (
    <section className={styles.selectedChildPanel}>
      <div className={styles.selectedChildHeader}>
        <div>
          <div className={styles.childDagEyebrow}>
            Selected child
          </div>
          <div className={styles.selectedChildTitle}>
            Depth {relation.depth}
          </div>
        </div>

        <div className={styles.selectedChildHeaderBadges}>
          {latest ? (
            <span className={styles.childDagLatestBadge}>
              Latest
            </span>
          ) : null}
          {relation.reanalysis ? (
            <span className={styles.selectedChildConclusion}>
              {relation.reanalysis.conclusion.replaceAll("_", " ")}
            </span>
          ) : null}
        </div>
      </div>

      <div className={styles.selectedChildIdentityGrid}>
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

      {relation.childExecutionId ? (
        <AiChildReanalysisCard
          relation={relation}
          isDecidingApproval={isDecidingApproval}
          approvalError={approvalError}
          onApprovalDecision={onApprovalDecision}
          isExecutingScenario={isExecutingScenario}
          scenarioExecutionError={scenarioExecutionError}
          onExecuteScenario={onExecuteScenario}
        />
      ) : null}
    </section>
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

function relationKey(
  relation: RuntimeAnalysisChildDagRelationResult
): string {
  return relation.childExecutionId?.trim()
    || relation.childInvocationKey;
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
