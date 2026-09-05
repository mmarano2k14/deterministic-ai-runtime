import { EntityStoreBase } from "../EntityStoreBase";
import type { EntityFindQuery, EntityQuery } from "../EntityStoreType";
import type { EntityStoreOptions } from "../EntityStoreOptions";

// Generic localStorage-backed store
export class LocalStorageEntityStore<T, TId = string>
  extends EntityStoreBase<T, TId>
{
  private readonly storageKey: string;

  constructor(options: EntityStoreOptions<T, TId>) {
    super(options);
    this.storageKey = options.storageKey;
  }

  private isBrowser(): boolean {
    return typeof window !== "undefined";
  }

  protected read(): T[] {
    if (!this.isBrowser()) {
      return [];
    }

    const raw = window.localStorage.getItem(this.storageKey);
    if (!raw) {
      return [];
    }

    try {
      const parsed = JSON.parse(raw) as T[];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  protected write(items: T[]): void {
    if (!this.isBrowser()) {
      return;
    }

    window.localStorage.setItem(this.storageKey, JSON.stringify(items));
  }

  public async getAll(query?: EntityQuery): Promise<T[]> {
    const items = this.sort(this.read());
    return this.applyQuery(items, query);
  }

  public async getById(id: TId): Promise<T | null> {
    return this.read().find((x) => this.getId(x) === id) ?? null;
  }

  public async save(entity: T): Promise<void> {
    const items = this.read();
    const updated = this.sort(this.upsert(items, entity));
    this.write(updated);
  }

  public async delete(id: TId): Promise<void> {
    const items = this.read().filter((x) => this.getId(x) !== id);
    this.write(items);
  }

  public async clear(): Promise<void> {
    this.write([]);
  }

  public async find(query: EntityFindQuery): Promise<T[]> {
    const items = this.read();
    return this.applyFindQuery(items, query);
  }
}