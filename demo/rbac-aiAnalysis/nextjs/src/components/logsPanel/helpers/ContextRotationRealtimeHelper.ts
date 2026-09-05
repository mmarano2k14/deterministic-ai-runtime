import { ConsoleLogEntry, RealtimeLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import { LogUiHelper } from "./LogUiHelper";

export type ContextRotationRealtimePayload = {
  oldContextKey: string;
  newContextKey: string;
  overlapWindowMs: number;
  path?: string;
  method?: string;
};

export type ContextRotationRealtimeEvent = {
  at: number;
  atIso: string;
  oldContextKey: string;
  newContextKey: string;
  overlapWindowMs: number;
  path?: string;
  method?: string;
};

export class ContextRotationRealtimeHelper {
  /**
   * Detects realtime logs emitted by ExecutionContextMiddleware
   * when a context key rotation is completed.
   */
  static isContextRotatedRealtimeLog(
    log: ConsoleLogEntry
  ): log is RealtimeLogEntry {
    if (!LogUiHelper.isRealtimeLogEntry(log)) {
      return false;
    }

    return (
      log.category === "Http.ExecutionContextMiddleware" &&
      log.message ===
        "ExecutionContext key rotated successfully at the end of the HTTP request."
    );
  }

  /**
   * Payload can be:
   * - already an object
   * - a JSON string
   */
  static tryParsePayload(
    raw: unknown
  ): ContextRotationRealtimePayload | null {
    if (raw == null) {
      return null;
    }

    if (typeof raw === "string") {
      try {
        const parsed = JSON.parse(raw) as Partial<ContextRotationRealtimePayload>;
        return this.normalizePayload(parsed);
      } catch {
        return null;
      }
    }

    if (typeof raw === "object") {
      return this.normalizePayload(raw as Partial<ContextRotationRealtimePayload>);
    }

    return null;
  }

  private static normalizePayload(
    parsed: Partial<ContextRotationRealtimePayload>
  ): ContextRotationRealtimePayload | null {
    if (
      typeof parsed.oldContextKey !== "string" ||
      typeof parsed.newContextKey !== "string"
    ) {
      return null;
    }

    return {
      oldContextKey: parsed.oldContextKey,
      newContextKey: parsed.newContextKey,
      overlapWindowMs:
        typeof parsed.overlapWindowMs === "number"
          ? parsed.overlapWindowMs
          : 0,
      path: typeof parsed.path === "string" ? parsed.path : undefined,
      method: typeof parsed.method === "string" ? parsed.method : undefined,
    };
  }

  static extractRotationEvents(logs: ConsoleLogEntry[]): ContextRotationRealtimeEvent[] {
    return logs
        .filter(this.isContextRotatedRealtimeLog)
        .map((log): ContextRotationRealtimeEvent | null => {
        const payload = this.tryParsePayload(log.payload);

        if (!payload) {
            return null;
        }

        const event: ContextRotationRealtimeEvent = {
            at: new Date(log.t).getTime(),
            atIso: log.t,
            oldContextKey: payload.oldContextKey,
            newContextKey: payload.newContextKey,
            overlapWindowMs: payload.overlapWindowMs,
        };

        if (payload.path !== undefined) {
            event.path = payload.path;
        }

        if (payload.method !== undefined) {
            event.method = payload.method;
        }

        return event;
        })
        .filter((x): x is ContextRotationRealtimeEvent => x !== null)
        .sort((a, b) => a.at - b.at);
    }
}