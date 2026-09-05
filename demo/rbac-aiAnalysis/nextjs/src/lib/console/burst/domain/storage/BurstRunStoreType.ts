import type {
  EntityStoreMode,
  IEntityStore,
} from "@/lib/infrastructure/storage/EntityStoreType";
import type { BurstRun } from "../BurstRun";
import type {
  ApiEntityStoreOptions,
  EntityStoreOptions,
  IndexedDbEntityStoreOptions,
} from "@/lib/infrastructure/storage/EntityStoreOptions";

export type BurstRunStoreOptions = {
  mode?: EntityStoreMode;
} & Partial<
  EntityStoreOptions<BurstRun, string> &
    IndexedDbEntityStoreOptions<BurstRun, string> &
    ApiEntityStoreOptions<BurstRun, string>
>;

export interface IBurstRunStore extends IEntityStore<BurstRun, string> {
  getLatest(): Promise<BurstRun | null>;
  getByParentRunId(parentId: string): Promise<BurstRun[]>;
}