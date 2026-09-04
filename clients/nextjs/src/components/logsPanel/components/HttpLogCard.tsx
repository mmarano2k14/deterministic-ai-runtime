import { JSX, useState } from "react";
import { LogBadge } from "./LogBadge";
import { HttpLogHelper } from "../helpers/HttpLogHelper";
import { HttpLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

export type HttpLogCardProps = {
  log: HttpLogEntry;
};

export function HttpLogCard(props: HttpLogCardProps): JSX.Element {
  const { log: l } = props;
  const [open, setOpen] = useState(false);

  const badges = HttpLogHelper.getBadges(l);
  const statusTone = HttpLogHelper.getStatusTone(l.status);

  return (
    <div className="log-card">
      <div
        className="log-card__header"
        data-tone={statusTone}
        onClick={() => setOpen((current) => !current)}
      >
        <div className="log-card__identity">
          <span className="log-card__toggle">
            {open ? "▼" : "▶"}
          </span>

          {badges.map((badge) => (
            <LogBadge key={badge.label} badge={badge} />
          ))}

          <b>{l.method}</b>
          <code className="log-card__path">{l.path}</code>
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
        <div className="log-card__details">
          <div>
            <b>Target:</b> <code>{l.baseUrl}</code>
          </div>

          {l.url ? (
            <div>
              <b>Resolved URL:</b> <code>{l.url}</code>
            </div>
          ) : null}

          <div className="log-card__detail-spaced">
            <b>Request headers:</b>{" "}
            <code>
              {Object.entries(l.requestHeaders ?? {})
                .map(([key, value]) => `${key}=${value}`)
                .join(" | ") || "(none)"}
            </code>
          </div>

          {typeof l.requestBody !== "undefined" ? (
            <div className="log-card__detail-spaced">
              <b>Request body:</b>
              <pre className="log-card__pre">
                {JSON.stringify(l.requestBody, null, 2)}
              </pre>
            </div>
          ) : null}

          {l.error ? (
            <div className="log-card__detail-section">
              <b className="log-card__error-label">Error:</b>{" "}
              <code>{l.error}</code>
            </div>
          ) : null}

          {typeof l.status !== "undefined" ? (
            <div className="log-card__detail-section">
              <b>Response:</b>{" "}
              <code>
                {l.status} {l.statusText ?? ""}
              </code>{" "}
              <span className="log-card__response-ok">
                {l.ok ? "✅ ok" : "❌ not ok"}
              </span>
            </div>
          ) : null}

          {l.rotation ? (
            <div className="log-card__detail-spaced">
              <b>Rotation:</b>{" "}
              <code>{l.rotation.from}</code> → <code>{l.rotation.to}</code>
            </div>
          ) : null}

          {l.responseHeaders && Object.keys(l.responseHeaders).length > 0 ? (
            <div className="log-card__detail-spaced">
              <b>Response headers:</b>{" "}
              <code>
                {Object.entries(l.responseHeaders)
                  .map(([key, value]) => `${key}=${value}`)
                  .join(" | ")}
              </code>
            </div>
          ) : null}

          {typeof l.responseBody === "string" && l.responseBody.length > 0 ? (
            <details className="log-card__details-block">
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
