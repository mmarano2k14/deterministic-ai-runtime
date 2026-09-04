"use client";

import React, {
  JSX,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { HttpLogCard } from "./components/HttpLogCard";
import { RealtimeLogCard } from "./components/RealtimeLogCard";
import { LogFilterKind } from "./LogsPanelType";
import { LogUiHelper } from "./helpers/LogUiHelper";
import { ContextRotationLogCard } from "./components/ContextRotationLogCard";
import { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

export type LogsPanelProps = {
  logs: ConsoleLogEntry[];
  onClearClick: () => void;
};

export function LogsPanel(props: LogsPanelProps): JSX.Element {
  const { logs, onClearClick } = props;

  /**
   * Selected filter used to show:
   * - all logs
   * - only HTTP logs
   * - only realtime logs
   * - only context rotation logs
   * - only HTTP errors
   */
  const [filter, setFilter] = useState<LogFilterKind>("all");

  /**
   * Controls whether the panel should keep following the live head.
   *
   * Since the newest logs are rendered at the top,
   * "stick to top" means:
   * - if the user stays near the top, auto-scroll to top on new logs
   * - if the user scrolls down, preserve scroll position
   */
  const [stickToTop, setStickToTop] = useState(true);

  /**
   * Scroll container reference used both by:
   * - manual scroll handling
   * - the virtualizer
   */
  const scrollRef = useRef<HTMLDivElement | null>(null);

  /**
   * Apply the active filter while preserving the original source order.
   *
   * Important:
   * We do not sort here because the source already provides
   * the correct order (newest first).
   */
  const filteredLogs = useMemo(() => {
    if (filter === "all") {
      return logs;
    }

    if (filter === "rotation") {
      return logs.filter((log) => LogUiHelper.isContextRotationLog(log));
    }

    if (filter === "http-error") {
      return logs.filter(
        (log) =>
          LogUiHelper.isHttpLogEntry(log) &&
          ((typeof log.status === "number" && log.status >= 400) || !!log.error)
      );
    }

    if (filter === "http") {
      return logs.filter((log) => LogUiHelper.isHttpLogEntry(log));
    }

    if (filter === "context-key") {
      return logs.filter((log) => LogUiHelper.isContextKeyLog(log));
    }

    if (filter === "runtime-engine") {
      return logs.filter((log) => LogUiHelper.isRuntimeEngineLog(log));
    }

    if (filter === "ai") {
      return logs.filter((log) => LogUiHelper.isAiLog(log));
    }

    return logs.filter((log) => log.kind === filter);
  }, [logs, filter]);

  /**
   * Counts displayed in filter buttons.
   */
  const httpCount = logs.filter((x) => LogUiHelper.isHttpLogEntry(x)).length;

  const httpErrorCount = logs.filter(
    (x) =>
      LogUiHelper.isHttpLogEntry(x) &&
      ((typeof x.status === "number" && x.status >= 400) || !!x.error)
  ).length;

  const realtimeCount = logs.filter((x) => x.kind === "realtime").length;

  const contextRotationCount = logs.filter((x) =>
    LogUiHelper.isContextRotationLog(x)
  ).length;

  const contextKeyCount = logs.filter((x) =>
    LogUiHelper.isContextKeyLog(x)
  ).length;

  const runtimeEngineCount = logs.filter((x) =>
    LogUiHelper.isRuntimeEngineLog(x)
  ).length;

  const aiCount = logs.filter((x) =>
    LogUiHelper.isAiLog(x)
  ).length;

  /**
   * Returns a realistic initial row estimate by entry type.
   *
   * The actual height is still measured dynamically by measureElement.
   * These estimates only reduce layout error before the first measurement,
   * especially for realtime entries which are naturally taller than a
   * collapsed HTTP row.
   */
  const estimateRowSize = (index: number): number => {
    const log = filteredLogs[index];

    if (!log) {
      return 72;
    }

    if (filter === "rotation" && LogUiHelper.isContextRotationLog(log)) {
      return 96;
    }

    if (LogUiHelper.isRealtimeLogEntry(log)) {
      return 112;
    }

    return 64;
  };

  /**
   * ------------------------------------------------------------
   * VIRTUALIZER
   * ------------------------------------------------------------
   */
  const rowVirtualizer = useVirtualizer({
    count: filteredLogs.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: estimateRowSize,
    overscan: 6,
    getItemKey: (index) => filteredLogs[index]?.id ?? index,
  });

  /**
   * Auto-follow the live head when the user is still near the top.
   *
   * Because newest logs are inserted at the top,
   * the live position is scrollTop = 0.
   */
  useEffect(() => {
    const container = scrollRef.current;

    if (!container) {
      return;
    }

    if (!stickToTop) {
      return;
    }

    container.scrollTop = 0;
  }, [filteredLogs, stickToTop]);

  /**
   * On filter switch, jump to the top, re-enable live follow mode,
   * and rebuild measurements for the newly selected row population.
   *
   * Do not call measure() for every incoming realtime log:
   * measureElement / ResizeObserver owns dynamic row measurement.
   */
  useEffect(() => {
    const container = scrollRef.current;

    if (!container) {
      return;
    }

    container.scrollTop = 0;
    setStickToTop(true);
    rowVirtualizer.measure();
  }, [filter, rowVirtualizer]);

  /**
   * Updates follow mode depending on the user's current scroll position.
   */
  const handleScroll = (): void => {
    const container = scrollRef.current;

    if (!container) {
      return;
    }

    setStickToTop(LogUiHelper.isNearTop(container));
  };

  /**
   * Manually jump back to the newest logs and re-enable live follow mode.
   */
  const jumpToLatest = (): void => {
    const container = scrollRef.current;

    if (!container) {
      return;
    }

    container.scrollTop = 0;
    setStickToTop(true);
  };

  /**
   * Render exactly one card for one virtual row.
   *
   * Context-rotation entries are ordinary HTTP entries with rotation metadata.
   * In the "all", "http", and "http-error" views they stay represented by the
   * normal HTTP card (which already exposes the rotation badge/details).
   *
   * The dedicated ContextRotationLogCard is used only by the rotation filter.
   * This prevents one log from producing multiple cards inside one measured row.
   */
  const renderLogCard = (log: ConsoleLogEntry): JSX.Element | null => {
    if (filter === "rotation" && LogUiHelper.isContextRotationLog(log)) {
      return <ContextRotationLogCard log={log} />;
    }

    if (LogUiHelper.isHttpLogEntry(log)) {
      return <HttpLogCard log={log} />;
    }

    if (LogUiHelper.isRealtimeLogEntry(log)) {
      return <RealtimeLogCard log={log} />;
    }

    return null;
  };

  return (
    <section className="panel">
      <div className="tab-header">
        <button
          className={filter === "all" ? "active" : ""}
          onClick={() => setFilter("all")}
          type="button"
        >
          All ({logs.length})
        </button>

        <button
          className={filter === "http" ? "active" : ""}
          onClick={() => setFilter("http")}
          type="button"
        >
          HTTP ({httpCount})
        </button>

        <button
          className={filter === "http-error" ? "active" : ""}
          onClick={() => setFilter("http-error")}
          type="button"
        >
          HTTP Error ({httpErrorCount})
        </button>

        <button
          className={filter === "rotation" ? "active" : ""}
          onClick={() => setFilter("rotation")}
          type="button"
        >
          Context ({contextRotationCount})
        </button>

        <button
          className={filter === "realtime" ? "active" : ""}
          onClick={() => setFilter("realtime")}
          type="button"
        >
          Realtime ({realtimeCount})
        </button>

        <button
          className={filter === "context-key" ? "active" : ""}
          onClick={() => setFilter("context-key")}
          type="button"
        >
          ContextKey ({contextKeyCount})
        </button>

        <button
          className={filter === "runtime-engine" ? "active" : ""}
          onClick={() => setFilter("runtime-engine")}
          type="button"
        >
          Runtime Engine ({runtimeEngineCount})
        </button>

        <button
          className={filter === "ai" ? "active" : ""}
          onClick={() => setFilter("ai")}
          type="button"
        >
          AI ({aiCount})
        </button>

        <button
          type="button"
          className="logs-jump-button active"
          onClick={onClearClick}
        >
          Clear Logs
        </button>

        {!stickToTop && filteredLogs.length > 0 && (
          <button
            type="button"
            className="logs-jump-button active"
            onClick={jumpToLatest}
          >
            Jump to latest
          </button>
        )}
      </div>

      <div
        ref={scrollRef}
        onScroll={handleScroll}
        className="log-list"
      >
        {filteredLogs.length === 0 ? (
          <div className="log-empty">No logs for current filter.</div>
        ) : (
          <div
            style={{
              height: `${rowVirtualizer.getTotalSize()}px`,
              position: "relative",
            }}
          >
            {rowVirtualizer.getVirtualItems().map((virtualRow) => {
              const log = filteredLogs[virtualRow.index];

              if (!log) {
                return null;
              }

              return (
                <div
                  key={log.id}
                  ref={rowVirtualizer.measureElement}
                  data-index={virtualRow.index}
                  className="log-row"
                  style={{
                    position: "absolute",
                    top: 0,
                    left: 0,
                    width: "100%",
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  {renderLogCard(log)}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </section>
  );
}
