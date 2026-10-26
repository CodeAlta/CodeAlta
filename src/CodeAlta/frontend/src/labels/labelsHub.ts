// The page's link to the labels of the catalog: what they are now, and the calls that change them.
import type { labels as labelsService, LabelChangeResponse } from "#neoastra";
import { readLabels, sameLabels, type Label } from "./labels";

export type LabelsApi = Pick<typeof labelsService, "list" | "create" | "update" | "move" | "delete" | "assign">;

/** What the page shows of the labels. `loaded` is false until the host answered once. */
export type LabelsState = Readonly<{ loaded: boolean; available: boolean; labels: readonly Label[] }>;

/** How a change ended: done, or refused with what the host said. */
export type LabelOutcome = Readonly<{ ok: boolean; status: string; message: string | null; label: Label | null }>;

/** What to change of a label: a value left out stays as it is, an empty text is removed. */
export type LabelEdit = Readonly<{ name?: string; description?: string; icon?: string; color?: string; favorite?: boolean; space?: string }>;

const empty: LabelsState = Object.freeze({ loaded: false, available: false, labels: Object.freeze([]) as readonly Label[] });

export type LabelsHub = ReturnType<typeof createLabelsHub>;

/**
 * Keeps what the host says of the labels. It lists once it is connected, when it is asked to read them again (a window
 * or a command changed them: the spaces hub hears it), and when a call that changes something returns.
 */
export function createLabelsHub(api: LabelsApi) {
  let state = empty;
  let epoch: string | null = null;
  let listing = 0;
  const listeners = new Set<() => void>();
  const show = (next: LabelsState) => { state = next; for (const listener of [...listeners]) listener(); };

  async function list() {
    const host = epoch;
    if (!host) return;
    const turn = ++listing;
    try {
      const reply = await api.list({ expectedEpoch: host }, { timeoutMilliseconds: 15_000 });
      if (turn !== listing || epoch !== host) return;
      // A host without labels, or a catalog that cannot be read now: the window goes on without labels.
      if (reply.status !== "ok") { if (!state.loaded || state.available) show({ ...empty, loaded: true }); return; }
      const labels = readLabels(reply.labels);
      if (state.loaded && state.available && sameLabels(state.labels, labels)) return;
      show({ loaded: true, available: true, labels });
    } catch {
      if (turn === listing && epoch === host && !state.loaded) show({ ...empty, loaded: true });
    }
  }

  async function change(call: (host: string) => Promise<LabelChangeResponse>): Promise<LabelOutcome> {
    const host = epoch;
    if (!host) return { ok: false, status: "unavailable", message: null, label: null };
    try {
      const reply = await call(host);
      await list();
      const label = reply.label ? readLabels([reply.label])[0] ?? null : null;
      return { ok: reply.status === "ok", status: reply.status, message: reply.message ?? null, label };
    } catch { return { ok: false, status: "failed", message: null, label: null }; }
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      epoch = hostEpoch;
      void list();
      return () => { if (epoch === hostEpoch) { epoch = null; show({ ...empty, loaded: state.loaded }); } };
    },
    /** A window without a host of its own has no labels. */
    unavailable() { if (!epoch && !state.loaded) show({ ...empty, loaded: true }); },
    /** Reads the labels again. */
    refresh: () => list(),
    /** Creates a label, last under its parent (null: at the top), and gives it to the projects named. */
    create: (name: string, look: Readonly<{ parent?: string | null; icon?: string | null; color?: string | null; space?: string | null; favorite?: boolean;
      description?: string | null }> = {}, projectIds: readonly string[] = []) =>
      change(host => api.create({ expectedEpoch: host, name, parent: look.parent ?? null, icon: look.icon ?? null, color: look.color ?? null,
        space: look.space ?? null, favorite: look.favorite ?? false, description: look.description ?? null, projectIds }, { timeoutMilliseconds: 30_000 })),
    update: (id: string, edit: LabelEdit) => change(host => api.update({ expectedEpoch: host, id, name: edit.name ?? null, description: edit.description ?? null,
      icon: edit.icon ?? null, color: edit.color ?? null, favorite: edit.favorite ?? null, space: edit.space ?? null }, { timeoutMilliseconds: 30_000 })),
    /** Moves a label under a parent (null: to the top), at a place among its new siblings (-1: last). */
    move: (id: string, parent: string | null, index: number) =>
      change(host => api.move({ expectedEpoch: host, id, parent, index }, { timeoutMilliseconds: 30_000 })),
    remove: (id: string) => change(host => api.delete({ expectedEpoch: host, id }, { timeoutMilliseconds: 30_000 })),
    /** Gives labels to a project and takes others from it, in one edit. */
    assign: (projectId: string, add: readonly string[], remove: readonly string[] = []) =>
      change(host => api.assign({ expectedEpoch: host, projectId, add, remove }, { timeoutMilliseconds: 30_000 })),
  };
}
