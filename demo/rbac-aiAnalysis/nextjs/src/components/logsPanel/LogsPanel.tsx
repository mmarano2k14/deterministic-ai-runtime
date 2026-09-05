"use client";

import React, {
  JSX,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { createPortal } from "react-dom";
import { useVirtualizer } from "@tanstack/react-virtual";
import { HttpLogCard } from "./components/HttpLogCard";
import { RealtimeLogCard } from "./components/RealtimeLogCard";
import { LogFilterKind } from "./LogsPanelType";
import { LogUiHelper } from "./helpers/LogUiHelper";
import { ContextRotationLogCard } from "./components/ContextRotationLogCard";
import { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

export type LogsPanelProps = {
  logs: ConsoleLogEntry[];
  headerPortalId?: string;
  onClearClick: () => void;
};

export function LogsPanel(props: LogsPanelProps): JSX.Element {
  const {
    logs,
    headerPortalId,
    onClearClick,
  } = props;

  /**
   * Selected filter used to show:
   * - all logs
   * - only HTTP logs
   * - only realtime logs
   * - only context rotation logs
   * - only HTTP errors
   */
  const [filter, setFilter] = useState<LogFilterKind>("all");

  const [searchQuery, setSearchQuery] = useState("");

  const [headerPortalTarget, setHeaderPortalTarget] =
    useState<HTMLElement | null>(null);

  const seenLogClassifications = useRef(
    new Map<string, number>()
  );

  const [cumulativeCounts, setCumulativeCounts] =
    useState<LogCumulativeCounts>(
      createEmptyLogCumulativeCounts()
    );

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

  useEffect(() => {
    if (!headerPortalId) {
      setHeaderPortalTarget(null);
      return;
    }

    setHeaderPortalTarget(
      document.getElementById(headerPortalId)
    );

    return () => {
      setHeaderPortalTarget(null);
    };
  }, [headerPortalId]);

  /**
   * Apply the active filter while preserving the original source order.
   *
   * Important:
   * We do not sort here because the source already provides
   * the correct order (newest first).
   */
  const filteredLogs = useMemo(() => {
    const filterMatchedLogs = (() => {
      if (filter === "all") {
        return logs;
      }

      if (filter === "rotation") {
        return logs.filter((log) => LogUiHelper.isContextRotationLog(log));
      }

      if (filter === "http-error") {
        return logs.filter(
          (log) =>
            LogUiHelper.isHttpLogEntry(log)
            && (
              (typeof log.status === "number" && log.status >= 400)
              || !!log.error
            )
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
    })();

    const normalizedQuery = searchQuery
      .trim()
      .toLowerCase();

    if (!normalizedQuery) {
      return filterMatchedLogs;
    }

    return filterMatchedLogs.filter((log) =>
      buildSearchText(log).includes(normalizedQuery)
    );
  }, [logs, filter, searchQuery]);

  /**
   * Lifetime counters since the last explicit Clear Logs.
   *
   * The retained log array is a bounded recency window (max 4000 in the
   * current sink). Old rows may therefore be evicted while the console keeps
   * running. Filter counters must not decrease when that happens.
   *
   * Classification is tracked per stable log id. This also handles HTTP
   * entries that are initially pushed and later patched with:
   * - final HTTP status / error;
   * - context-rotation metadata.
   *
   * A category is incremented only the first time one log id acquires it.
   */
  useEffect(() => {
    const deltas =
      createEmptyLogCumulativeCounts();

    let changed = false;

    for (const log of logs) {
      const currentMask =
        classifyLogForCumulativeCounts(log);

      const previousMask =
        seenLogClassifications.current.get(log.id)
        ?? 0;

      const newlyAcquiredMask =
        currentMask & ~previousMask;

      if (newlyAcquiredMask === 0) {
        continue;
      }

      seenLogClassifications.current.set(
        log.id,
        previousMask | currentMask
      );

      applyLogCounterMask(
        deltas,
        newlyAcquiredMask
      );

      changed = true;
    }

    if (!changed) {
      return;
    }

    setCumulativeCounts((current) =>
      addLogCumulativeCounts(
        current,
        deltas
      )
    );
  }, [logs]);

  /**
   * Current retained counts.
   *
   * These are used only for tooltips / retained-window information.
   * The visible button counters use cumulativeCounts.
   */
  const retainedHttpCount =
    logs.filter((x) => LogUiHelper.isHttpLogEntry(x)).length;

  const retainedHttpErrorCount = logs.filter(
    (x) =>
      LogUiHelper.isHttpLogEntry(x) &&
      ((typeof x.status === "number" && x.status >= 400) || !!x.error)
  ).length;

  const retainedRealtimeCount =
    logs.filter((x) => x.kind === "realtime").length;

  const retainedContextRotationCount = logs.filter((x) =>
    LogUiHelper.isContextRotationLog(x)
  ).length;

  const retainedContextKeyCount = logs.filter((x) =>
    LogUiHelper.isContextKeyLog(x)
  ).length;

  const retainedRuntimeEngineCount = logs.filter((x) =>
    LogUiHelper.isRuntimeEngineLog(x)
  ).length;

  const retainedAiCount = logs.filter((x) =>
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
  }, [filter, searchQuery, rowVirtualizer]);

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
   * Clear is the explicit lifetime boundary for both:
   * - retained rows;
   * - cumulative counters.
   */
  const handleClearLogs = (): void => {
    seenLogClassifications.current.clear();
    setCumulativeCounts(
      createEmptyLogCumulativeCounts()
    );
    setSearchQuery("");
    setStickToTop(true);
    onClearClick();
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

  const toolbar = (
      <div className="logs-filter-toolbar">
        <div
          className="logs-filter-group logs-filter-group--all"
          aria-label="All logs"
        >
          <span className="logs-filter-group__label">ALL</span>

          <button
            className={filter === "all" ? "active" : ""}
            onClick={() => setFilter("all")}
            type="button"
            title={logCounterTitle(
              "All",
              cumulativeCounts.all,
              logs.length
            )}
          >
            All ({cumulativeCounts.all})
          </button>
        </div>

        <div
          className="logs-filter-group logs-filter-group--http"
          aria-label="HTTP log filters"
        >
          <span className="logs-filter-group__label">HTTP</span>

          <button
            className={filter === "http" ? "active" : ""}
            onClick={() => setFilter("http")}
            type="button"
            title={logCounterTitle(
              "HTTP",
              cumulativeCounts.http,
              retainedHttpCount
            )}
          >
            HTTP ({cumulativeCounts.http})
          </button>

          <button
            className={filter === "http-error" ? "active" : ""}
            onClick={() => setFilter("http-error")}
            type="button"
            title={logCounterTitle(
              "HTTP Error",
              cumulativeCounts.httpError,
              retainedHttpErrorCount
            )}
          >
            HTTP Error ({cumulativeCounts.httpError})
          </button>

          <button
            className={filter === "rotation" ? "active" : ""}
            onClick={() => setFilter("rotation")}
            type="button"
            title={logCounterTitle(
              "Context",
              cumulativeCounts.context,
              retainedContextRotationCount
            )}
          >
            Context ({cumulativeCounts.context})
          </button>
        </div>

        <div
          className="logs-filter-group logs-filter-group--realtime"
          aria-label="Realtime log filters"
        >
          <span className="logs-filter-group__label">REALTIME</span>

          <button
            className={filter === "realtime" ? "active" : ""}
            onClick={() => setFilter("realtime")}
            type="button"
            title={logCounterTitle(
              "Realtime",
              cumulativeCounts.realtime,
              retainedRealtimeCount
            )}
          >
            Realtime ({cumulativeCounts.realtime})
          </button>

          <button
            className={filter === "context-key" ? "active" : ""}
            onClick={() => setFilter("context-key")}
            type="button"
            title={logCounterTitle(
              "ContextKey",
              cumulativeCounts.contextKey,
              retainedContextKeyCount
            )}
          >
            ContextKey ({cumulativeCounts.contextKey})
          </button>

          <button
            className={filter === "runtime-engine" ? "active" : ""}
            onClick={() => setFilter("runtime-engine")}
            type="button"
            title={logCounterTitle(
              "Runtime Engine",
              cumulativeCounts.runtimeEngine,
              retainedRuntimeEngineCount
            )}
          >
            Runtime Engine ({cumulativeCounts.runtimeEngine})
          </button>

          <button
            className={filter === "ai" ? "active" : ""}
            onClick={() => setFilter("ai")}
            type="button"
            title={logCounterTitle(
              "AI",
              cumulativeCounts.ai,
              retainedAiCount
            )}
          >
            AI ({cumulativeCounts.ai})
          </button>
        </div>

        <div
          className="logs-filter-search"
          aria-label="Search current log filter"
        >
          <span className="logs-filter-group__label">SEARCH</span>

          <input
            type="search"
            value={searchQuery}
            onChange={(event) => setSearchQuery(event.target.value)}
            placeholder="Search logs…"
            aria-label="Search logs"
          />

          {searchQuery.trim() ? (
            <span className="logs-filter-search__count">
              {filteredLogs.length} match{filteredLogs.length === 1 ? "" : "es"}
            </span>
          ) : null}
        </div>

        <div
          className="logs-filter-actions"
          aria-label="Log actions"
        >
          <span className="logs-filter-group__label">ACTIONS</span>

          <span
            className={`logs-live-state ${
              stickToTop ? "is-live" : "is-paused"
            }`}
            title={
              stickToTop
                ? "Following newest logs"
                : "Live follow paused because you scrolled away from the newest logs"
            }
          >
            <span aria-hidden="true">●</span>
            {stickToTop ? "Follow live" : "Paused"}
          </span>

          <span
            className="logs-retained-window"
            title="Rows currently retained in the bounded in-memory log buffer. Filter counters show totals observed since the last Clear Logs."
          >
            {logs.length} retained
          </span>

          <button
            type="button"
            className="logs-jump-button active"
            onClick={handleClearLogs}
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
      </div>
  );

  return (
    <section className="logs-panel">
      {headerPortalTarget
        ? createPortal(
            toolbar,
            headerPortalTarget
          )
        : toolbar}

      <div
        ref={scrollRef}
        onScroll={handleScroll}
        className="log-list"
      >
        {filteredLogs.length === 0 ? (
          <div className="log-empty">No logs for current filter.</div>
        ) : (
          <div
            className="log-virtual-space"
            style={{
              height: `${rowVirtualizer.getTotalSize()}px`,
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
                  className="log-row log-row--virtual"
                  style={{
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

type LogCumulativeCounts = {
  all: number;
  http: number;
  httpError: number;
  context: number;
  realtime: number;
  contextKey: number;
  runtimeEngine: number;
  ai: number;
};

const LogCounterMask = {
  All: 1 << 0,
  Http: 1 << 1,
  HttpError: 1 << 2,
  Context: 1 << 3,
  Realtime: 1 << 4,
  ContextKey: 1 << 5,
  RuntimeEngine: 1 << 6,
  Ai: 1 << 7,
} as const;

function createEmptyLogCumulativeCounts(): LogCumulativeCounts {
  return {
    all: 0,
    http: 0,
    httpError: 0,
    context: 0,
    realtime: 0,
    contextKey: 0,
    runtimeEngine: 0,
    ai: 0,
  };
}

function classifyLogForCumulativeCounts(
  log: ConsoleLogEntry
): number {
  let mask = LogCounterMask.All;

  if (LogUiHelper.isHttpLogEntry(log)) {
    mask |= LogCounterMask.Http;

    if (
      (typeof log.status === "number" && log.status >= 400)
      || !!log.error
    ) {
      mask |= LogCounterMask.HttpError;
    }

    if (LogUiHelper.isContextRotationLog(log)) {
      mask |= LogCounterMask.Context;
    }

    return mask;
  }

  if (LogUiHelper.isRealtimeLogEntry(log)) {
    mask |= LogCounterMask.Realtime;

    if (LogUiHelper.isContextKeyLog(log)) {
      mask |= LogCounterMask.ContextKey;
    }

    if (LogUiHelper.isRuntimeEngineLog(log)) {
      mask |= LogCounterMask.RuntimeEngine;
    }

    if (LogUiHelper.isAiLog(log)) {
      mask |= LogCounterMask.Ai;
    }
  }

  return mask;
}

function applyLogCounterMask(
  target: LogCumulativeCounts,
  mask: number
): void {
  if (mask & LogCounterMask.All) target.all++;
  if (mask & LogCounterMask.Http) target.http++;
  if (mask & LogCounterMask.HttpError) target.httpError++;
  if (mask & LogCounterMask.Context) target.context++;
  if (mask & LogCounterMask.Realtime) target.realtime++;
  if (mask & LogCounterMask.ContextKey) target.contextKey++;
  if (mask & LogCounterMask.RuntimeEngine) target.runtimeEngine++;
  if (mask & LogCounterMask.Ai) target.ai++;
}

function addLogCumulativeCounts(
  current: LogCumulativeCounts,
  delta: LogCumulativeCounts
): LogCumulativeCounts {
  return {
    all: current.all + delta.all,
    http: current.http + delta.http,
    httpError: current.httpError + delta.httpError,
    context: current.context + delta.context,
    realtime: current.realtime + delta.realtime,
    contextKey: current.contextKey + delta.contextKey,
    runtimeEngine: current.runtimeEngine + delta.runtimeEngine,
    ai: current.ai + delta.ai,
  };
}

function logCounterTitle(
  label: string,
  observed: number,
  retained: number
): string {
  return `${label}: ${observed} observed since Clear Logs · ${retained} currently retained`;
}

function buildSearchText(log: ConsoleLogEntry): string {
  if (LogUiHelper.isHttpLogEntry(log)) {
    return [
      log.name,
      log.method,
      log.path,
      log.url,
      log.status,
      log.statusText,
      log.error,
      log.rotation?.from,
      log.rotation?.to,
      safeSearchJson(log.requestHeaders),
      safeSearchJson(log.requestBody),
      safeSearchJson(log.responseHeaders),
      log.responseBody,
    ]
      .filter((value) => value !== undefined && value !== null)
      .join(" ")
      .toLowerCase();
  }

  if (LogUiHelper.isRealtimeLogEntry(log)) {
    return [
      log.level,
      log.category,
      log.message,
      log.eventName,
      log.userId,
      safeSearchJson(log.data),
      safeSearchJson(log.payload),
    ]
      .filter((value) => value !== undefined && value !== null)
      .join(" ")
      .toLowerCase();
  }

  return "";
}

function safeSearchJson(value: unknown): string {
  if (value === undefined || value === null) {
    return "";
  }

  if (typeof value === "string") {
    return value;
  }

  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

