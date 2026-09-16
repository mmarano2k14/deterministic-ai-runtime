import type { AiSdkPublicationCallSite } from "./publication-call-site.js";
import type { AiSdkPublicationDependencyUpload } from "./publication-dependency-upload.js";
import type { AiSdkPublicationFileUpload } from "./publication-file-upload.js";

export interface AiSdkPublicationFunctionUpload {
  readonly site: AiSdkPublicationCallSite;
  readonly environmentRef: string;
  readonly entryPointPath: string;
  readonly entryPointSymbol: string;
  readonly sources?: readonly AiSdkPublicationFileUpload[];
  readonly dependencies?: readonly AiSdkPublicationDependencyUpload[];
}
