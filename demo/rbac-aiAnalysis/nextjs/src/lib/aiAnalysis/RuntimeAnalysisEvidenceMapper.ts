import type {
  ConsoleLogEntry,
  HttpLogEntry,
  RealtimeLogEntry,
} from "@/lib/infrastructure/logs/inMemoryLogType";
import type { RuntimeAnalysisEvidenceInput } from "./RuntimeAnalysisType";

export class RuntimeAnalysisEvidenceMapper {
  private static readonly RuntimeAnalysisPathPrefix = "/runtime-analysis";
  private static readonly DemoUiCategoryPrefix = "demo.ui.";

  public static map(
    log: ConsoleLogEntry
  ): RuntimeAnalysisEvidenceInput | null {
    const timestampUtc = this.normalizeTimestamp(log.t);

    if (timestampUtc === null) {
      return null;
    }

    if (log.kind === "http") {
      return this.mapHttpLog(log, timestampUtc);
    }

    if (log.kind === "realtime") {
      return this.mapRealtimeLog(log, timestampUtc);
    }

    return null;
  }

  private static mapHttpLog(
    log: HttpLogEntry,
    timestampUtc: string
  ): RuntimeAnalysisEvidenceInput | null {
    if (this.isRuntimeAnalysisPath(log.path)) {
      return null;
    }

    return {
      timestampUtc,
      category: this.httpCategory(log),
      eventType: this.httpEventType(log),
      message: this.httpMessage(log),
      statusCode: log.status,
      correlationId: log.id,
      metadata: this.compactMetadata({
        method: log.method,
        path: log.path,
        name: log.name,
      }),
    };
  }

  private static mapRealtimeLog(
    log: RealtimeLogEntry,
    timestampUtc: string
  ): RuntimeAnalysisEvidenceInput | null {
    const sourceCategory =
      this.nonEmpty(log.category);

    if (
      sourceCategory?.toLowerCase().startsWith(
        this.DemoUiCategoryPrefix
      )
    ) {
      return null;
    }

    const payload = this.asRecord(log.payload);
    const payloadPath = this.readString(payload, "path");

    if (this.isRuntimeAnalysisPath(payloadPath)) {
      return null;
    }

    const middlewareEvent = this.mapExecutionContextMiddleware(
      log,
      timestampUtc
    );

    if (middlewareEvent) {
      return middlewareEvent;
    }

    return {
      timestampUtc,
      category: this.nonEmpty(log.category) ?? "realtime",
      eventType: this.nonEmpty(log.eventName) ?? "runtime.log",
      message: this.nonEmpty(log.message),
      correlationId: this.readString(payload, "correlationId"),
      sharedRunId: this.readString(payload, "sharedRunId"),
      executionId: this.readString(payload, "executionId"),
      dagId: this.readString(payload, "dagId"),
      stepId: this.readString(payload, "stepId"),
      childExecutionId: this.readString(payload, "childExecutionId"),
      policyKey: this.readString(payload, "policyKey"),
      metadata: this.compactMetadata({
        level: log.level,
      }),
    };
  }

  private static mapExecutionContextMiddleware(
    log: RealtimeLogEntry,
    timestampUtc: string
  ): RuntimeAnalysisEvidenceInput | null {
    const category = this.nonEmpty(log.category);
    const message = this.nonEmpty(log.message);

    if (
      category?.toLowerCase() !==
      "http.executioncontextmiddleware"
    ) {
      return null;
    }

    const eventType = this.executionContextEventType(message);

    if (!eventType) {
      return null;
    }

    return {
      timestampUtc,
      category: "context",
      eventType,
      message,
      metadata: this.compactMetadata({
        level: log.level,
        sourceCategory: category,
      }),
    };
  }

  private static executionContextEventType(
    message: string | undefined
  ): string | undefined {
    const normalized = message?.toLowerCase();

    if (!normalized) {
      return undefined;
    }

    if (normalized.includes("successfully attached")) {
      return "context.attached";
    }

    if (normalized.includes("key rotated successfully")) {
      return "context.rotated";
    }

    if (normalized.includes("released and cleared")) {
      return "context.released";
    }

    return undefined;
  }

  private static httpCategory(log: HttpLogEntry): string {
    if (log.rotation !== undefined) {
      return "context";
    }

    if (log.status === 429) {
      return "concurrency";
    }

    if (log.status === 401) {
      return "authentication";
    }

    if (log.status === 403) {
      return "authorization";
    }

    return "http";
  }

  private static httpEventType(log: HttpLogEntry): string {
    if (log.rotation !== undefined) {
      return "context.rotated";
    }

    if (log.error) {
      return "request.error";
    }

    if (log.status === 429) {
      return "request.rejected.concurrency";
    }

    if (log.status === 401) {
      return "request.rejected.unauthorized";
    }

    if (log.status === 403) {
      return "request.rejected.forbidden";
    }

    if (typeof log.status === "number" && log.status >= 500) {
      return "request.completed.server-error";
    }

    if (typeof log.status === "number" && log.status >= 400) {
      return "request.completed.client-error";
    }

    if (typeof log.status === "number") {
      return "request.completed";
    }

    return "request.started";
  }

  private static httpMessage(log: HttpLogEntry): string {
    if (log.error) {
      return log.error;
    }

    const request = `${log.method ?? "HTTP"} ${log.path ?? ""}`.trim();

    if (typeof log.status === "number") {
      return `${request} -> ${log.status}`;
    }

    return request;
  }

  private static isRuntimeAnalysisPath(
    path: string | null | undefined
  ): boolean {
    const normalized = this.nonEmpty(path);

    return (
      normalized !== undefined &&
      normalized.toLowerCase().startsWith(
        this.RuntimeAnalysisPathPrefix
      )
    );
  }

  private static normalizeTimestamp(value: string): string | null {
    const timestamp = Date.parse(value);

    if (Number.isNaN(timestamp)) {
      return null;
    }

    return new Date(timestamp).toISOString();
  }

  private static asRecord(
    value: unknown
  ): Record<string, unknown> | undefined {
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      return undefined;
    }

    return value as Record<string, unknown>;
  }

  private static readString(
    record: Record<string, unknown> | undefined,
    key: string
  ): string | undefined {
    if (!record) {
      return undefined;
    }

    const matchingKey = Object.keys(record).find(
      (candidate) => candidate.toLowerCase() === key.toLowerCase()
    );

    if (!matchingKey) {
      return undefined;
    }

    const value = record[matchingKey];

    return typeof value === "string" ? this.nonEmpty(value) : undefined;
  }

  private static compactMetadata(
    values: Record<string, string | null | undefined>
  ): Record<string, string |  null | undefined> {
    return Object.fromEntries(
      Object.entries(values).filter(
        ([, value]) => this.nonEmpty(value) !== undefined
      )
    );
  }

  private static nonEmpty(
    value: string | null | undefined
  ): string | undefined {
    const normalized = value?.trim();

    return normalized ? normalized : undefined;
  }
}
