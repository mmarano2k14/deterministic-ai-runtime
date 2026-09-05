import type { IEntityStore, EntityStoreMode } from "./EntityStoreType";
import type {
  EntityStoreOptions,
  ApiEntityStoreOptions,
  IndexedDbEntityStoreOptions,
} from "./EntityStoreOptions";
import { LocalStorageEntityStore } from "./providers/LocalStorageEntityStore";
import { SimulatedApiEntityStore } from "./providers/SimulatedApiEntityStore";
import { IndexedDbEntityStore } from "./providers/IndexedDbEntityStore";
import { ProxyApiEntityStore } from "./providers/ProxyApiEntityStore";
import { SimpleApiEntityStore } from "./providers/SimpleApiEntityStore";

// Factory for generic entity stores
export class EntityStoreFactory {
  public static createEntityStore<T, TId = string>(
    options:
      | ({ mode?: EntityStoreMode } & EntityStoreOptions<T, TId>)
      | ({ mode?: EntityStoreMode } & IndexedDbEntityStoreOptions<T, TId>)
      | ({ mode?: EntityStoreMode } & ApiEntityStoreOptions<T, TId>)
  ): IEntityStore<T, TId> {
    switch (options.mode) {
      case "indexed-db":
        return new IndexedDbEntityStore<T, TId>(
          options as IndexedDbEntityStoreOptions<T, TId>
        );

      case "api-proxy":
        return new ProxyApiEntityStore<T, TId>(
          options as ApiEntityStoreOptions<T, TId>
        );

      case "api-simple":
        return new SimpleApiEntityStore<T, TId>(
          options as ApiEntityStoreOptions<T, TId>
        );

      case "simulated-api":
        return new SimulatedApiEntityStore<T, TId>(
          options as EntityStoreOptions<T, TId>
        );

      case "local-storage":
      default:
        return new LocalStorageEntityStore<T, TId>(
          options as EntityStoreOptions<T, TId>
        );
    }
  }
}