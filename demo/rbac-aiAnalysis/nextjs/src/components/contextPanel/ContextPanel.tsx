"use client";

import { InFlightMaxValue, maxInFlightOptions, rotationOverlapPresets } from "@/lib/console/ConsoleType";
import React, { JSX } from "react";

export type ContextPanelProps = {
  disabled: boolean;

  demoUserId: string;
  contextKey: string;
  maxInFlight: InFlightMaxValue;
  rotationOverlapMs: string;

  onDemoUserIdChange: (v: string) => void;
  onContextKeyChange: (v: string) => void;
  onMaxInFlightChange: (v: InFlightMaxValue) => void;
  onRotationOverlapMsChange: (v: string) => void;
  onGetContextClick: () => void;
  onClearClick: () => void;
};


export function ContextPanel(props: ContextPanelProps): JSX.Element {
  const {
    disabled,
    maxInFlight,
    rotationOverlapMs,
    onMaxInFlightChange,
    onRotationOverlapMsChange,
  } = props;

  const currentOverlapIndex = Math.max(
    0,
    rotationOverlapPresets.indexOf(rotationOverlapMs)
  );

 return (
  <>
    

    <div className="header-inline">
      <label>Max In-Flight</label>
      <select
        value={maxInFlight}
        onChange={(e) =>
          onMaxInFlightChange(e.target.value as InFlightMaxValue)
        }
        disabled={disabled}
      >
        {maxInFlightOptions.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </div>

    <div className="header-inline">
      <label>Rotation overlap <span>({rotationOverlapMs} ms)</span></label>

      <input
        type="range"
        min={0}
        max={rotationOverlapPresets.length - 1}
        step={1}
        value={currentOverlapIndex}
        onChange={(e) => {
          const index = Number(e.target.value);
          onRotationOverlapMsChange(rotationOverlapPresets[index]);
        }}
        disabled={disabled}
      />

      
    </div>
  </>
);
}