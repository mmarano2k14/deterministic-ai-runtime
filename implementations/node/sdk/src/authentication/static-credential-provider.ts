import type { AiSdkCredential, AiSdkCredentialProvider } from "./credential.js";

export class AiSdkStaticCredentialProvider implements AiSdkCredentialProvider {
  readonly #credential: AiSdkCredential;

  public constructor(credential: AiSdkCredential) {
    this.#credential = { ...credential };
  }

  public async getCredential(signal?: AbortSignal): Promise<AiSdkCredential> {
    signal?.throwIfAborted();
    return this.#credential;
  }
}
