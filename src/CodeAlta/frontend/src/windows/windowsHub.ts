// The page's link to the windows of the application: which window it is, the other windows and the tabs they hold, and
// what the host asks of this window (a tab moved into it, a tab to bring to the front, a move, a close).
import { sessionIdOfRef } from "./tabRefs";
import type { windows as windowsService, WindowsCurrentResponse, WindowsEvent, WindowsWindow } from "#neoastra";

export type WindowsApi = Pick<typeof windowsService, "list" | "watch" | "report" | "moveTab" | "refuseMove" | "focusTab" | "focus" | "close" | "open"
  | "setIdentity" | "forget" | "setRestoreAtStartup" | "displays" | "dragOver" | "drop">;

/** What the page knows of the windows. */
export type WindowsState = Readonly<{
  /** The window of this page; null where the host has no windows (a page without its host, the configuration editor). */
  current: WindowsWindow | null;
  /** The windows: the main one, the open secondary ones, the saved ones that are closed. Empty until the host answered. */
  windows: readonly WindowsWindow[];
  /** Whether the secondary windows that were open come back at the next start. */
  restoreAtStartup: boolean;
  /** Whether a tab can be dragged out of a window: the platform tells where the windows are. */
  canDrag: boolean;
  /** The id of the window that holds each session whose tab another open window holds, by session id: what the Explorer marks. */
  sessionWindows: ReadonlyMap<string, string>;
}>;

/** What the host asks of this window. */
export type WindowsRequest = WindowsEvent;

export type WindowsHub = ReturnType<typeof createWindowsHub>;

const noOwners: ReadonlyMap<string, string> = new Map();
const reconnectMilliseconds = 2000;

/** The window id of the main window. */
export const mainWindowId = "main";

/**
 * What the page of a secondary window keeps of its tabs, as the main page keeps them in the storage of the pages: the
 * session tabs, its other tabs, and how the panes hold them. Its window keeps it in `windows.json`.
 */
export type WindowPage = { sessionTabs: string | null; fileTabs: string | null; layout: string | null };

/** What a secondary window kept of its page; nothing for one that is new or a text that is not understood. */
export function readWindowPage(raw: string | null | undefined): WindowPage {
  try {
    const value: unknown = raw ? JSON.parse(raw) : null;
    if (!value || typeof value !== "object" || (value as { version?: unknown }).version !== 1) return { sessionTabs: null, fileTabs: null, layout: null };
    const text = (field: unknown) => typeof field === "string" && field.length <= 262_144 ? field : null;
    const page = value as { sessionTabs?: unknown; fileTabs?: unknown; layout?: unknown };
    return { sessionTabs: text(page.sessionTabs), fileTabs: text(page.fileTabs), layout: text(page.layout) };
  } catch { return { sessionTabs: null, fileTabs: null, layout: null }; }
}

// DesktopWindowRecord.MaximumPageState in the host. The tab fields have smaller bounds in their owning stores.
const maximumPageState = 512 * 1024;

/** The text a secondary window keeps of its page. Omit the layout if the combined state would not fit. */
export function writeWindowPage({ sessionTabs, fileTabs, layout }: WindowPage): string {
  const value = { version: 1, sessionTabs, fileTabs, layout };
  const text = JSON.stringify(value);
  return text.length <= maximumPageState ? text : JSON.stringify({ ...value, layout: null });
}

/**
 * Keeps what the host says of the windows, from what the page was given when it started (`windows.current`). It reads
 * the windows again each time the host says they changed, and passes the other requests to the shell.
 */
export function createWindowsHub(api: WindowsApi, initial: WindowsCurrentResponse | null) {
  const current = initial?.status === "ok" && initial.window ? initial.window : null;
  // The windows the host knew when the page started, with the ones that come back at this start: their tabs are theirs.
  const known = current ? initial?.windows?.length ? initial.windows : [current] : [];
  let state: WindowsState = { current, windows: known, restoreAtStartup: initial?.restoreAtStartup ?? true, canDrag: !!initial?.canDrag,
    sessionWindows: sessionWindowsOf(known, current?.id ?? null) };
  // The window that holds each tab, by the reference of the tab. Apart from `state`: the page never draws it, and the host
  // lists it again after each report of this window (a tab switch), which must not render the window again.
  let owners = ownersOf(known);
  // What `state` was made of, without what the page does not draw: the host lists the windows again after each report.
  let shown = shownKey(state.windows, state.restoreAtStartup, state.sessionWindows);
  const listeners = new Set<() => void>();
  let request: (value: WindowsRequest) => void = () => { };
  let reported = "";
  let reportTimer: ReturnType<typeof setTimeout> | null = null;
  let reporting = false;
  let pending: Readonly<{ tabs: readonly string[]; active: string | null; shown: readonly string[]; pageState: string | null }> | null = null;
  let listing = 0;
  const notify = () => { for (const listener of [...listeners]) listener(); };

  /**
   * What the page draws of the windows: not the tabs they hold (only `owners` reads them, and the sessions the Explorer
   * marks), nor which one is in front or minimized, that the host tells at each change of focus and no component reads.
   */
  function shownKey(windows: readonly WindowsWindow[], restoreAtStartup: boolean, sessionWindows: ReadonlyMap<string, string>) {
    return JSON.stringify([restoreAtStartup, [...sessionWindows], windows.map(({ tabs: _tabs, activeTab: _activeTab, focused: _focused, state: _state, ...window }) => window)]);
  }

  function sessionWindowsOf(windows: readonly WindowsWindow[], self: string | null): ReadonlyMap<string, string> {
    const held = new Map<string, string>();
    for (const window of windows) if (window.open && window.id !== self) for (const tab of window.tabs) {
      const id = sessionIdOfRef(tab);
      if (id) held.set(id, window.id);
    }
    return held;
  }

  function ownersOf(windows: readonly WindowsWindow[]): ReadonlyMap<string, string> {
    if (!windows.some(window => window.tabs.length)) return noOwners;
    const owners = new Map<string, string>();
    for (const window of windows) if (window.open) for (const tab of window.tabs) owners.set(tab, window.id);
    return owners;
  }

  async function refresh() {
    if (!current) return;
    const turn = ++listing;
    try {
      const reply = await api.list({}, { timeoutMilliseconds: 15_000 });
      if (turn !== listing || reply.status !== "ok") return;
      owners = ownersOf(reply.windows);
      const sessionWindows = sessionWindowsOf(reply.windows, current.id);
      const key = shownKey(reply.windows, reply.restoreAtStartup, sessionWindows);
      if (key === shown) return;
      shown = key;
      const self = reply.windows.find(window => window.id === current.id) ?? state.current;
      state = { ...state, current: self, windows: reply.windows, restoreAtStartup: reply.restoreAtStartup, sessionWindows };
      notify();
    } catch { /* The windows are read again when the host says they changed. */ }
  }

  function flushReport() {
    if (reportTimer !== null) clearTimeout(reportTimer);
    reportTimer = null;
    if (!pending || reporting) return;
    const value = pending, key = reported;
    pending = null;
    reporting = true;
    const failed = () => { if (reported === key) reported = ""; };
    void api.report({ tabs: [...value.tabs], active: value.active, shown: [...value.shown], pageState: value.pageState }, { timeoutMilliseconds: 15_000 })
      .then(reply => { if (reply.status !== "ok") failed(); }, failed).finally(() => {
        reporting = false;
        // Keep the host's snapshots in order even when a newer report was flushed before this one answered.
        if (reportTimer === null) flushReport();
      });
  }

  return {
    /** Whether this page is the one of a secondary window, which holds tabs only. Fixed for the life of the page. */
    secondary: !!current && !current.main,
    /** What the page of a secondary window kept of its tabs and panes when the window was last closed. */
    pageState: initial?.status === "ok" ? initial.pageState ?? null : null,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /** What the host asks of this window: the shell answers it. */
    onRequest(handler: (value: WindowsRequest) => void) { request = handler; },
    /** Listens to the host until the returned function is called. */
    connect(): () => void {
      if (!current) return () => { };
      const abort = new AbortController();
      void (async () => {
        while (!abort.signal.aborted) {
          try {
            void refresh();
            for await (const event of await api.watch({}, { signal: abort.signal })) {
              if (abort.signal.aborted) return;
              if (event.kind === "changed") void refresh();
              else request(event);
            }
          } catch { /* The host is gone a moment: listen again. */ }
          if (abort.signal.aborted) return;
          await new Promise(resolve => setTimeout(resolve, reconnectMilliseconds));
        }
      })();
      return () => abort.abort();
    },
    /** The window that holds a tab; null when none does. */
    ownerOf(ref: string): string | null { return owners.get(ref) ?? null; },
    /** Whether another window than this one holds a tab. */
    heldElsewhere(ref: string): boolean {
      const owner = owners.get(ref);
      return !!current && !!owner && owner !== current.id && state.windows.some(window => window.id === owner && window.open);
    },
    /**
     * Whether this window answers a request about a tab that reaches every window (a terminal to show, a canvas a plugin
     * asks for): the window that holds the tab, else the main one.
     */
    handles(ref: string): boolean {
      if (!current) return true;
      const owner = owners.get(ref);
      return owner ? owner === current.id || !state.windows.some(window => window.id === owner && window.open) : current.main;
    },
    /** A tab that moved into this window is its own from now on, before the host lists the windows again. */
    claim(ref: string) {
      if (!current || owners.get(ref) === current.id) return;
      owners = new Map(owners).set(ref, current.id);
    },
    /**
     * Says which tabs this window holds, the one in front (`active`), the ones in front of its panes (`shown`, what the
     * user looks at while the window has the focus), and what its page keeps of them; written a moment later, once.
     */
    report(tabs: readonly string[], active: string | null, shown: readonly string[], pageState: string | null) {
      if (!current) return;
      const key = JSON.stringify([tabs, active, shown, pageState]);
      if (key === reported) return;
      reported = key;
      pending = { tabs, active, shown, pageState };
      // The tabs this window holds are its own at once: a link that follows does not open them twice.
      const held = new Map(owners);
      for (const [tab, owner] of held) if (owner === current.id && !tabs.includes(tab)) held.delete(tab);
      for (const tab of tabs) held.set(tab, current.id);
      owners = held;
      if (reportTimer === null) reportTimer = setTimeout(flushReport, 200);
    },
    /** Sends a queued report without waiting for its timer. An in-flight report still finishes first. */
    flushReport,
    /** Moves a tab of this window to another window, or to a new one (no target); the page closes the tab on `ok`. */
    async moveTab(tab: string, payload: string, spaceBound: boolean, target: string | null, requestId: string | null = null): Promise<string> {
      try { return (await api.moveTab({ tab, payload, spaceBound, target, requestId }, { timeoutMilliseconds: 60_000 })).status; }
      catch { return "unavailable"; }
    },
    /** Says that a move the host asked for cannot be done. */
    refuseMove(requestId: string, status: string) { void api.refuseMove({ requestId, status }, { timeoutMilliseconds: 15_000 }).catch(() => { }); },
    /** Brings the window that holds a tab to the front, on that tab; `payload` is what it is asked to show there, as text the tab reads. */
    focusTab(tab: string, payload: string | null = null) { void api.focusTab({ tab, payload }, { timeoutMilliseconds: 15_000 }).catch(() => { }); },
    focus(windowId: string) { void api.focus({ windowId }, { timeoutMilliseconds: 15_000 }).catch(() => { }); },
    /** This secondary window closed its tabs: the window closes. */
    closeSelf() { void api.close({ windowId: null }, { timeoutMilliseconds: 15_000 }).catch(() => { }); },
    /** Asks another window to close: its page closes its tabs, then the window. */
    close(windowId: string) { void api.close({ windowId }, { timeoutMilliseconds: 15_000 }).catch(() => { }); },
    async open(request: Readonly<{ savedId?: string | null; name?: string | null; icon?: string | null; color?: string | null }>) {
      try { return await api.open({ savedId: request.savedId ?? null, name: request.name ?? null, icon: request.icon ?? null, color: request.color ?? null }, { timeoutMilliseconds: 60_000 }); }
      catch { return null; }
    },
    async setIdentity(windowId: string, change: Readonly<{ name?: string | null; icon?: string | null; color?: string | null; saved?: boolean | null; pinnedDisplayId?: string | null }>) {
      try {
        const reply = await api.setIdentity({ windowId, name: change.name ?? null, icon: change.icon ?? null, color: change.color ?? null, saved: change.saved ?? null,
          pinnedDisplayId: change.pinnedDisplayId ?? null }, { timeoutMilliseconds: 15_000 });
        if (reply.status === "ok") void refresh();
        return reply.status;
      } catch { return "unavailable"; }
    },
    async forget(windowId: string) {
      try { const reply = await api.forget({ windowId }, { timeoutMilliseconds: 15_000 }); void refresh(); return reply.status; }
      catch { return "unavailable"; }
    },
    async setRestoreAtStartup(restore: boolean) {
      state = { ...state, restoreAtStartup: restore };
      shown = shownKey(state.windows, restore, state.sessionWindows);
      notify();
      try { await api.setRestoreAtStartup({ restore }, { timeoutMilliseconds: 15_000 }); } catch { /* Read again with the windows. */ }
      void refresh();
    },
    async displays() {
      try { const reply = await api.displays({}, { timeoutMilliseconds: 15_000 }); return reply.status === "ok" ? reply.displays : []; }
      catch { return []; }
    },
    /** A tab of this window is dragged to a point of the page: the window under it, when another, is told. */
    async dragOver(tab: string, spaceBound: boolean, point: Readonly<{ x: number; y: number }> | null) {
      try {
        const reply = await api.dragOver({ tab, spaceBound, x: point ? Math.round(point.x) : null, y: point ? Math.round(point.y) : null }, { timeoutMilliseconds: 5_000 });
        return reply.windowId;
      } catch { return null; }
    },
    /** A tab dragged out of this window was released at a point of the page: it moves to the window there, or to a new one. */
    async drop(tab: string, payload: string, spaceBound: boolean, point: Readonly<{ x: number; y: number }>) {
      try { return (await api.drop({ tab, payload, spaceBound, x: Math.round(point.x), y: Math.round(point.y) }, { timeoutMilliseconds: 60_000 })).status; }
      catch { return "unavailable"; }
    },
    refresh,
  };
}
