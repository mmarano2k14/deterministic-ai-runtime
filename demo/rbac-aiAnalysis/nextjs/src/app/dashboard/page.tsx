"use client";

import { JSX, useEffect, useRef, useState } from "react";

import { AiAnalysisSidebar } from "@/components/aiAnalysisPanel/AiAnalysisSidebar";
import { BurstPanel } from "@/components/burstPanel/BurstPanel";
import { BurstPanelForm } from "@/components/burstPanel/BurstPanelForm";
import { BurstPanelHelpers } from "@/components/burstPanel/helpers/BurstPanelHelpers";
import { ScenarioPresetsPanel } from "@/components/burstPanel/scenarios/ScenarioPresetsPanel";
import { BurstActions } from "@/components/burstPanel/sections/BurstActions";
import { ContextPanel } from "@/components/contextPanel/ContextPanel";
import { LogsPanel } from "@/components/logsPanel/LogsPanel";
import { RequestPanel } from "@/components/requestPanel/RequestPanel";
import { TargetPanel } from "@/components/targetPanel/TargetPanel";
import { BottomDrawer } from "@/components/ui/BottomDrawer";
import { ConsoleSidePanel } from "@/components/ui/ConsoleSidePanel";
import { ControlSidebarTabs } from "@/components/ui/ControlSidebarTabs";
import { ConsoleStatusBar } from "@/components/ui/status/ConsoleStatusBar";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { BurstScenarioDefinition } from "@/lib/console/burst/scenarios/BurstScenarioPresetType";
import { RuntimeAnalysisApprovedScenarioMapper } from "@/lib/aiAnalysis/RuntimeAnalysisApprovedScenarioMapper";
import type { RuntimeAnalysisSuggestedScenario } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { useBurstController } from "@/lib/console/burst/useBurstController";
import type { InFlightMaxValue } from "@/lib/console/ConsoleType";
import { useConsoleContext } from "@/lib/console/contextProvider/useConsoleContext";
import {
  ConsoleControlTabKey,
  ConsoleSidebarsLayout,
} from "@/lib/console/layout/ConsoleSidebarsLayout";

const LOGS_COLLAPSED_HEIGHT = 56;
const WORKSPACE_GAP = 12;
const LIVE_LOG_HEADER_PORTAL_ID = "live-log-header-controls";

export default function Page(): JSX.Element {
  const { state, actions, dispatch, api } = useConsoleContext();
  const burst = useBurstController({ api, dispatch });

  const [isLogsCollapsed, setIsLogsCollapsed] = useState(false);
  const [logsHeight, setLogsHeight] = useState(320);
  const [sidebars, setSidebars] = useState(() =>
    ConsoleSidebarsLayout.createDefault()
  );
  const [isAiWorking, setIsAiWorking] = useState(false);

  const previousBurstStateRef = useRef(burst.model.state);

  const isRunning = BurstPanelHelpers.isRunning(burst.model);
  const manualBurstBlocked =
    isRunning || state.busy || isAiWorking;
  const isAiOpen = !sidebars.aiCollapsed;
  const visibleLogsHeight = isLogsCollapsed
    ? LOGS_COLLAPSED_HEIGHT
    : logsHeight;

  useEffect(() => {
    const previousState = previousBurstStateRef.current;
    const currentState = burst.model.state;

    previousBurstStateRef.current = currentState;

    if (
      (previousState === "Running" || previousState === "Stopping")
      && currentState === "Completed"
    ) {
      // A completed burst produces the evidence used by AI analysis.
      // Open the AI workspace automatically, but do not change any
      // analysis state or launch work on the user's behalf.
      setSidebars((current) =>
        ConsoleSidebarsLayout.setAiCollapsed(current, false)
      );
    }
  }, [burst.model.state]);

  async function handleManualBurstStart(): Promise<void> {
    if (manualBurstBlocked) {
      return;
    }

    await burst.actions.start();
  }

  async function handleLaunchScenario(
    scenario: BurstScenarioDefinition
  ): Promise<void> {
    if (manualBurstBlocked) {
      return;
    }

    if (scenario.maxInFlight) {
      dispatch({
        type: "maxInFlightChanged",
        maxInFlightValue: scenario.maxInFlight as InFlightMaxValue,
      });
    }

    if (scenario.rotationOverlapMs) {
      dispatch({
        type: "rotationOverlapMsChange",
        rotationOverlapMs: scenario.rotationOverlapMs,
      });
    }

    await burst.actions.start(scenario.burstConfig);
  }

  async function handleExecuteAiScenario(
    scenario: RuntimeAnalysisSuggestedScenario,
    planKey: string
  ): Promise<void> {
    const launch =
      RuntimeAnalysisApprovedScenarioMapper.map(
        scenario,
        planKey
      );

    dispatch({
      type: "maxInFlightChanged",
      maxInFlightValue:
        launch.maxInFlight,
    });

    dispatch({
      type: "rotationOverlapMsChange",
      rotationOverlapMs:
        launch.rotationOverlapMs,
    });

    // IMPORTANT: reuse the exact same execution path as the manual presets.
    await burst.actions.start(
      launch.burstConfig
    );
  }

  function handleControlSidebarCollapsedChange(next: boolean): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.setControlsCollapsed(current, next)
    );
  }

  function handleControlSidebarTabClick(tab: ConsoleControlTabKey): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.openControlTab(current, tab)
    );
  }

  function handleAiSidebarCollapsedChange(next: boolean): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.setAiCollapsed(current, next)
    );
  }

  const consoleBodyClassName = [
    "console-body",
    "dashboard-console-body",
    sidebars.controlsCollapsed ? "dashboard-controls-collapsed" : "",
    isAiOpen ? "dashboard-ai-open" : "",
  ]
    .filter(Boolean)
    .join(" ");

  const mainStyle = isAiOpen
    ? {
        height: `calc(100% - ${visibleLogsHeight + WORKSPACE_GAP}px)`,
      }
    : undefined;

  const logsDrawerClassName = [
    "bottom-drawer--logs",
    isAiOpen ? "dashboard-logs-drawer-ai-open" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div className="console-shell">
      <header className="console-header">
        <div className="header-left">
          <TargetPanel
            disabled={state.busy || isAiWorking}
            baseUrl={state.baseUrl}
            onTargetChanges={(v) =>
              dispatch({ type: "TargetChanged", baseUrl: v })
            }
          />

          <ConsoleStatusBar
            status={burst.model.state}
            busy={isRunning || isAiWorking}
            lastError={state.lastError}
            username={state.demoUserId}
            contextKey={api.contextKey}
            onDismissError={actions.resetError}
          />
        </div>

        <div className="header-right">
          <ContextPanel
            disabled={isRunning || isAiWorking}
            demoUserId={state.demoUserId}
            contextKey={api.contextKey}
            maxInFlight={state.maxInFlight}
            rotationOverlapMs={state.rotationOverlapMs}
            onDemoUserIdChange={() => {}}
            onContextKeyChange={() => {}}
            onGetContextClick={actions.getContextKey}
            onMaxInFlightChange={(v) =>
              dispatch({ type: "maxInFlightChanged", maxInFlightValue: v })
            }
            onRotationOverlapMsChange={(v) =>
              dispatch({ type: "rotationOverlapMsChange", rotationOverlapMs: v })
            }
            onClearClick={() =>
              dispatch({ type: "ContextChanged", contextKey: "" })
            }
          />

          <BurstActions
            disabled={state.busy || isAiWorking}
            isRunning={isRunning}
            onStart={handleManualBurstStart}
            onStop={burst.actions.stop}
            onReset={burst.actions.reset}
          />

          <ThemeToggle />
        </div>
      </header>

      <div
        className="console-content"
        style={{
          paddingBottom: isAiOpen ? "0px" : `${visibleLogsHeight}px`,
        }}
      >
        <div className={consoleBodyClassName}>
          <ConsoleSidePanel
            side="left"
            title="Controls"
            isCollapsed={sidebars.controlsCollapsed}
            onCollapsedChange={handleControlSidebarCollapsedChange}
            collapsedContent={
              <ControlSidebarTabs
                activeTab={sidebars.activeControlTab}
                onTabClick={handleControlSidebarTabClick}
              />
            }
          >
            <ScenarioPresetsPanel
              planKey="read"
              disabled={manualBurstBlocked}
              onLaunch={handleLaunchScenario}
            />

            <RequestPanel
              disabled={manualBurstBlocked}
              invoiceId={state.invoiceId}
              amount={state.amount}
              onInvoiceIdChange={(v) =>
                dispatch({ type: "InvoiceChanged", invoiceId: v })
              }
              onAmountChange={(v) =>
                dispatch({ type: "AmountChanged", amount: v })
              }
              onReadClick={handleLaunchScenario}
              onRefundClick={handleLaunchScenario}
              onClearLogClick={actions.clearLogs}
            />

            <BurstPanelForm
              disabled={manualBurstBlocked}
              model={burst.model}
              onConfigure={burst.actions.configure}
              onStart={handleManualBurstStart}
              onStop={burst.actions.stop}
              onReset={burst.actions.reset}
            />
          </ConsoleSidePanel>

          <main className="console-main" style={mainStyle}>
            <BurstPanel
              disabled={manualBurstBlocked}
              model={burst.model}
              onConfigure={burst.actions.configure}
              onStart={handleManualBurstStart}
              onStop={burst.actions.stop}
              onReset={burst.actions.reset}
            />
          </main>

          <AiAnalysisSidebar
            isCollapsed={sidebars.aiCollapsed}
            model={burst.model}
            logs={state.logs}
            maxInFlight={String(state.maxInFlight)}
            rotationOverlapMs={String(state.rotationOverlapMs)}
            api={api}
            onExecuteScenario={handleExecuteAiScenario}
            onBusyChange={setIsAiWorking}
            onCollapsedChange={handleAiSidebarCollapsedChange}
          />
        </div>
      </div>

      <BottomDrawer
        title="Live log"
        isCollapsed={isLogsCollapsed}
        height={logsHeight}
        minHeight={140}
        maxHeight={620}
        collapsedHeight={LOGS_COLLAPSED_HEIGHT}
        className={logsDrawerClassName}
        headerPortalId={LIVE_LOG_HEADER_PORTAL_ID}
        onCollapsedChange={setIsLogsCollapsed}
        onHeightChange={setLogsHeight}
      >
        <LogsPanel
          logs={state.logs}
          headerPortalId={LIVE_LOG_HEADER_PORTAL_ID}
          onClearClick={actions.clearLogs}
        />
      </BottomDrawer>
    </div>
  );
}
