export type EntityStoreMode =
  | "local-storage"
  | "simulated-api"
  | "indexed-db"
  | "api-proxy"
  | "api-simple";

// Generic query options
export type EntityQuery = {
  limit?: number;
};

// Serializable filter condition
export type EntityWhereValue =
  | string
  | number
  | boolean
  | null
  | Array<string | number | boolean | null>;

export type EntityWhere = Record<string, EntityWhereValue>;

// Generic find request
export type EntityFindQuery = {
  where?: EntityWhere;
  limit?: number;
  orderBy?: string;
  orderDirection?: "asc" | "desc";
};

// Minimal contract shared by all entity stores
export interface IEntityStore<T, TId = string> {
  getAll(query?: EntityQuery): Promise<T[]>;
  getById(id: TId): Promise<T | null>;
  save(entity: T): Promise<void>;
  delete(id: TId): Promise<void>;
  clear(): Promise<void>;
  find(query: EntityFindQuery): Promise<T[]>;
}