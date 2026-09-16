export const AI_SDK_PUBLICATION_DEPENDENCY_PACKAGE_KINDS = [
  "PythonWheelBundle",
  "NodeLockedBundle",
  "DotNetAssemblyClosure",
] as const;

export type AiSdkPublicationDependencyPackageKind =
  (typeof AI_SDK_PUBLICATION_DEPENDENCY_PACKAGE_KINDS)[number];
