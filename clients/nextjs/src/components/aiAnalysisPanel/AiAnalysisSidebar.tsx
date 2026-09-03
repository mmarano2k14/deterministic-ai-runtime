"use client";

import { JSX } from "react";
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
  onCollapsedChange: (next: boolean) => void;
};

export function AiAnalysisSidebar(props: AiAnalysisSidebarProps): JSX.Element {
  const {
    isCollapsed,
    model,
    logs,
    maxInFlight,
    rotationOverlapMs,
    api,
    onExecuteScenario,
    onCollapsedChange,
  } = props;

  return (
    <ConsoleSidePanel
      side="right"
      title="AI Runtime Analysis"
      isCollapsed={isCollapsed}
      onCollapsedChange={onCollapsedChange}
      collapsedContent={
        <button
          type="button"
          className="console-sidebar__mini-tab active"
          onClick={() => onCollapsedChange(false)}
          title="Open AI Runtime Analysis"
          aria-label="Open AI Runtime Analysis"
        >
          AI
        </button>
      }
    >
      <AiAnalysisPanel
        model={model}
        logs={logs}
        maxInFlight={maxInFlight}
        rotationOverlapMs={rotationOverlapMs}
        api={api}
        onExecuteScenario={onExecuteScenario}
      />
    </ConsoleSidePanel>
  );
}
