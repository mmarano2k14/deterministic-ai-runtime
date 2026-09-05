"use client";

import { JSX, useEffect, useState } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisChildDagResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiChildDagTreeDetailsModalProps = {
  isOpen: boolean;
  childDag: RuntimeAnalysisChildDagResult;
  rootExecutionId: string;
  onClose: () => void;
};

export function AiChildDagTreeDetailsModal(
  props: AiChildDagTreeDetailsModalProps
): JSX.Element | null {
  const {
    isOpen,
    childDag,
    rootExecutionId,
    onClose,
  } = props;

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    function handleKeyDown(event: KeyboardEvent): void {
      if (event.key === "Escape") {
        onClose();
      }
    }

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [isOpen, onClose]);

  if (!isOpen) {
    return null;
  }

  const relations = Array.isArray(childDag.relations)
    ? [...childDag.relations].sort(
        (left, right) => left.depth - right.depth
      )
    : [];

  const status =
    typeof childDag.status === "string"
    && childDag.status.trim().length > 0
      ? childDag.status.trim()
      : "Unknown";

  const observedDepth = Number.isFinite(childDag.observedDepth)
    ? childDag.observedDepth
    : relations.length;

  return (
    <div
      className="ai-analysis-child-dag-tree-modal-overlay"
      onClick={onClose}
    >
      <section
        className="ai-analysis-child-dag-tree-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="ai-child-dag-tree-modal-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className="ai-analysis-child-dag-tree-modal-header">
          <div>
            <div className="ai-analysis-child-dag-tree-modal-eyebrow">
              Durable investigation
            </div>
            <h2
              id="ai-child-dag-tree-modal-title"
              className="ai-analysis-child-dag-tree-modal-title"
            >
              Full Child DAG tree
            </h2>
            <p className="ai-analysis-child-dag-tree-modal-subtitle">
              Root execution + {relations.length} durable child
              {relations.length === 1 ? "" : "ren"} · Depth {observedDepth}
            </p>
          </div>

          <div className="ai-analysis-child-dag-tree-modal-header-actions">
            <span
              className="ai-analysis-child-dag-status"
              data-status={status.toLowerCase()}
            >
              {status}
            </span>

            <button
              type="button"
              className="ai-analysis-child-dag-tree-modal-close"
              onClick={onClose}
            >
              Close
            </button>
          </div>
        </header>

        <div className="ai-analysis-child-dag-tree-modal-summary">
          <div>
            <span>Current depth</span>
            <strong>{observedDepth}</strong>
          </div>

          <p>
            {childDag.summary
              || "Durable Child DAG relations projected from the current runtime-analysis execution."}
          </p>
        </div>

        <div className="ai-analysis-child-dag-tree-modal-tree">
          <div className="ai-analysis-child-dag-tree-modal-root">
            <div className="ai-analysis-child-dag-tree-modal-node-topline">
              <span>ROOT EXECUTION</span>
              <strong>Depth 0</strong>
            </div>

            <code title={rootExecutionId}>
              {rootExecutionId}
            </code>

            <span>runtime-analysis</span>
          </div>

          {relations.map((relation, index) => (
            <ChildDagModalNode
              key={`${relation.depth}:${relation.childInvocationKey}`}
              relation={relation}
              latest={index === relations.length - 1}
            />
          ))}
        </div>

        <div className="ai-analysis-child-dag-tree-modal-proof-grid">
          <ModalProof
            label="Relations completed"
            value={childDag.allRelationsCompleted}
          />
          <ModalProof
            label="Continuations resumed"
            value={childDag.allContinuationsResumed}
          />
          <ModalProof
            label="Invocation generation = 0"
            value={childDag.allInvocationGenerationsZero}
          />
          <ModalProof
            label="Child ExecutionIds unique"
            value={childDag.childExecutionIdsUnique}
          />
        </div>
      </section>
    </div>
  );
}

function ChildDagModalNode(props: {
  relation: RuntimeAnalysisChildDagRelationResult;
  latest: boolean;
}): JSX.Element {
  const {
    relation,
    latest,
  } = props;

  const [expanded, setExpanded] = useState(latest);
  const depth = Math.min(Math.max(relation.depth, 1), 5);

  return (
    <article
      className="ai-analysis-child-dag-tree-modal-node"
      data-depth={String(depth)}
      data-expanded={expanded}
    >
      <div
        className="ai-analysis-child-dag-tree-modal-connector"
        aria-hidden="true"
      />

      <button
        type="button"
        className="ai-analysis-child-dag-tree-modal-node-toggle"
        aria-expanded={expanded}
        onClick={() => setExpanded((current) => !current)}
      >
        <span className="ai-analysis-child-dag-tree-modal-node-heading">
          <span className="ai-analysis-child-dag-tree-modal-node-kind">
            CHILD DAG
          </span>

          {latest ? (
            <span className="ai-analysis-child-dag-latest-badge">
              Latest
            </span>
          ) : null}
        </span>

        <span className="ai-analysis-child-dag-tree-modal-node-right">
          <span className="ai-analysis-child-dag-depth-badge">
            Depth {relation.depth}
          </span>

          <span
            className="ai-analysis-child-dag-tree-modal-chevron"
            aria-hidden="true"
          >
            ▾
          </span>
        </span>
      </button>

      <div className="ai-analysis-child-dag-tree-modal-node-preview">
        <div>
          <span>Child</span>
          <strong title={relation.childExecutionId ?? ""}>
            {relation.childExecutionId
              ? shortId(relation.childExecutionId)
              : "pending"}
          </strong>
        </div>

        <div>
          <span>Parent</span>
          <strong title={relation.parentExecutionId}>
            {shortId(relation.parentExecutionId)}
          </strong>
        </div>

        <div className="ai-analysis-child-dag-tree-modal-node-statuses">
          <StatusPill
            label={`Relation · ${relation.relationStatus || "Unknown"}`}
            state={statusState(relation.relationStatus)}
          />
          <StatusPill
            label={`Continuation · ${relation.continuationStatus || "Unknown"}`}
            state={statusState(relation.continuationStatus)}
          />
          <StatusPill
            label={`Generation · ${relation.invocationGeneration}`}
            state={relation.invocationGeneration === 0 ? "pass" : "fail"}
          />
        </div>
      </div>

      {expanded ? (
        <div className="ai-analysis-child-dag-tree-modal-node-details">
          <DetailGrid relation={relation} />

          <div className="ai-analysis-child-dag-tree-modal-stage-grid">
            <Stage
              label="AI"
              value={
                relation.reanalysis
                  ? relation.reanalysis.conclusion.replaceAll("_", " ")
                  : "Working / not available"
              }
              detail={
                relation.reanalysis
                  ? `${Math.round(relation.reanalysis.confidence * 100)}% confidence · ${
                      relation.reanalysis.shouldContinue
                        ? "follow-up proposed"
                        : "no further experiment"
                    }`
                  : null
              }
            />

            <Stage
              label="Policy"
              value={
                relation.policyValidation
                  ? relation.policyValidation.allowed
                    ? "ALLOWED"
                    : "DENIED"
                  : "Not evaluated"
              }
            />

            <Stage
              label="Human approval"
              value={relation.humanApproval?.status ?? "Not required yet"}
            />

            <Stage
              label="Scenario execution"
              value={relation.scenarioExecution?.status ?? "Not started"}
            />

            <Stage
              label="Verification"
              value={relation.verification?.status ?? "Not available"}
              detail={relation.verification?.summary ?? null}
            />

            <Stage
              label="Investigation mode"
              value={
                relation.investigationMode === "continue-useful-experiments"
                  ? "Continue with another useful experiment"
                  : "Stop when conclusion is strong"
              }
            />
          </div>

          {relation.reanalysis?.summary ? (
            <div className="ai-analysis-child-dag-tree-modal-note">
              <span>AI re-analysis</span>
              <p>{relation.reanalysis.summary}</p>
            </div>
          ) : null}

          {relation.childFailureReason ? (
            <div className="ai-analysis-child-dag-tree-modal-failure">
              {relation.childFailureReason}
            </div>
          ) : null}
        </div>
      ) : null}
    </article>
  );
}

function DetailGrid(props: {
  relation: RuntimeAnalysisChildDagRelationResult;
}): JSX.Element {
  const { relation } = props;

  return (
    <div className="ai-analysis-child-dag-tree-modal-detail-grid">
      <Detail label="Child ExecutionId" value={relation.childExecutionId} />
      <Detail label="Parent ExecutionId" value={relation.parentExecutionId} />
      <Detail label="DAG" value={relation.childDagId} />
      <Detail label="DAG version" value={relation.childDagDefinitionVersion} />
      <Detail label="Invocation key" value={relation.childInvocationKey} />
      <Detail label="Runtime status" value={relation.runtimeStatus} />
      <Detail label="Current step" value={relation.currentStep} />
      <Detail label="Tenant" value={relation.tenantId} />
      <Detail label="Created" value={formatTimestamp(relation.createdAtUtc)} />
      <Detail
        label="Completed"
        value={formatTimestamp(relation.completedAtUtc)}
      />
      <Detail
        label="Parent resumed"
        value={formatTimestamp(relation.parentResumedAtUtc)}
      />
      <Detail label="Result digest" value={relation.childResultDigest} />
    </div>
  );
}

function Detail(props: {
  label: string;
  value: string | null | undefined;
}): JSX.Element {
  return (
    <div>
      <span>{props.label}</span>
      <strong title={props.value ?? ""}>
        {props.value?.trim() || "—"}
      </strong>
    </div>
  );
}

function Stage(props: {
  label: string;
  value: string;
  detail?: string | null;
}): JSX.Element {
  return (
    <div>
      <span>{props.label}</span>
      <strong>{props.value}</strong>
      {props.detail ? <small>{props.detail}</small> : null}
    </div>
  );
}

function StatusPill(props: {
  label: string;
  state: "pass" | "pending" | "fail";
}): JSX.Element {
  return (
    <span
      className="ai-analysis-child-dag-fact"
      data-state={props.state}
    >
      {props.label}
    </span>
  );
}

function ModalProof(props: {
  label: string;
  value: boolean;
}): JSX.Element {
  return (
    <div
      className="ai-analysis-child-dag-tree-modal-proof"
      data-state={props.value ? "pass" : "pending"}
    >
      <span>{props.label}</span>
      <strong>{props.value ? "PASS" : "PENDING"}</strong>
    </div>
  );
}

function statusState(value: string): "pass" | "pending" | "fail" {
  const normalized = value.trim().toLowerCase();

  if (
    normalized === "completed"
    || normalized === "resumed"
    || normalized === "consumed"
  ) {
    return "pass";
  }

  if (
    normalized === "failed"
    || normalized === "rejected"
    || normalized === "faulted"
    || normalized === "suppressed"
  ) {
    return "fail";
  }

  return "pending";
}

function shortId(value: string): string {
  const normalized = value.trim();

  if (normalized.length <= 18) {
    return normalized || "—";
  }

  return `${normalized.slice(0, 9)}…${normalized.slice(-7)}`;
}

function formatTimestamp(value: string | null): string {
  if (!value) {
    return "—";
  }

  const timestamp = Date.parse(value);

  if (Number.isNaN(timestamp)) {
    return value;
  }

  return new Date(timestamp).toLocaleString();
}
