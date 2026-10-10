// The page's link to the canvases of plugins: what plugins declare, the instances that tabs show, and what plugins push to them.
import type { canvases, CanvasActionResponse, CanvasEvent, CanvasItem, CanvasOpenResponse } from "#neoastra";

export type CanvasApi = Pick<typeof canvases, "list" | "open" | "visible" | "close" | "closeSpace" | "action" | "describe" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** The most open requests kept for a window that has not taken them yet, and the most instances whose last events are kept for a tab that comes late. */
export const retainedLimit = 64;

/** A plugin asks for a tab: the canvas, where, about what, and whether to bring it to the front. */
export type CanvasOpenRequest = Readonly<{
  pluginKey: string; canvasId: string; spaceId: string | null; projectId: string | null; sessionId: string | null; key: string | null;
  /** The id of the folder of the plugin package, or null for a built-in plugin. */
  plugin: string | null; focus: boolean; title: string | null; icon: string | null;
}>;

/** What happened to an instance a tab shows. `update` carries only what changed. */
export type CanvasInstanceEvent =
  | Readonly<{ kind: "update"; revision: number; html: string | null; title: string | null; statusText: string | null; actions: boolean | null; state: string | null }>
  | Readonly<{ kind: "state"; state: string }>
  | Readonly<{ kind: "closed" }>;

export type CanvasHub = ReturnType<typeof createCanvasHub>;

const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit && !/[\u0000-\u001f\u007f]/u.test(value);

/** Reads an `open` event of the host as a request for a tab; anything that is not well formed is null. */
export function readOpenRequest(event: CanvasEvent): CanvasOpenRequest | null {
  if (event.kind !== "open" || !text(event.pluginKey, 512) || typeof event.canvasId !== "string" || !/^[A-Za-z0-9._-]{1,64}$/u.test(event.canvasId)) return null;
  const optional = (value: unknown, limit: number) => value === null || value === undefined || value === "" ? null : text(value, limit) ? value : undefined;
  const spaceId = optional(event.spaceId, 256), projectId = optional(event.projectId, 256), sessionId = optional(event.sessionId, 128), key = optional(event.key, 128);
  if (spaceId === undefined || projectId === undefined || sessionId === undefined || key === undefined) return null;
  return { pluginKey: event.pluginKey, canvasId: event.canvasId, spaceId, projectId, sessionId, key, plugin: optional(event.package, 512) ?? null, focus: event.focus,
    title: optional(event.title, 200) ?? null, icon: optional(event.icon, 64) ?? null };
}

/** Reads an event about an instance; null for what is not one or is not well formed. */
export function readInstanceEvent(event: CanvasEvent): { instanceId: string; event: CanvasInstanceEvent } | null {
  if (!text(event.instanceId, 256)) return null;
  const instanceId = event.instanceId;
  if (event.kind === "closed") return { instanceId, event: { kind: "closed" } };
  if (event.kind === "state") return text(event.state, 64) ? { instanceId, event: { kind: "state", state: event.state } } : null;
  if (event.kind !== "update" || typeof event.revision !== "number" || !Number.isSafeInteger(event.revision)) return null;
  // A fragment or a title that is not text, or is too large, is no update of it: the rest of the event stands.
  const html = typeof event.html === "string" && event.html.length <= 256 * 1024 ? event.html : null;
  const title = typeof event.title === "string" && event.title.length <= 200 ? event.title : null;
  const statusText = typeof event.statusText === "string" && event.statusText.length <= 200 ? event.statusText : null;
  return { instanceId, event: { kind: "update", revision: event.revision, html, title, statusText, actions: typeof event.actions === "boolean" ? event.actions : null,
    state: typeof event.state === "string" && event.state.length <= 64 ? event.state : null } };
}

// What the second update says wins; what it does not say stays.
function mergeUpdates(older: Extract<CanvasInstanceEvent, { kind: "update" }>, newer: Extract<CanvasInstanceEvent, { kind: "update" }>): Extract<CanvasInstanceEvent, { kind: "update" }> {
  return { kind: "update", revision: Math.max(older.revision, newer.revision), html: newer.html ?? older.html, title: newer.title ?? older.title,
    statusText: newer.statusText ?? older.statusText, actions: newer.actions ?? older.actions, state: newer.state ?? older.state };
}

/**
 * Keeps what the host says of the canvases. It lists the canvases of the plugins once it is connected and again each
 * time the host says plugins changed, hands the requests of plugins for a tab to the window, and gives the events of an
 * instance to the tab that shows it. Every call a tab makes goes through it, with the host epoch.
 */
export function createCanvasHub(api: CanvasApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let epoch: string | null = null;
  let catalog: readonly CanvasItem[] = [];
  let listing = 0;
  let version = 0;
  let onOpen: ((request: CanvasOpenRequest) => void) | null = null;
  const waiting: CanvasOpenRequest[] = [];
  const catalogListeners = new Set<() => void>();
  const changeListeners = new Set<() => void>();
  const attached = new Map<string, Set<(event: CanvasInstanceEvent) => void>>();
  // The last events of the instances nobody listens to yet: a tab attaches after its open call returned, and events may have come before.
  const retained = new Map<string, { update: Extract<CanvasInstanceEvent, { kind: "update" }> | null; last: CanvasInstanceEvent | null }>();

  function retain(instanceId: string, event: CanvasInstanceEvent) {
    const known = retained.get(instanceId) ?? { update: null, last: null };
    retained.delete(instanceId); // The newest goes last.
    retained.set(instanceId, event.kind === "update" ? { update: known.update ? mergeUpdates(known.update, event) : event, last: known.last }
      : { update: known.update, last: event });
    while (retained.size > retainedLimit) retained.delete(retained.keys().next().value!);
  }

  function deliver(instanceId: string, event: CanvasInstanceEvent) {
    const listeners = attached.get(instanceId);
    if (listeners && listeners.size > 0) { for (const listener of [...listeners]) listener(event); return; }
    retain(instanceId, event);
  }

  function open(request: CanvasOpenRequest) {
    if (onOpen) { onOpen(request); return; }
    if (waiting.length === 16) waiting.shift();
    waiting.push(request);
  }

  async function list() {
    const host = epoch;
    if (!host) return;
    const turn = ++listing;
    try {
      const reply = await api.list({ expectedEpoch: host }, { timeoutMilliseconds: 15_000 });
      if (turn !== listing || epoch !== host || reply.status !== "ok") return;
      catalog = reply.canvases;
      version++;
      for (const listener of [...catalogListeners]) listener();
    } catch { /* The host is gone: the next connection lists again. */ }
  }

  function changed() { for (const listener of [...changeListeners]) listener(); }

  return {
    /** The canvases the plugins declare now. */
    getCatalog: () => catalog,
    /** Changes with each new listing: for `useSyncExternalStore`. */
    getVersion: () => version,
    /** Reads the canvases the plugins declare again: what the Canvases page asks for when it is shown. */
    refresh: () => list(),
    subscribeCatalog(listener: () => void) { catalogListeners.add(listener); return () => { catalogListeners.delete(listener); }; },
    /** Calls the listener when plugins started, were replaced or stopped, and when the host is reached again: a tab that waits for its plugin asks again. */
    subscribeChanges(listener: () => void) { changeListeners.add(listener); return () => { changeListeners.delete(listener); }; },
    /** The window's way to take the requests of plugins for a tab; the ones that came before it was ready are given to it now. */
    onOpenRequest(handler: ((request: CanvasOpenRequest) => void) | null) {
      onOpen = handler;
      if (handler) for (const request of waiting.splice(0)) handler(request);
    },
    /** Whether the page is connected to a host. */
    get connected() { return epoch !== null; },
    /**
     * Gives the events of an instance to a listener: first what came after `since` while no one listened, then what comes.
     * The returned function ends it.
     */
    attach(instanceId: string, since: number, listener: (event: CanvasInstanceEvent) => void) {
      const known = retained.get(instanceId);
      retained.delete(instanceId);
      let listeners = attached.get(instanceId);
      if (!listeners) attached.set(instanceId, listeners = new Set());
      listeners.add(listener);
      if (known?.update && known.update.revision > since) listener(known.update);
      if (known?.last) listener(known.last);
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0 && attached.get(instanceId) === listeners) attached.delete(instanceId);
      };
    },
    /** Opens the instance a tab shows. A host that cannot answer is `unavailable`. */
    async open(request: Readonly<{ pluginKey: string; canvasId: string; spaceId: string | null; projectId: string | null; sessionId: string | null; key: string | null; visible: boolean }>): Promise<CanvasOpenResponse> {
      const host = epoch;
      if (!host) return { status: "unavailable", instanceId: null, title: null, statusText: null, html: null, actions: false, revision: 0, package: null, icon: null };
      try {
        return await api.open({ expectedEpoch: host, ...request }, { timeoutMilliseconds: 45_000 });
      } catch { return { status: "unavailable", instanceId: null, title: null, statusText: null, html: null, actions: false, revision: 0, package: null, icon: null }; }
    },
    /** Says whether a tab shows an instance; false when the host could not be told. */
    async setVisible(instanceId: string, visible: boolean): Promise<boolean> {
      const host = epoch;
      if (!host) return false;
      try { return (await api.visible({ expectedEpoch: host, instanceId, visible }, { timeoutMilliseconds: 10_000 })).status === "ok"; } catch { return false; }
    },
    /** Closes the instance of a tab that was closed. */
    async close(instanceId: string): Promise<void> {
      const host = epoch;
      if (!host) return;
      try { await api.close({ expectedEpoch: host, instanceId }, { timeoutMilliseconds: 30_000 }); } catch { /* The plugin closes it with its own end. */ }
    },
    /** Closes the instances of a space that was deleted. */
    async closeSpace(spaceId: string): Promise<void> {
      const host = epoch;
      if (!host) return;
      try { await api.closeSpace({ expectedEpoch: host, spaceId }, { timeoutMilliseconds: 30_000 }); } catch { /* The instances wait to be asked for again, and are evicted in their turn. */ }
    },
    /** Raises the action of an element of a fragment. */
    async action(instanceId: string, action: string, value: string | null, values: Record<string, string>): Promise<CanvasActionResponse> {
      const host = epoch;
      if (!host) return { status: "unavailable", html: null, closed: false };
      try { return await api.action({ expectedEpoch: host, instanceId, action, value, values }, { timeoutMilliseconds: 45_000 }); }
      catch { return { status: "unavailable", html: null, closed: false }; }
    },
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      async function watch() {
        try {
          const events = await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal });
          // Connected: read what plugins declare, and let the tabs that wait ask again.
          void list();
          changed();
          for await (const event of events) {
            if (abort.signal.aborted) return;
            if (event.kind === "open") { const request = readOpenRequest(event); if (request) open(request); }
            else if (event.kind === "plugins") { void list(); changed(); }
            else { const read = readInstanceEvent(event); if (read) deliver(read.instanceId, read.event); }
          }
        } catch { /* The channel ended: watch again below, unless the window is going away. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (epoch === hostEpoch) epoch = null;
        retained.clear();
      };
    },
  };
}
