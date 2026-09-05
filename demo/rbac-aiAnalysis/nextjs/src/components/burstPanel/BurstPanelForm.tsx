"use client";

import { JSX } from "react";

import type {
  BurstConfig,
  BurstRuntime,
} from "@/lib/console/burst/runtime/BurstMachineType";


import { BurstPanelHelpers } from "./helpers/BurstPanelHelpers";
import { BurstSharedFields } from "./sections/BurstSharedFields";
import { BurstActions } from "./sections/BurstActions";

export type BurstPanelProps = {
  disabled: boolean;
  model: BurstRuntime;
  onConfigure: (cfg: BurstConfig) => void;
  onStart: () => void;
  onStop: () => void;
  onReset: () => void;
};

export function BurstPanelForm(props: BurstPanelProps): JSX.Element {
  const { disabled, model, onConfigure, onStart, onStop, onReset } = props;
  const isRunning = BurstPanelHelpers.isRunning(model);
  const config = BurstPanelHelpers.getConfig(model);
  const elapsedMs = BurstPanelHelpers.getElapsedMs(model);



  return (
    <section className="panel">

      {/* Shared configuration fields */}
      <BurstSharedFields
        disabled={disabled}
        isRunning={isRunning}
        config={config}
        onConfigure={onConfigure}
      />

      {/* Mode specific configuration */}
      {BurstPanelHelpers.renderModeFields({
        config,
        disabled,
        isRunning,
        onConfigure,
      })}

      {/* Actions */}
      <BurstActions
        disabled={disabled}
        isRunning={isRunning}
        elapsedMs={elapsedMs}
        onStart={onStart}
        onStop={onStop}
        onReset={onReset}
      />

    </section>
  );
}