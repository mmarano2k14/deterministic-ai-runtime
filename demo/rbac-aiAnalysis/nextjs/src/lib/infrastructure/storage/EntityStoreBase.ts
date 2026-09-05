import type {
  EntityFindQuery,
  EntityQuery,
  IEntityStore,
} from "./EntityStoreType";
import type { EntityStoreOptions } from "./EntityStoreOptions";

// Base class with common helpers
export abstract class EntityStoreBase<T, TId = string>
  implements IEntityStore<T, TId>
{
  protected readonly getId: (entity: T) => TId;
  protected readonly compare?: (left: T, right: T) => number;

  protected constructor(options: EntityStoreOptions<T, TId>) {
    this.getId = options.getId;
    this.compare = options.compare;
  }

  public abstract getAll(query?: EntityQuery): Promise<T[]>;
  public abstract getById(id: TId): Promise<T | null>;
  public abstract save(entity: T): Promise<void>;
  public abstract delete(id: TId): Promise<void>;
  public abstract clear(): Promise<void>;
  public abstract find(query: EntityFindQuery): Promise<T[]>;

  protected applyQuery(items: T[], query?: EntityQuery): T[] {
    if (!query?.limit || query.limit <= 0) {
      return items;
    }

    return items.slice(0, query.limit);
  }

  protected sort(items: T[]): T[] {
    if (!this.compare) {
      return [...items];
    }

    return [...items].sort(this.compare);
  }

  protected upsert(items: T[], entity: T): T[] {
    const id = this.getId(entity);
    const index = items.findIndex((x) => this.getId(x) === id);

    if (index < 0) {
      return [entity, ...items];
    }

    const next = [...items];
    next[index] = entity;
    return next;
  }

  protected applyFindQuery(items: T[], query: EntityFindQuery): T[] {
    let result = [...items];

    if (query.where) {
      result = result.filter((item) => this.matchesWhere(item, query.where!));
    }

    if (query.orderBy) {
      const direction = query.orderDirection === "asc" ? 1 : -1;
      result.sort((left, right) => {
        const a = (left as Record<string, unknown>)[query.orderBy!];
        const b = (right as Record<string, unknown>)[query.orderBy!];

        if (a === b) return 0;
        return a! > b! ? direction : -direction;
      });
    } else {
      result = this.sort(result);
    }

    if (query.limit && query.limit > 0) {
      result = result.slice(0, query.limit);
    }

    return result;
  }

  private matchesWhere(
    item: T,
    where: Record<
      string,
      string | number | boolean | null | Array<string | number | boolean | null>
    >
  ): boolean {
    const record = item as Record<string, unknown>;

    return Object.entries(where).every(([key, expected]) => {
      const actual = record[key];

      if (Array.isArray(expected)) {
        return expected.includes(actual as never);
      }

      return actual === expected;
    });
  }
}