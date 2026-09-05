"use client";

import React, { JSX } from "react";
import { TargetPreset } from "@/lib/infrastructure/transport/http/HttpClientType";

// ---------------------------------------------------------------------
// Presets
// ---------------------------------------------------------------------
const PRESETS: TargetPreset[] = [
  { label: ".NET (Kestrel)", baseUrl: "http://localhost:5000" },
  { label: "Java (Spring)", baseUrl: "http://localhost:8080" },
  { label: "Node", baseUrl: "http://localhost:3001" },
];

export type TargetPanelProps = {
  disabled: boolean;
  baseUrl: string;
  onTargetChanges: (v: string) => void;
};

export function TargetPanel(props: TargetPanelProps): JSX.Element {
  const { baseUrl, onTargetChanges } = props;

  return (
    <React.Fragment>
      <div className="header-block">
        <label>Target</label>
        <select
          value={baseUrl}
          onChange={(e) => onTargetChanges(e.target.value)}
          disabled={true}
        >
          {PRESETS.map((p) => (
            <option key={p.baseUrl} value={p.baseUrl}>
              {p.label} — {p.baseUrl}
            </option>
          ))}
        </select>
      </div>
    </React.Fragment>
  );
}