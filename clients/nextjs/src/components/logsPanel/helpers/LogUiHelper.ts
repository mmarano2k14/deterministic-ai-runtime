import {
  ConsoleLogEntry,
  HttpLogEntry,
  RealtimeLogEntry,
} from "@/lib/infrastructure/logs/inMemoryLogType";

export class LogUiHelper {
  private static readonly AiRealtimeCategoryPrefix =
    "demo.ui.ai.";

  private static readonly RuntimeEngineCategoryPrefix =
    "ai.";

  private static readonly ExecutionContextCategory =
    "http.executioncontextmiddleware";

  public static getLogColor(log: ConsoleLogEntry): string {
    if (log.kind === "http") {
      const status = log.status ?? 0;

      if (status >= 200 && status < 300) return "#0f9d58";
      if (status >= 400 && status < 500) return "#f29900";
      if (status >= 500) return "#d93025";

      return "#1a73e8";
    }

    if (log.kind === "realtime") {
      const level = (log.level ?? "").toLowerCase();

      if (level === "error") return "#d93025";
      if (level === "warning") return "#f29900";

      return "#1a73e8";
    }

    return "#999";
  }

  public static isHttpLogEntry(
    log: ConsoleLogEntry
  ): log is HttpLogEntry {
    return log.kind === "http";
  }

  public static isRealtimeLogEntry(
    log: ConsoleLogEntry
  ): log is RealtimeLogEntry {
    return log.kind === "realtime";
  }

  public static isContextRotationLog(
    log: ConsoleLogEntry
  ): log is HttpLogEntry & {
    rotation: {
      from: string;
      to: string;
    };
  } {
    return (
      log.kind === "http"
      && log.rotation !== undefined
    );
  }

  public static isHttpErrorLogEntry(
    log: ConsoleLogEntry
  ): log is HttpLogEntry {
    return (
      log.kind === "http"
      && log.status !== undefined
      && log.status >= 400
    );
  }

  /**
   * Realtime-only ContextKey lifecycle view.
   *
   * HTTP rotations remain exclusively in the existing "Context" filter.
   */
  public static isContextKeyLog(
    log: ConsoleLogEntry
  ): boolean {
    if (!this.isRealtimeLogEntry(log)) {
      return false;
    }

    const category =
      this.normalize(log.category);

    if (
      category === this.ExecutionContextCategory
      || category.includes("executioncontext")
    ) {
      return true;
    }

    const message =
      this.normalize(log.message);

    if (
      message.includes("executioncontext")
      || message.includes("execution context")
      || message.includes("context key")
    ) {
      return true;
    }

    return this.payloadHasAnyKey(
      log.data ?? log.payload,
      [
        "contextkey",
        "oldcontextkey",
        "newcontextkey",
      ]
    );
  }

  /**
   * Realtime-only native Deterministic AI Runtime events.
   *
   * demo.ui.ai.* belongs to the dedicated AI provider filter.
   */
  public static isRuntimeEngineLog(
    log: ConsoleLogEntry
  ): boolean {
    if (!this.isRealtimeLogEntry(log)) {
      return false;
    }

    const category =
      this.normalize(log.category);

    if (
      category.startsWith(
        this.AiRealtimeCategoryPrefix
      )
    ) {
      return false;
    }

    if (
      category.startsWith(
        this.RuntimeEngineCategoryPrefix
      )
    ) {
      return true;
    }

    const message =
      this.normalize(log.message);

    return (
      message.startsWith("[ai ")
      || message.includes("[ai pipeline")
      || message.includes("[ai runtime")
    );
  }

  /**
   * Realtime-only AI provider activity from the demo backend.
   *
   * HTTP /runtime-analysis/* traffic intentionally stays in the HTTP filter.
   */
  public static isAiLog(
    log: ConsoleLogEntry
  ): boolean {
    if (!this.isRealtimeLogEntry(log)) {
      return false;
    }

    return this.normalize(log.category)
      .startsWith(
        this.AiRealtimeCategoryPrefix
      );
  }

  public static isNearTop(
    element: HTMLDivElement,
    threshold = 40
  ): boolean {
    return element.scrollTop <= threshold;
  }

  private static normalize(
    value: string | null | undefined
  ): string {
    return (value ?? "")
      .trim()
      .toLowerCase();
  }

  private static payloadHasAnyKey(
    value: unknown,
    keys: readonly string[]
  ): boolean {
    const record =
      this.toRecord(value);

    if (!record) {
      return false;
    }

    const normalizedKeys =
      new Set(
        Object.keys(record).map(
          (key) => key.toLowerCase()
        )
      );

    return keys.some(
      (key) => normalizedKeys.has(key)
    );
  }

  private static toRecord(
    value: unknown
  ): Record<string, unknown> | null {
    let candidate = value;

    if (typeof candidate === "string") {
      try {
        candidate = JSON.parse(
          candidate
        );
      } catch {
        return null;
      }
    }

    if (
      typeof candidate !== "object"
      || candidate === null
      || Array.isArray(candidate)
    ) {
      return null;
    }

    return candidate as Record<string, unknown>;
  }
}
