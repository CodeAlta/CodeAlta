// The terminals of the page: the link to the host, and the terminal of each tab that shows one.
import { terminals as service } from "#neoastra";
import { shellColor, subscribeAppearance } from "../shellColors";
import { createTerminalHub, type TerminalsApi } from "./terminalHub";
import { defaultTerminalLook, type TerminalLook } from "./terminalLook";
import { canShowPictures, loadTerminalFont, TerminalSurface, type TerminalMatches, type TerminalProgress } from "./TerminalSurface";
import { terminalTheme, type TerminalKeyAction } from "./terminals";

/** What a panel is told by the terminal it shows. */
export type TerminalPanelEvents = Readonly<{ action(action: TerminalKeyAction): void; matches(matches: TerminalMatches): void }>;
export type TerminalWorkspace = ReturnType<typeof createTerminalWorkspace>;
type Shown = { surface: TerminalSurface; users: number; disposal: number; events: TerminalPanelEvents | null };

/** The colors of the terminals, read from the window as it is now. */
export function readTerminalTheme() {
  const root = document.documentElement;
  const probe = root.appendChild(document.createElement("span"));
  try { return terminalTheme(property => shellColor(probe, property), root.dataset.theme !== "light"); }
  finally { probe.remove(); }
}

/**
 * Owns what the page shows of the terminals. A terminal on the page lasts while a tab shows it: the tab can
 * be hidden behind another one or moved to another pane, and the terminal is the same.
 * @param open Opens an address in the browser of the system.
 */
export function createTerminalWorkspace(open: (address: string) => void, api: TerminalsApi = service) {
  const hub = createTerminalHub(api);
  const shown = new Map<string, Shown>();
  const progress = new Map<string, TerminalProgress>();
  const listeners = new Set<() => void>();
  let look: TerminalLook = defaultTerminalLook;
  let unfollow: (() => void) | null = null;
  let fonts: Promise<void> | null = null;
  let pictures: boolean | null = null;
  const notify = () => { for (const listener of [...listeners]) listener(); };

  function create(id: string, entry: () => Shown | undefined): TerminalSurface {
    const system = hub.system();
    return new TerminalSurface({
      platform: system?.platform ?? (navigator.platform.startsWith("Win") ? "windows" : navigator.platform.startsWith("Mac") ? "macos" : "linux"),
      build: system?.build ?? 0, look, theme: readTerminalTheme(), pictures: pictures ??= canShowPictures(),
      input: data => hub.input(id, data),
      resized: (columns, rows) => hub.resize(id, columns, rows),
      link: open,
      action: action => entry()?.events?.action(action),
      matches: matches => entry()?.events?.matches(matches),
      progress: value => {
        if (value.state === 0) progress.delete(id); else progress.set(id, value);
        notify();
      },
      copy: text => void navigator.clipboard?.writeText(text).catch(() => { /* The clipboard is not available: nothing is copied. */ }),
    });
  }

  return {
    hub,
    /** Resolves once the font of the terminals is ready: a terminal is opened after that. */
    fonts: () => fonts ??= loadTerminalFont(),
    /** The terminal of a tab, created and linked to the host the first time. Each call is undone by `release`. */
    acquire(id: string): TerminalSurface {
      let entry = shown.get(id);
      if (!entry) {
        entry = { surface: undefined as unknown as TerminalSurface, users: 0, disposal: 0, events: null };
        entry.surface = create(id, () => shown.get(id));
        shown.set(id, entry);
        hub.attach(id, entry.surface);
        unfollow ??= subscribeAppearance(() => { const theme = readTerminalTheme(); for (const value of shown.values()) value.surface.setTheme(theme); });
      }
      window.clearTimeout(entry.disposal);
      entry.users++;
      return entry.surface;
    },
    /** A tab no longer shows the terminal. It is taken off the page once no tab does: its program keeps running. */
    release(id: string) {
      const entry = shown.get(id);
      if (!entry || --entry.users > 0) return;
      // A tab that is drawn again right away (the page in development does it) keeps its terminal.
      entry.disposal = window.setTimeout(() => {
        if (entry.users > 0 || shown.get(id) !== entry) return;
        shown.delete(id);
        progress.delete(id);
        hub.detach(id);
        entry.surface.dispose();
        if (shown.size === 0) { unfollow?.(); unfollow = null; }
        notify();
      }, 0);
    },
    /** The terminal a tab shows, or nothing. */
    surface: (id: string) => shown.get(id)?.surface,
    /** What a panel wants to be told by its terminal; null when it stops listening. */
    listen(id: string, events: TerminalPanelEvents | null) {
      const entry = shown.get(id);
      if (entry) entry.events = events;
    },
    /** How far the command of a terminal is, when its program says it. */
    progress: (id: string) => progress.get(id),
    /** Calls `listener` when the progress of a terminal changes. */
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    look: () => look,
    setLook(value: TerminalLook) {
      look = value;
      for (const entry of shown.values()) entry.surface.setLook(value);
    },
  };
}
