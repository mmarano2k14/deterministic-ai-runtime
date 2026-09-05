import { JSX } from "react";
import type {
  BurstConfig,
  BurstDispatchModeKey,
  BurstRuntime,
  BurstPlanKey,
} from "@/lib/console/burst/runtime/BurstMachineType";
import { MaintainedConcurrencyFields } from "../modes/MaintainedConcurrencyFields";
import { SingleBurstFields } from "../modes/SingleBurstFields";
import { WaveBatchesFields } from "../modes/WaveBatchesFields";
import { WaveBatchesStaggeredFields } from "../modes/WaveBatchesStaggeredFields";



/**
 * Centralizes constants and UI helper functions used by BurstPanel.
 * The goal is to keep the React component focused on rendering only.
 */
export class BurstPanelHelpers {
  /**
   * Safe UI fallback.
   * Keeps the old default behavior: maintained concurrency.
   */
  public static getFallbackConfig(): BurstConfig {
    return {
      dispatchMode: "maintained-concurrency",
      planKey: "read",
      total: 500,
      concurrency: 50,
      delayMs: 10,
    };
  }

  /**
   * Returns the current config from the model, or a safe fallback.
   */
  public static getConfig(model: BurstRuntime): BurstConfig {
    return model.report?.config ?? BurstPanelHelpers.getFallbackConfig();
  }

  /**
   * True when the burst is actively running or draining.
   */
  public static isRunning(model: BurstRuntime): boolean {
    return model.state === "Running" || model.state === "Stopping";
  }

  /**
   * Extracts the elapsed time from the report.
   */
  public static getElapsedMs(model: BurstRuntime): number | undefined {
    const report = model.report;
    if (!report?.timing.startedAt) return undefined;

    const end = report.timing.finishedAt ?? Date.now();
    return Math.max(0, end - report.timing.startedAt);
  }

  /**
   * Updates only shared fields available in all dispatch modes.
   */
  public static mergeSharedConfig(
    current: BurstConfig,
    partial: {
      planKey?: BurstPlanKey;
      total?: number;
    }
  ): BurstConfig {
    return {
      ...current,
      ...partial,
    } as BurstConfig;
  }

  /**
   * Builds a valid config when the dispatch mode changes.
   * This prevents mixed invalid shapes in the discriminated union.
   */
  public static changeDispatchMode(
  current: BurstConfig,
  dispatchMode: BurstDispatchModeKey
): BurstConfig {
    switch (dispatchMode) {
      case "single-burst":
        return {
          dispatchMode: "single-burst",
          planKey: current.planKey,
          total: current.total,
          delayMs: current.delayMs,
        };

      case "maintained-concurrency":
        return {
          dispatchMode: "maintained-concurrency",
          planKey: current.planKey,
          total: current.total,
          delayMs: current.delayMs,
          concurrency:
            current.dispatchMode === "maintained-concurrency"
              ? current.concurrency
              : 50,
        };

      case "wave-batches":
        return {
          dispatchMode: "wave-batches",
          planKey: current.planKey,
          total: current.total,
          delayMs: current.delayMs,
          batchSize: current.dispatchMode === "wave-batches" ? current.batchSize : 5,
          wavePauseMs: current.dispatchMode === "wave-batches" ? current.wavePauseMs : 300,
        };

      case "wave-batches-staggered":
        return {
          dispatchMode: "wave-batches-staggered",
          planKey: current.planKey,
          total: current.total,
          delayMs: current.delayMs,
          batchSize:
            current.dispatchMode === "wave-batches-staggered"
              ? current.batchSize
              : 5,
          wavePauseMs:
            current.dispatchMode === "wave-batches-staggered"
              ? current.wavePauseMs
              : 300,
        };
    }
  }

  /**
   * Renders the specific field component for the current mode.
   */
  public static renderModeFields(args: {
    config: BurstConfig;
    disabled: boolean;
    isRunning: boolean;
    onConfigure: (cfg: BurstConfig) => void;
  }): JSX.Element | null {
    const { config, disabled, isRunning, onConfigure } = args;

    switch (config.dispatchMode) {
      case "single-burst":
        return (
          <SingleBurstFields
            disabled={disabled}
            isRunning={isRunning}
            config={config}
            onChange={onConfigure}
          />
        );

      case "maintained-concurrency":
        return (
          <MaintainedConcurrencyFields
            disabled={disabled}
            isRunning={isRunning}
            config={config}
            onChange={onConfigure}
          />
        );

      case "wave-batches":
        return (
          <WaveBatchesFields
            disabled={disabled}
            isRunning={isRunning}
            config={config}
            onChange={onConfigure}
          />
        );

      case "wave-batches-staggered":
        return (
          <WaveBatchesStaggeredFields
            disabled={disabled}
            isRunning={isRunning}
            config={config}
            onChange={onConfigure}
          />
        );

      default:
        return null;
    }
  }

  /**
   * Human-readable labels for dispatch modes.
   */
  public static readonly dispatchModeOptions: Array<{
    value: BurstDispatchModeKey;
    label: string;
  }> = [
    { value: "single-burst", label: "Single burst" },
    { value: "maintained-concurrency", label: "Maintained concurrency" },
    { value: "wave-batches", label: "Wave batches" },
    { value: "wave-batches-staggered", label: "Wave batches (staggered)" },
  ];

  /**
   * Human-readable labels for burst plans.
   */
  public static readonly planOptions: Array<{
    value: BurstPlanKey;
    label: string;
  }> = [
    { value: "read", label: "Invoice.Read (GET)" },
    { value: "refund", label: "Invoice.Refund (POST)" },
  ];
}