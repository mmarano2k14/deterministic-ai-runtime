import { EntityStoreFacadeBase } from "@/lib/infrastructure/storage/EntityStoreFacadeBase";
import type { BurstRun } from "../BurstRun";
import type { BurstRunStoreOptions, IBurstRunStore } from "./BurstRunStoreType";
import { EntityStoreFactory } from "@/lib/infrastructure/storage/EntityStoreFactory";

// BurstRun store facade.
export class BurstRunStore
  extends EntityStoreFacadeBase<BurstRun, string>
  implements IBurstRunStore
{
  public constructor(options?: BurstRunStoreOptions) {
    const mode = options?.mode ?? "local-storage";

    const getId =
      options?.getId ??
      ((run: BurstRun) => run.id);

    const compare =
      options?.compare ??
      ((left: BurstRun, right: BurstRun) => {
        const leftTime = left.createdAt ?? "";
        const rightTime = right.createdAt ?? "";
        return leftTime < rightTime ? 1 : leftTime > rightTime ? -1 : 0;
      });

    if (mode === "api-proxy" || mode === "api-simple") {
      super(
        EntityStoreFactory.createEntityStore<BurstRun, string>({
          mode,
          baseUrl: options?.baseUrl ?? "",
          resourcePath: options?.resourcePath ?? "/burst-runs",
          getId,
          compare,
          serialize: options?.serialize,
          deserialize: options?.deserialize,
        })
      );
      return;
    }

    if (mode === "indexed-db") {
      super(
        EntityStoreFactory.createEntityStore<BurstRun, string>({
          mode,
          dbName: options?.dbName ?? "console-storage",
          storeName: options?.storeName ?? "burst-runs",
          version: options?.version ?? 1,
          getId,
          compare,
        })
      );
      return;
    }

    super(
      EntityStoreFactory.createEntityStore<BurstRun, string>({
        mode,
        storageKey: options?.storageKey ?? "burst-runs",
        getId,
        compare,
      })
    );
  }

  public async getLatest(): Promise<BurstRun | null> {
    const items = await this.find({
      limit: 1,
      orderBy: "createdAt",
      orderDirection: "desc",
    });

    return items[0] ?? null;
  }

  public async getByParentRunId(parentId: string): Promise<BurstRun[]> {
    return this.find({
      where: {
        basedOnRunId: parentId,
      },
    });
  }

  public static createBurstRunStore(options?: BurstRunStoreOptions): BurstRunStore {
    return new BurstRunStore(options);
  }
}