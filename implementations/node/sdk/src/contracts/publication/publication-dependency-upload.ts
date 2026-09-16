import type { AiSdkPublicationDependencyPackage } from "./publication-dependency-package.js";
import type { AiSdkPublicationFileUpload } from "./publication-file-upload.js";

export interface AiSdkPublicationDependencyUpload {
  readonly name: string;
  readonly version: string;
  readonly files?: readonly AiSdkPublicationFileUpload[];
  readonly package?: AiSdkPublicationDependencyPackage;
}
