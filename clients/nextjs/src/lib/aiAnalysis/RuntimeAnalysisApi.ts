import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type {
  RuntimeAnalysisSnapshot,
  RuntimeAnalysisSnapshotRequest,
} from "./RuntimeAnalysisType";

export class RuntimeAnalysisApi {
  public constructor(
    private readonly rbacApi: MultiplexedRbacApi
  ) {}

  public async buildSnapshot(
    request: RuntimeAnalysisSnapshotRequest,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisSnapshot> {
    const result = await this.rbacApi.call(
      {
        name: "RUNTIME ANALYSIS SNAPSHOT",
        method: "POST",
        path: "/runtime-analysis/snapshot",
        body: request,
      },
      undefined,
      signal
    );

    if (result.kind === "error") {
      const responseBody = result.response?.body;
      const details = responseBody || result.error.details;

      throw new Error(
        details
          ? `${result.error.error}: ${details}`
          : result.error.error
      );
    }

    try {
      return JSON.parse(result.response.body) as RuntimeAnalysisSnapshot;
    } catch {
      throw new Error(
        "Runtime analysis snapshot endpoint returned an invalid JSON response."
      );
    }
  }
}
