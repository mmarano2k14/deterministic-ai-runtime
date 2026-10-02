import type { AiSdkPublicationDependencyPackageKind } from "./publication-dependency-package-kind.js";

export interface AiSdkPublicationDependencyPackage {
  readonly schemaVersion: number;
  readonly kind: AiSdkPublicationDependencyPackageKind;
  readonly manifestPath: string;
}
