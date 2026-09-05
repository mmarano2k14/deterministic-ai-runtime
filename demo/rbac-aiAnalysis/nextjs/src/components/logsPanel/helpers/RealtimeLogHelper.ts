import { RealtimeLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import { LogBadge } from "../LogsPanelType";

export type RealtimeLevelTone =
  | "neutral"
  | "info"
  | "warning"
  | "danger"
  | "critical";

export class RealtimeLogHelper {
  public static getLevelTone(level?: string): RealtimeLevelTone {
    if (!level) {
      return "neutral";
    }

    switch (level.toLowerCase()) {
      case "debug":
        return "neutral";

      case "information":
      case "info":
        return "info";

      case "warning":
        return "warning";

      case "error":
        return "danger";

      case "critical":
        return "critical";

      default:
        return "neutral";
    }
  }

  public static getBadges(log: RealtimeLogEntry): LogBadge[] {
    const badges: LogBadge[] = [
      {
        label: "REALTIME",
        tone: "success",
      },
    ];

    const eventName = (log.eventName ?? "").toLowerCase();
    const message = (log.message ?? "").toLowerCase();
    const category = (log.category ?? "").toLowerCase();

    if (eventName.includes("context-rotated") || message.includes("rotated")) {
      badges.push({
        label: "CONTEXT ROTATED",
        tone: "warning",
      });
    }

    if (
      category.includes("executioncontext")
      || message.includes("executioncontext")
      || message.includes("execution context")
    ) {
      badges.push({
        label: "CONTEXT KEY",
        tone: "purple",
      });
    }

    if (
      category.startsWith("ai.")
      && !category.startsWith("demo.ui.ai.")
    ) {
      badges.push({
        label: "RUNTIME ENGINE",
        tone: "info",
      });
    }

    if (category.startsWith("demo.ui.ai.")) {
      badges.push({
        label: "AI",
        tone: "purple",
      });
    }

    if (eventName.includes("runtime-log")) {
      badges.push({
        label: "RUNTIME",
        tone: "info",
      });
    }

    if ((log.level ?? "").toLowerCase() === "warning") {
      badges.push({
        label: "WARNING",
        tone: "warning",
      });
    }

    if ((log.level ?? "").toLowerCase() === "error") {
      badges.push({
        label: "ERROR",
        tone: "danger",
      });
    }

    return badges;
  }
}
