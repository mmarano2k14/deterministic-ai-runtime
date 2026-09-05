import { HttpLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import { LogBadge } from "../LogsPanelType";

export type HttpStatusTone =
  | "neutral"
  | "success"
  | "info"
  | "warning"
  | "danger";

export class HttpLogHelper {
  public static getBadges(log: HttpLogEntry): LogBadge[] {
    const badges: LogBadge[] = [
      {
        label: "HTTP",
        tone: "neutral",
      },
    ];

    const path = (log.path ?? "").toLowerCase();
    const name = (log.name ?? "").toLowerCase();

    if (path.includes("refund") || name.includes("refund")) {
      badges.push({
        label: "REFUND",
        tone: "purple",
      });
    }

    if (path.includes("invoice") || name.includes("read")) {
      badges.push({
        label: "READ",
        tone: "info",
      });
    }

    if (path.includes("login") || name.includes("login")) {
      badges.push({
        label: "LOGIN",
        tone: "success",
      });
    }

    if (log.rotation) {
      badges.push({
        label: "CONTEXT ROTATED",
        tone: "warning",
      });
    }

    if (log.status === 401) {
      badges.push({
        label: "UNAUTHORIZED",
        tone: "warning",
      });
    }

    if (log.status === 403) {
      badges.push({
        label: "FORBIDDEN",
        tone: "danger",
      });
    }

    if (typeof log.status === "number" && log.status >= 500) {
      badges.push({
        label: "SERVER ERROR",
        tone: "danger",
      });
    }

    return badges;
  }

  public static getStatusTone(status?: number): HttpStatusTone {
    if (typeof status !== "number") {
      return "neutral";
    }

    if (status >= 200 && status < 300) return "success";
    if (status >= 300 && status < 400) return "info";
    if (status >= 400 && status < 500) return "warning";
    if (status >= 500) return "danger";

    return "neutral";
  }
}
