// Isolated bridge for mounting the actual main.tsx with spaces: the catalog of the project fixture, and a host
// that keeps two spaces in memory. No native bridge or user data is touched.
import type { SpaceAssignRequest, SpaceItem, SpaceShownRequest, SpacesEvent } from "#neoastra";
export * from "../projectFocus.neoastra.mount";

const space = (id: string, name: string, projectIds: string[], look: Partial<SpaceItem> = {}): SpaceItem =>
  ({ id, name, description: null, icon: null, color: null, isDefault: id === "default", projectIds, file: id === "default" ? null : `/fixture/spaces/${id}.md`, ...look });
// The default space holds both projects of the fixture; Work has the second one.
const listed: SpaceItem[] = [space("default", "Default", ["project", "other"]), space("work", "Work", ["other"], { icon: "briefcase", color: "#2d72d2" })];
const fixture: { shown: string[]; assigned: SpaceAssignRequest[]; tell: (event: SpacesEvent) => void; sessions: object[] } = { shown: [], assigned: [], tell: () => { }, sessions: [] };
Object.assign(window, { spacesFixture: fixture });
const done = async () => ({ status: "ok", message: null, space: null });

export const spaces = {
  list: async () => ({ status: "ok", spaces: listed }),
  activity: async () => ({ status: "ok", sessions: fixture.sessions, truncated: false }),
  create: done, update: done, delete: done, reorder: done,
  assign: async (request: SpaceAssignRequest) => {
    fixture.assigned.push(request);
    for (const item of listed) {
      const index = listed.indexOf(item);
      const joins = request.join?.includes(item.id), leaves = request.leave?.includes(item.id);
      if (joins || leaves) listed[index] = { ...item, projectIds: [...item.projectIds.filter(id => id !== request.projectId), ...(joins ? [request.projectId!] : [])] };
    }
    return done();
  },
  shown: async (request: SpaceShownRequest) => { fixture.shown.push(request.id ?? ""); return done(); },
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    const waiting: SpacesEvent[] = [];
    let wake = () => { };
    fixture.tell = event => { waiting.push(event); wake(); };
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (waiting.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (waiting.length > 0) yield waiting.shift()!;
      }
    })();
  },
};
