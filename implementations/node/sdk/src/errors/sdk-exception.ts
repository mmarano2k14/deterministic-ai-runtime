import type { AiSdkError } from "./sdk-error.js";

export class AiSdkException extends Error {
  public readonly sdkError: AiSdkError;

  public constructor(error: AiSdkError, options?: ErrorOptions) {
    super(error.message, options);
    this.name = "AiSdkException";
    this.sdkError = error;
  }
}
