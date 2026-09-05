import type {
  EntityFindQuery,
  EntityQuery,
  IEntityStore,
} from "./EntityStoreType";

// Generic facade base.
// Adds no business logic, only delegates to the inner store.
export abstract class EntityStoreFacadeBase<T, TId = string>
  implements IEntityStore<T, TId>
{
  protected constructor(protected readonly inner: IEntityStore<T, TId>) {}

  public async getAll(query?: EntityQuery): Promise<T[]> {
    return this.inner.getAll(query);
  }

  public async getById(id: TId): Promise<T | null> {
    return this.inner.getById(id);
  }

  public async save(entity: T): Promise<void> {
    return this.inner.save(entity);
  }

  public async delete(id: TId): Promise<void> {
    return this.inner.delete(id);
  }

  public async clear(): Promise<void> {
    return this.inner.clear();
  }

  public async find(query: EntityFindQuery): Promise<T[]> {
    return this.inner.find(query);
  }
}