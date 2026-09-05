import { HeaderOverride } from "../infrastructure/transport/http/HttpClientType";

export type Rotation = { from: string; to: string };

export class ClientSession {
  private _contextKey = "";
  private _demoUserId = "demo-user-1";
  private _maxInFlight = "5";
  private _rotationOverlapMs = "1000";

  public get contextKey(): string {
    return this._contextKey;
  }
  public set contextKey(v: string) {
    this._contextKey = (v ?? "").trim();
  }

  public get demoUserId(): string {
    return this._demoUserId;
  }
  public set demoUserId(v: string) {
    this._demoUserId = (v ?? "").trim();
  }

  public get maxInFlight(): string {
    return this._maxInFlight;
  }
  public set maxInFlight(v: string) {
    this._maxInFlight = (v ?? "").trim();
  }

  public get rotationOverlapMs(): string {
    return this._rotationOverlapMs;
  }
  public set rotationOverlapMs(v: string) {
    this._rotationOverlapMs = (v ?? "").trim();
  }

  public buildHeaders(options?: HeaderOverride): Record<string, string> {
    const h: Record<string, string> = {};
    const contextKey = options?.contextKeyOverride ?? this._contextKey;
    if (this._contextKey) h["X-Access-Context"] = contextKey
    if (this._demoUserId) h["X-Demo-UserId"] = this._demoUserId;
    if (this._maxInFlight) h["X-Demo-Max-InFlight"] = this._maxInFlight;
    if (this._rotationOverlapMs) h["X-Demo-Rotation-Overlap-Ms"] = this._rotationOverlapMs;
    return h;
  }

  public applyRotation(headers?: Record<string, string | string[]>): Rotation | undefined {
    const rotated = headers?.["x-access-context"];
    const value =
      typeof rotated === "string" ? rotated :
      Array.isArray(rotated) ? rotated[0] :
      "";

    if (value && value !== this._contextKey) {
      const rotation = { from: this._contextKey || "(empty)", to: value };
      this._contextKey = value;
      return rotation;
    }
    return undefined;
  }
}