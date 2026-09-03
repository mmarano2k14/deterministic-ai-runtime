import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import { RuntimeAnalysisApi } from "./RuntimeAnalysisApi";
import {
  RuntimeAnalysisSnapshotRequestBuilder,
  type RuntimeAnalysisSnapshotBuildInput,
} from "./RuntimeAnalysisSnapshotRequestBuilder";
import type {
  RuntimeAnalysisPreparedContext,
} from "./RuntimeAnalysisType";

export class RuntimeAnalysisSnapshotService {
  private readonly api: RuntimeAnalysisApi;

  public constructor(rbacApi: MultiplexedRbacApi) {
    this.api = new RuntimeAnalysisApi(rbacApi);
  }

  public async prepare(
    input: RuntimeAnalysisSnapshotBuildInput,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisPreparedContext> {
    const request = RuntimeAnalysisSnapshotRequestBuilder.build(input);
    const snapshot = await this.api.buildSnapshot(request, signal);

    return {
      request,
      snapshot,
    };
  }
}
