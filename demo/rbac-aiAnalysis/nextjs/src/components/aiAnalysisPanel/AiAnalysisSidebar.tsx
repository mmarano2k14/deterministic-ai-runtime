"use client";

import { JSX, useEffect, useState } from "react";
import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type { RuntimeAnalysisSuggestedScenario } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { ConsoleSidePanel } from "@/components/ui/ConsoleSidePanel";
import { AiAnalysisPanel } from "./AiAnalysisPanel";

export type AiAnalysisSidebarProps = {
  isCollapsed: boolean;
  model: BurstRuntime;
  logs: readonly ConsoleLogEntry[];
  maxInFlight: string;
  rotationOverlapMs: string;
  api: MultiplexedRbacApi;
  onExecuteScenario: (
    scenario: RuntimeAnalysisSuggestedScenario,
    planKey: string
  ) => Promise<void>;
  onBusyChange?: (busy: boolean) => void;
  onCollapsedChange: (next: boolean) => void;
};

type AiCollapsedStatus =
  | "idle"
  | "working"
  | "ready"
  | "error";

function AiCollapsedStatusButton(props: {
  logs: readonly ConsoleLogEntry[];
  onClick: () => void;
}): JSX.Element {
  const { logs, onClick } = props;
  const status = resolveAiCollapsedStatus(logs);

  return (
    <button
      type="button"
      className="console-sidebar__mini-tab active ai-sidebar-mini-tab"
      data-ai-status={status}
      onClick={onClick}
      title={aiCollapsedTitle(status)}
      aria-label={aiCollapsedTitle(status)}
    >
      <span>AI</span>

      {status !== "idle" ? (
        <span
          className="ai-sidebar-mini-tab__status"
          aria-hidden="true"
        >
          {aiCollapsedGlyph(status)}
        </span>
      ) : null}
    </button>
  );
}

function resolveAiCollapsedStatus(
  logs: readonly ConsoleLogEntry[]
): AiCollapsedStatus {
  const terminalActivityIds = new Set<string>();
  let latestTerminalStatus: AiCollapsedStatus | null = null;

  for (const log of logs) {
    if (log.kind !== "realtime") {
      continue;
    }

    const category = (log.category ?? "")
      .trim()
      .toLowerCase();

    if (
      category !== "demo.ui.ai.started"
      && category !== "demo.ui.ai.completed"
      && category !== "demo.ui.ai.failed"
    ) {
      continue;
    }

    const activityId = readActivityId(
      log.data ?? log.payload
    );

    if (!activityId) {
      continue;
    }

    if (category === "demo.ui.ai.completed") {
      terminalActivityIds.add(activityId);
      latestTerminalStatus ??= "ready";
      continue;
    }

    if (category === "demo.ui.ai.failed") {
      terminalActivityIds.add(activityId);
      latestTerminalStatus ??= "error";
      continue;
    }

    if (!terminalActivityIds.has(activityId)) {
      return "working";
    }
  }

  return latestTerminalStatus ?? "idle";
}

function readActivityId(value: unknown): string | null {
  let candidate = value;

  if (typeof candidate === "string") {
    try {
      candidate = JSON.parse(candidate);
    } catch {
      return null;
    }
  }

  if (
    typeof candidate !== "object"
    || candidate === null
    || Array.isArray(candidate)
  ) {
    return null;
  }

  const record = candidate as Record<string, unknown>;
  const matchingKey = Object.keys(record).find(
    (key) => key.toLowerCase() === "activityid"
  );

  if (!matchingKey) {
    return null;
  }

  const raw = record[matchingKey];

  return typeof raw === "string" && raw.trim()
    ? raw.trim()
    : null;
}

function aiCollapsedGlyph(
  status: Exclude<AiCollapsedStatus, "idle">
): string {
  switch (status) {
    case "working":
      return "●";
    case "ready":
      return "✓";
    case "error":
      return "!";
  }
}

function aiCollapsedTitle(status: AiCollapsedStatus): string {
  switch (status) {
    case "working":
      return "AI is working — open AI Runtime Analysis";
    case "ready":
      return "AI finding available — open AI Runtime Analysis";
    case "error":
      return "AI needs attention — open AI Runtime Analysis";
    default:
      return "Open AI Runtime Analysis";
  }
}

export function AiAnalysisSidebar(props: AiAnalysisSidebarProps): JSX.Element {
  const {
    isCollapsed,
    model,
    logs,
    maxInFlight,
    rotationOverlapMs,
    api,
    onExecuteScenario,
    onBusyChange,
    onCollapsedChange,
  } = props;

  const [panelBusy, setPanelBusy] = useState(false);
  const realtimeBusy =
    resolveAiCollapsedStatus(logs) === "working";
  const isAiBusy = panelBusy || realtimeBusy;

  useEffect(() => {
    onBusyChange?.(isAiBusy);
  }, [isAiBusy, onBusyChange]);

  useEffect(() => {
    return () => {
      onBusyChange?.(false);
    };
  }, [onBusyChange]);

  return (
    <ConsoleSidePanel
      side="right"
      title="AI Runtime Analysis"
      isCollapsed={isCollapsed}
      onCollapsedChange={onCollapsedChange}
      collapsedContent={
        <AiCollapsedStatusButton
          logs={logs}
          onClick={() => onCollapsedChange(false)}
        />
      }
    >
      <AiAnalysisPanel
        model={model}
        logs={logs}
        maxInFlight={maxInFlight}
        rotationOverlapMs={rotationOverlapMs}
        api={api}
        onExecuteScenario={onExecuteScenario}
        onBusyChange={setPanelBusy}
      />
    </ConsoleSidePanel>
  );
}
