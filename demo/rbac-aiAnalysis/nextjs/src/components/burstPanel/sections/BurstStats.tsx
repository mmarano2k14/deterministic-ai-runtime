"use client";

import { JSX } from "react";
import { UiHelpers } from "../helpers/uiHelpers";
import type { BurstCounters, BurstRuntime, BurstState, BurstStats as BurstStatsType } from "@/lib/console/burst/runtime/BurstMachineType";
import { BurstPanelHelpers } from "../helpers/BurstPanelHelpers";

export type BurstStatsProps = {
  counters?: BurstCounters;
  stats?: BurstStatsType;
  state?: BurstState;
  model: BurstRuntime;
};

/**
 * Summary stat cards for the current burst run.
 */
export function BurstStats(props: BurstStatsProps): JSX.Element {
  const { counters, stats, model } = props;
  const elapsedMs = BurstPanelHelpers.getElapsedMs(model);

  return (
    <section className="kpi-bar">
      <UiHelpers.Stat label="OK" value={counters?.ok ?? 0} />
      <UiHelpers.Stat label="401" value={counters?.unauthorized ?? 0} />
      <UiHelpers.Stat label="403" value={counters?.forbidden ?? 0} />
      <UiHelpers.Stat label="429" value={counters?.rejected ?? 0} />
      <UiHelpers.Stat label="Other" value={counters?.other ?? 0} />
      <UiHelpers.Stat label="Errors" value={counters?.errors ?? 0} />
      <UiHelpers.Stat
        label="p50 / p95"
        value={UiHelpers.formatMsPair(
          stats?.p50ms,
          stats?.p95ms
        )}
        valueClassName="kpi-value--latency-pair"
      />
      <UiHelpers.Stat label="Elapsed" value={UiHelpers.formatMs(elapsedMs)} />
    </section>
  );
}