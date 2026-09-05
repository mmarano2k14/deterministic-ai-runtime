"use client";

import { JSX, useMemo, useState } from "react";

import type {
  BurstConfig,
  BurstRuntime,
} from "@/lib/console/burst/runtime/BurstMachineType";

import { BurstHistogram } from "@/lib/console/burst/metric/BurstHistogram";
import { useBurstMetricsSampler } from "@/lib/console/burst/metric/useBurstMetricsSampler";
import { BurstPanelHelpers } from "./helpers/BurstPanelHelpers";
import { BurstProgress } from "./sections/BurstProgress";
import { BurstStats } from "./sections/BurstStats";
import React from "react";
import { BurstGraph } from "./charts/BurstGraph";
import { BurstHistogramChart } from "./charts/BurstHistogramChart";
import { ContextRotationTimeline } from "../logsPanel/components/ContextRotationTimeline";
import { useConsoleContext } from "@/lib/console/contextProvider/useConsoleContext";

export type BurstPanelProps = {
  disabled: boolean;
  model: BurstRuntime;
  onConfigure: (cfg: BurstConfig) => void;
  onStart: () => void;
  onStop: () => void;
  onReset: () => void;
};

export function BurstPanel(props: BurstPanelProps): JSX.Element {
  const { model } = props;
  const controller = useConsoleContext();
  
  const [showTimelineRequests, setShowTimelineRequests] = useState(false)

  const report = model.report;
  const state = model.state;

  const config = BurstPanelHelpers.getConfig(model);

  const progress = report?.progress;
  const counters = report?.counters;
  const stats = report?.stats;

  const started = progress?.started ?? 0;
  const completed = progress?.completed ?? 0;
  const inFlight = progress?.inFlight ?? 0;

  const metrics = useBurstMetricsSampler(model);

  const histogram = useMemo(() => {
    return BurstHistogram.build(stats?.durationsMs ?? []);
  }, [stats?.durationsMs]);

  return (
    <React.Fragment>
      {/* Stats */}
      <BurstStats
        counters={counters}
        stats={stats}
        state = {state}
        model={model}
      />

      {/* Progress */}
      <BurstProgress
        started={started}
        completed={completed}
        inFlight={inFlight}
        total={config.total}
        durations={stats?.durationsMs}
        metricPoints={metrics.length}
      />

      <section className="panel main-tabs">
        <div className="tab-content">

          <div className="panel-grid-1-2">
            <div className="chart-card" >
              <label>Latency Histogram</label>
              <BurstGraph data={metrics} />
            </div>

            <div className="chart-card">
              <label>Latency Histogram</label>
              <BurstHistogramChart data={histogram} />
            </div>


            <div className="panel-section chart-card">
              <div className="panel-header-split">
                <label>Rotation Window</label>

                <label className="checkbox-inline">
                  <input
                    type="checkbox"
                    checked={showTimelineRequests}
                    onChange={(e) => setShowTimelineRequests(e.target.checked)}
                  />
                  Show requests on context timeline
                </label>
              </div>

              <ContextRotationTimeline
                logs={controller.state.logs}
                activeContextKey={controller.api.contextKey}
                showRequests={showTimelineRequests}
                maxItems={5}
              />
            </div>
          </div>
        </div>
      </section>

      {/* Error */}
      {report?.error && (
        <div className="burst-error">
          <b>Error:</b> {report.error}
        </div>
      )}
    </React.Fragment>
  );
}