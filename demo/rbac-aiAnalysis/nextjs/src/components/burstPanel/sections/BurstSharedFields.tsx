"use client";

import { JSX } from "react";
import type {
  BurstConfig,
  BurstDispatchModeKey,
  BurstPlanKey,
} from "@/lib/console/burst/runtime/BurstMachineType";
import { BurstPanelHelpers } from "../helpers/BurstPanelHelpers";

export type BurstSharedFieldsProps = {
  disabled: boolean;
  isRunning: boolean;
  config: BurstConfig;
  onConfigure: (cfg: BurstConfig) => void;
};

export function BurstSharedFields(
  props: BurstSharedFieldsProps
): JSX.Element {
  const { disabled, isRunning, config, onConfigure } = props;

  return (
    <div className="form-grid">
      <div>
        <label>Dispatch mode</label>
        <select
          value={config.dispatchMode}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onConfigure(
              BurstPanelHelpers.changeDispatchMode(
                config,
                e.target.value as BurstDispatchModeKey
              )
            )
          }
        >
          {BurstPanelHelpers.dispatchModeOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label>Plan</label>
        <select
          value={config.planKey}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onConfigure(
              BurstPanelHelpers.mergeSharedConfig(config, {
                planKey: e.target.value as BurstPlanKey,
              })
            )
          }
        >
          {BurstPanelHelpers.planOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label>Total requests</label>
        <input
          type="number"
          value={config.total}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onConfigure(
              BurstPanelHelpers.mergeSharedConfig(config, {
                total: Number(e.target.value),
              })
            )
          }
        />
      </div>
    </div>
  );
}