import { EntityStoreBase } from "../EntityStoreBase";
import type { EntityFindQuery, EntityQuery } from "../EntityStoreType";
import type { ApiEntityStoreOptions } from "../EntityStoreOptions";

// API store using direct fetch calls
export class SimpleApiEntityStore<T, TId = string>
  extends EntityStoreBase<T, TId>
{
  private readonly baseUrl: string;
  private readonly resourcePath: string;
  private readonly serialize: (entity: T) => unknown;
  private readonly deserialize: (payload: unknown) => T;

  public constructor(options: ApiEntityStoreOptions<T, TId>) {
    super({
      storageKey: "__unused__",
      getId: options.getId,
      compare: options.compare,
    });

    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.resourcePath = options.resourcePath.startsWith("/")
      ? options.resourcePath
      : `/${options.resourcePath}`;
    this.serialize = options.serialize ?? ((entity) => entity);
    this.deserialize = options.deserialize ?? ((payload) => payload as T);
  }

  public async getAll(query?: EntityQuery): Promise<T[]> {
    const response = await fetch(this.buildUrl(query), {
      method: "GET",
      headers: {
        "Content-Type": "application/json",
      },
      credentials: "include",
    });

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }

    const payload = (await response.json()) as unknown[];
    const items = payload.map((x) => this.deserialize(x));

    return this.applyQuery(this.sort(items), query);
  }

  public async getById(id: TId): Promise<T | null> {
    const response = await fetch(
      `${this.baseUrl}${this.resourcePath}/${encodeURIComponent(String(id))}`,
      {
        method: "GET",
        headers: {
          "Content-Type": "application/json",
        },
        credentials: "include",
      }
    );

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }

    return this.deserialize(await response.json());
  }

  public async save(entity: T): Promise<void> {
    const id = this.getId(entity);

    const response = await fetch(
      `${this.baseUrl}${this.resourcePath}/${encodeURIComponent(String(id))}`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
        },
        credentials: "include",
        body: JSON.stringify(this.serialize(entity)),
      }
    );

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }
  }

  public async delete(id: TId): Promise<void> {
    const response = await fetch(
      `${this.baseUrl}${this.resourcePath}/${encodeURIComponent(String(id))}`,
      {
        method: "DELETE",
        headers: {
          "Content-Type": "application/json",
        },
        credentials: "include",
      }
    );

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }
  }

  public async clear(): Promise<void> {
    const response = await fetch(`${this.baseUrl}${this.resourcePath}`, {
      method: "DELETE",
      headers: {
        "Content-Type": "application/json",
      },
      credentials: "include",
    });

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }
  }

  public async find(query: EntityFindQuery): Promise<T[]> {
    const response = await fetch(`${this.baseUrl}${this.resourcePath}/find`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      credentials: "include",
      body: JSON.stringify(query),
    });

    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}.`);
    }

    const payload = (await response.json()) as unknown[];
    return payload.map((x) => this.deserialize(x));
  }

  private buildUrl(query?: EntityQuery): string {
    const url = new URL(`${this.baseUrl}${this.resourcePath}`);

    if (query?.limit && query.limit > 0) {
      url.searchParams.set("limit", String(query.limit));
    }

    return url.toString();
  }
}