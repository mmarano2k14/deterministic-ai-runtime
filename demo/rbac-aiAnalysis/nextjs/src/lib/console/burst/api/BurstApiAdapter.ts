import { HeaderOverride, RequestSpec } from "@/lib/infrastructure/transport/http/HttpClientType";
import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import { ApiCallResult, IBurstApi } from "../execution/BurstController";


/**
 * MultiplexedRbacApi à l'interface burst.
 *
 * Important:
 * - Le burst NE gère PAS la rotation globale de session.
 * - La rotation reste dans MultiplexedRbacApi / ClientSession.
 * - En revanche, un mode burst peut imposer un contextKey figé
 *   pour une vague donnée via contextKeyOverride.
 */
export class BurstApiAdapter implements IBurstApi {
  constructor(private readonly api: MultiplexedRbacApi) {}

  async call(
    spec: RequestSpec,
    options?: HeaderOverride
  ): Promise<ApiCallResult> {
    const startedAt = performance.now();

    try {
      const r = await this.api.call(spec, options);

      const durationMs = Math.max(0, performance.now() - startedAt);

      if (r?.kind === "ok") {
        const status: number | undefined = r?.response?.status;
        if (typeof status !== "number") {
          return {
            kind: "error",
            durationMs,
            message: "Missing status in OK response",
          };
        }

        return { kind: "ok", status, durationMs, rotation: r.rotation };
      }

      const status: number | undefined = r?.response?.status;
      const msg =
        r?.error?.error
          ? `${r.error.error}${r.error.details ? `: ${r.error.details}` : ""}`
          : "Unknown error";

      return { kind: "error", status, durationMs, message: msg };
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      return {
        kind: "error",
        durationMs: Math.max(0, performance.now() - startedAt),
        message: msg,
      };
    }
  }
}