import React from "react";

export class UiHelpers {

  public static Stat({
    label,
    value,
    valueClassName,
  }: {
    label: string;
    value: React.ReactNode;
    valueClassName?: string;
  }) {
    return (
      <div className="kpi-card">
        <span className="kpi-label">{label}</span>
        <strong className={valueClassName}>{value}</strong>
      </div>
    );
  }

  public static ProgressBar({ value }: { value: number }) {
    const pct = Math.round(value * 100);
    const isActive = pct > 0 && pct < 100;

    return (
      <div
        className={`burst-progress-native-wrap ${
          isActive ? "is-active" : ""
        }`}
      >
        <progress
          className="burst-progress-native"
          max={100}
          value={pct}
          aria-label={`Burst progress ${pct}%`}
        />
      </div>
    );
  }

  public static ratio(done: number, total: number): number {
    if (total <= 0) return 0;
    const r = done / total;
    return Math.max(0, Math.min(1, r));
  }

  public static formatMs(v?: number): string {
    if (!v || v < 0) return "0 ms";
    if (v < 1000) return `${Math.round(v)} ms`;
    return `${(v / 1000).toFixed(2)} s`;
  }

  /**
   * Compact latency-pair display for KPI cards.
   *
   * When both values use milliseconds, render one shared unit:
   *   110 / 221 ms
   *
   * If either value crosses one second, keep explicit units because the
   * values may use different scales:
   *   850 ms / 1.21 s
   */
  public static formatMsPair(
    first?: number,
    second?: number
  ): string {
    const safeFirst =
      first && first > 0 ? first : 0;
    const safeSecond =
      second && second > 0 ? second : 0;

    if (
      safeFirst < 1000
      && safeSecond < 1000
    ) {
      return `${Math.round(safeFirst)} / ${Math.round(safeSecond)} ms`;
    }

    return `${this.formatMs(safeFirst)} / ${this.formatMs(safeSecond)}`;
  }

}