import type { HttpClient } from "@/lib/infrastructure/transport/http/HttpClient";
import type { EntityStoreMode } from "./EntityStoreType";

export type EntityStoreOptions<T, TId> = {
  mode?: EntityStoreMode;
  storageKey: string;
  getId: (entity: T) => TId;
  compare?: (left: T, right: T) => number;
};

export type ApiEntityStoreOptions<T, TId> = {
  baseUrl: string;
  resourcePath: string;
  getId: (entity: T) => TId;
  compare?: (left: T, right: T) => number;
  client?: HttpClient;
  serialize?: (entity: T) => unknown;
  deserialize?: (payload: unknown) => T;
};

export type IndexedDbEntityStoreOptions<T, TId> = {
  dbName: string;
  storeName: string;
  version?: number;
  getId: (entity: T) => TId;
  compare?: (left: T, right: T) => number;
};