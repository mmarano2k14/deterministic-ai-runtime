import { EntityStoreBase } from "../EntityStoreBase";
import type { EntityFindQuery, EntityQuery } from "../EntityStoreType";
import type { IndexedDbEntityStoreOptions } from "../EntityStoreOptions";

type StoredEntity<T> = T & { __entityStoreId: IDBValidKey };

// IndexedDB-backed provider for larger client-side persistence.
// Keeps the same IEntityStore<T, TId> contract as the other providers.
export class IndexedDbEntityStore<T, TId = string>
  extends EntityStoreBase<T, TId>
{
  private readonly dbName: string;
  private readonly storeName: string;
  private readonly version: number;

  public constructor(options: IndexedDbEntityStoreOptions<T, TId>) {
    super({
      storageKey: "__unused__",
      getId: options.getId,
      compare: options.compare,
    });

    this.dbName = options.dbName;
    this.storeName = options.storeName;
    this.version = options.version ?? 1;
  }

  private isBrowser(): boolean {
    return typeof window !== "undefined" && typeof indexedDB !== "undefined";
  }

  // Opens the IndexedDB database and creates the object store if needed.
  private async openDb(): Promise<IDBDatabase> {
    if (!this.isBrowser()) {
      throw new Error("IndexedDB is not available in this environment.");
    }

    return new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(this.dbName, this.version);

      request.onupgradeneeded = () => {
        const db = request.result;

        if (!db.objectStoreNames.contains(this.storeName)) {
          db.createObjectStore(this.storeName, { keyPath: "__entityStoreId" });
        }
      };

      request.onsuccess = () => resolve(request.result);
      request.onerror = () =>
        reject(request.error ?? new Error("Failed to open IndexedDB."));
    });
  }

  // Converts the domain identifier into an IndexedDB-compatible key.
  private toKey(id: TId): IDBValidKey {
    if (
      typeof id === "string" ||
      typeof id === "number" ||
      id instanceof Date ||
      Array.isArray(id)
    ) {
      return id as IDBValidKey;
    }

    throw new Error(
      "IndexedDB provider only supports IDs compatible with IDBValidKey."
    );
  }

  // Adds a technical key used internally by the object store.
  private toStoredEntity(entity: T): StoredEntity<T> {
    return {
      ...(entity as Record<string, unknown>),
      __entityStoreId: this.toKey(this.getId(entity)),
    } as StoredEntity<T>;
  }

  // Removes the internal IndexedDB key before returning the entity to the domain.
  private fromStoredEntity(entity: StoredEntity<T>): T {
    const { ...rest } = entity as StoredEntity<T> & Record<string, unknown>;

    return rest as T; 
  }

  // Executes a readonly request and resolves with the request result.
  private async runReadOnly<TResult>(
    action: (store: IDBObjectStore) => IDBRequest<TResult>
  ): Promise<TResult> {
    const db = await this.openDb();

    return new Promise<TResult>((resolve, reject) => {
      const transaction = db.transaction(this.storeName, "readonly");
      const store = transaction.objectStore(this.storeName);
      const request = action(store);

      request.onsuccess = () => resolve(request.result);
      request.onerror = () =>
        reject(request.error ?? new Error("IndexedDB readonly request failed."));

      transaction.oncomplete = () => db.close();

      transaction.onabort = () => {
        db.close();
        reject(transaction.error ?? new Error("IndexedDB transaction aborted."));
      };

      transaction.onerror = () => {
        db.close();
        reject(transaction.error ?? new Error("IndexedDB transaction failed."));
      };
    });
  }

  // Executes a readwrite request and resolves when the transaction completes.
  private async runReadWrite<TResult>(
    action: (store: IDBObjectStore) => IDBRequest<TResult>
  ): Promise<void> {
    const db = await this.openDb();

    return new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(this.storeName, "readwrite");
      const store = transaction.objectStore(this.storeName);
      const request = action(store);

      request.onerror = () =>
        reject(request.error ?? new Error("IndexedDB write request failed."));

      transaction.oncomplete = () => {
        db.close();
        resolve();
      };

      transaction.onabort = () => {
        db.close();
        reject(transaction.error ?? new Error("IndexedDB transaction aborted."));
      };

      transaction.onerror = () => {
        db.close();
        reject(transaction.error ?? new Error("IndexedDB transaction failed."));
      };
    });
  }

  // Internal read used by getAll and find.
  private async getAllInternal(): Promise<T[]> {
    const result = await this.runReadOnly<StoredEntity<T>[]>((store) =>
      store.getAll()
    );

    if (!Array.isArray(result)) {
      return [];
    }

    return result.map((item) => this.fromStoredEntity(item));
  }

  public async getAll(query?: EntityQuery): Promise<T[]> {
    const items = this.sort(await this.getAllInternal());
    return this.applyQuery(items, query);
  }

  public async getById(id: TId): Promise<T | null> {
    const result = await this.runReadOnly<StoredEntity<T> | undefined>((store) =>
      store.get(this.toKey(id))
    );

    if (!result) {
      return null;
    }

    return this.fromStoredEntity(result);
  }

  public async save(entity: T): Promise<void> {
    await this.runReadWrite<IDBValidKey>((store) =>
      store.put(this.toStoredEntity(entity))
    );
  }

  public async delete(id: TId): Promise<void> {
    await this.runReadWrite<undefined>((store) =>
      store.delete(this.toKey(id))
    );
  }

  public async clear(): Promise<void> {
    await this.runReadWrite<undefined>((store) => store.clear());
  }

  public async find(query: EntityFindQuery): Promise<T[]> {
    const items = await this.getAllInternal();
    return this.applyFindQuery(items, query);
  }
}