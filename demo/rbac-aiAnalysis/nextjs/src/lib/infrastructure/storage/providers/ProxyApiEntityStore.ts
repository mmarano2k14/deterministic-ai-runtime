import { EntityStoreBase } from "../EntityStoreBase";
import type { EntityFindQuery, EntityQuery } from "../EntityStoreType";
import type { ApiEntityStoreOptions } from "../EntityStoreOptions";
import { HttpClient } from "../../transport/http/HttpClient";
import type { RequestSpec } from "../../transport/http/HttpClientType";

// API store using the existing proxy
export class ProxyApiEntityStore<T, TId = string>
  extends EntityStoreBase<T, TId>
{
  private readonly baseUrl: string;
  private readonly resourcePath: string;
  private readonly client: HttpClient;
  private readonly serialize: (entity: T) => unknown;
  private readonly deserialize: (payload: unknown) => T;

  public constructor(options: ApiEntityStoreOptions<T, TId>) {
    super({
      storageKey: "__unused__",
      getId: options.getId,
      compare: options.compare,
    });

    this.baseUrl = options.baseUrl;
    this.resourcePath = options.resourcePath;
    this.client = options.client ?? new HttpClient();
    this.serialize = options.serialize ?? ((entity) => entity);
    this.deserialize = options.deserialize ?? ((payload) => payload as T);
  }

  public async getAll(query?: EntityQuery): Promise<T[]> {
    const spec: RequestSpec = {
      name: "entity-get-all",
      method: "GET",
      path: this.buildPath(query),
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      throw new Error(result.error.error);
    }

    const content = result.response.body;
    const payload = content ? (JSON.parse(content) as unknown[]) : [];
    return payload.map((x) => this.deserialize(x));
  }

  public async getById(id: TId): Promise<T | null> {
    const spec: RequestSpec = {
      name: "entity-get-by-id",
      method: "GET",
      path: `${this.resourcePath}/${encodeURIComponent(String(id))}`,
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      if (result.response?.status === 404) {
        return null;
      }

      throw new Error(result.error.error);
    }

    const content = result.response.body;
    if (!content) {
      return null;
    }

    return this.deserialize(JSON.parse(content));
  }

  public async save(entity: T): Promise<void> {
    const id = this.getId(entity);

    const spec: RequestSpec = {
      name: "entity-save",
      method: "PUT",
      path: `${this.resourcePath}/${encodeURIComponent(String(id))}`,
      body: this.serialize(entity),
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      throw new Error(result.error.error);
    }
  }

  public async delete(id: TId): Promise<void> {
    const spec: RequestSpec = {
      name: "entity-delete",
      method: "DELETE",
      path: `${this.resourcePath}/${encodeURIComponent(String(id))}`,
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      throw new Error(result.error.error);
    }
  }

  public async clear(): Promise<void> {
    const spec: RequestSpec = {
      name: "entity-clear",
      method: "DELETE",
      path: this.resourcePath,
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      throw new Error(result.error.error);
    }
  }

  public async find(query: EntityFindQuery): Promise<T[]> {
    const spec: RequestSpec = {
      name: "entity-find",
      method: "POST",
      path: `${this.resourcePath}/find`,
      body: query,
    };

    const result = await this.client.call(this.baseUrl, spec);

    if (result.kind === "error") {
      throw new Error(result.error.error);
    }

    const content = result.response.body;
    const payload = content ? (JSON.parse(content) as unknown[]) : [];
    return payload.map((x) => this.deserialize(x));
  }

  private buildPath(query?: EntityQuery): string {
    if (!query?.limit) {
      return this.resourcePath;
    }

    const params = new URLSearchParams();
    params.set("limit", String(query.limit));
    return `${this.resourcePath}?${params.toString()}`;
  }
}