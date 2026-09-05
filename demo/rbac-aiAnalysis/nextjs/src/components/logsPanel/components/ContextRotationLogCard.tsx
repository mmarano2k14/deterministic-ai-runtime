"use client";

import { JSX, useState } from "react";

import { LogBadge } from "./LogBadge";
import { HttpLogHelper } from "../helpers/HttpLogHelper";
import { HttpLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

export type ContextRotationLogCardProps = {
  log: HttpLogEntry & {
    rotation: {
      from: string;
      to: string;
    };
  };
};

function shortKey(value?: string): string {
  if (!value) return "-";
  if (value.length <= 20) return value;
  return `${value.slice(0, 12)}...${value.slice(-6)}`;
}

export function ContextRotationLogCard(
  props: ContextRotationLogCardProps
): JSX.Element {
  const { log: l } = props;
  const [open, setOpen] = useState(false);

  const badges = HttpLogHelper.getBadges(l);
  const statusTone = HttpLogHelper.getStatusTone(l.status);

  return (
    <div className="log-card">
      <div
        className="log-card__header log-card__header--rotation"
        data-tone={statusTone}
        onClick={() => setOpen((current) => !current)}
      >
        <div className="log-card__rotation-main">
          <div className="log-card__identity">
            <span className="log-card__toggle">
              {open ? "▼" : "▶"}
            </span>

            {badges.map((badge) => (
              <LogBadge key={badge.label} badge={badge} />
            ))}

            {l.method ? <b>{l.method}</b> : null}

            {l.path ? (
              <code className="log-card__path">{l.path}</code>
            ) : null}
          </div>

          <div className="log-card__rotation-summary">
            <span className="log-card__muted">Rotation</span>
            <code>{shortKey(l.rotation.from)}</code>
            <span className="log-card__muted">→</span>
            <code>{shortKey(l.rotation.to)}</code>
          </div>
        </div>

        <div className="log-card__meta">
          {typeof l.status !== "undefined" ? (
            <span
              className="log-card__status"
              data-tone={statusTone}
            >
              {l.status} {l.statusText ?? ""}
            </span>
          ) : null}

          <span className="log-card__timestamp">{l.t}</span>
        </div>
      </div>

      {open ? (
        <div className="log-card__details log-card__details--grid">
          <div className="log-card__summary-card">
            <div className="log-card__summary-title">
              Rotation Summary
            </div>

            <div>
              <b>From:</b> <code>{l.rotation.from}</code>
            </div>

            <div>
              <b>To:</b> <code>{l.rotation.to}</code>
            </div>

            <div>
              <b>Request:</b>{" "}
              <code>
                {l.method} {l.path}
              </code>
            </div>

            {typeof l.status !== "undefined" ? (
              <div>
                <b>Status:</b>{" "}
                <code>
                  {l.status} {l.statusText ?? ""}
                </code>{" "}
                <span className="log-card__response-ok">
                  {l.ok ? "✅ ok" : "❌ not ok"}
                </span>
              </div>
            ) : null}
          </div>

          <div>
            <b>Target:</b> <code>{l.baseUrl}</code>
          </div>

          {l.url ? (
            <div>
              <b>Resolved URL:</b> <code>{l.url}</code>
            </div>
          ) : null}

          <div>
            <b>Request headers:</b>{" "}
            <code>
              {Object.entries(l.requestHeaders ?? {})
                .map(([key, value]) => `${key}=${value}`)
                .join(" | ") || "(none)"}
            </code>
          </div>

          {typeof l.requestBody !== "undefined" ? (
            <div>
              <b>Request body:</b>
              <pre className="log-card__pre">
                {JSON.stringify(l.requestBody, null, 2)}
              </pre>
            </div>
          ) : null}

          {l.responseHeaders && Object.keys(l.responseHeaders).length > 0 ? (
            <div>
              <b>Response headers:</b>{" "}
              <code>
                {Object.entries(l.responseHeaders)
                  .map(([key, value]) => `${key}=${value}`)
                  .join(" | ")}
              </code>
            </div>
          ) : null}

          {l.error ? (
            <div>
              <b className="log-card__error-label">Error:</b>{" "}
              <code>{l.error}</code>
            </div>
          ) : null}

          {typeof l.responseBody === "string" && l.responseBody.length > 0 ? (
            <details>
              <summary className="log-card__summary">Response body</summary>
              <pre className="log-card__pre log-card__pre--response">
                {l.responseBody}
              </pre>
            </details>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
