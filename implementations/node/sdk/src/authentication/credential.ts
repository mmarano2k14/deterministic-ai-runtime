export interface AiSdkCredential {
  readonly scheme: string;
  readonly value: string;
}

export interface AiSdkCredentialProvider {
  getCredential(signal?: AbortSignal): Promise<AiSdkCredential | null>;
}
