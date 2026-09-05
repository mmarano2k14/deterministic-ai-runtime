"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import type { BurstRuntime } from "../runtime/BurstMachineType";
import type { BurstMetricPoint } from "./BurstMetricsType";

type InternalBurstMetricPoint = BurstMetricPoint & {
  runStartedAt?: number;
};

export function useBurstMetricsSampler(model: BurstRuntime) {
  const [metrics, setMetrics] = useState<InternalBurstMetricPoint[]>([]);

  const prevCompletedRef = useRef<number>(0);
  const prevTimeRef = useRef<number>(0);
  const reportRef = useRef(model.report);

  const runStartedAt = model.report?.timing.startedAt;

  // keep latest report available without retriggering interval setup
  useEffect(() => {
    reportRef.current = model.report;
  }, [model.report]);

  useEffect(() => {
    prevCompletedRef.current = 0;
    prevTimeRef.current = 0;

    if (model.state !== "Running" && model.state !== "Stopping") {
      return;
    }

    const interval = setInterval(() => {
      const r = reportRef.current;
      if (!r) return;

      const now = Date.now();
      const completed = r.progress.completed;

      // only record if something changed
      if (completed === prevCompletedRef.current && prevTimeRef.current !== 0) {
        return;
      }

      let rps: number | undefined = undefined;

      if (prevTimeRef.current > 0) {
        const dtSec = (now - prevTimeRef.current) / 1000;
        const deltaCompleted = completed - prevCompletedRef.current;

        if (dtSec > 0) {
          rps = deltaCompleted / dtSec;
        }
      }

      prevCompletedRef.current = completed;
      prevTimeRef.current = now;

      setMetrics((prev) =>
        [
          ...prev,
          {
            t: now,
            elapsedMs: runStartedAt ? now - runStartedAt : 0,
            completed,
            ok: r.counters.ok,
            errors:
              r.counters.errors +
              r.counters.forbidden +
              r.counters.unauthorized +
              r.counters.other,
            p50: r.stats.p50ms,
            p95: r.stats.p95ms,
            rps,
            runStartedAt,
          },
        ].slice(-400)
      );
    }, 100);

    return () => clearInterval(interval);
  }, [model.state, runStartedAt]);

  const visibleMetrics = useMemo(() => {
    if (!runStartedAt) return [];
    return metrics.filter((x) => x.runStartedAt === runStartedAt);
  }, [metrics, runStartedAt]);

  return visibleMetrics;
}