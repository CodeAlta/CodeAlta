// The editor of the machine that folders and files are opened in beside the code editor of CodeAlta, and the file
// manager of the system. The window has one opener: it asks the host for the editor the first time something shows
// an action that names it, and again when the user chooses another one. The page names a project, a session or a
// worktree, never a program to run: the host finds the folder and starts the editor.
import { createContext, useContext, useEffect, useSyncExternalStore } from "react";
import type { ExternalEditorsResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { BrandIcon, type BrandIconName } from "../BrandIcon";
import type { MessageKey } from "../localization";

/** Where something is opened: the external editor, or the file manager of the system. */
export type ExternalTarget = "editor" | "explorer";

/** The editor folders are opened in. */
export interface ExternalEditorInfo {
  readonly id: string;
  readonly name: string;
}

/** What the window opens outside CodeAlta, as it is known. */
export interface ExternalState {
  /** The editor in use; null when none was found or chosen, undefined until the host said. */
  readonly editor: ExternalEditorInfo | null | undefined;
  /** `windows`, `macos` or `linux`, which names the file manager; null until the host said. */
  readonly platform: string | null;
}

/** The calls to the host, and what the window says when one fails. */
export interface ExternalOpenerApi {
  list(refresh: boolean): Promise<ExternalEditorsResponse>;
  /** Opens the folder of a project, or the one a session works in. */
  openFolder(target: ExternalTarget, projectId: string, sessionId: string | null): Promise<string>;
  /** Opens an entry of a project or of the folder of a code editor ("" for the folder itself), a file at a line. */
  openEntry(target: ExternalTarget, projectId: string, path: string, line: number | null): Promise<string>;
  /** Opens a checkout of the repository of a project, as the inventory of the worktrees names it. */
  openWorktree(target: ExternalTarget, projectId: string, path: string): Promise<string>;
  /** Tells the user why something was not opened. */
  failed(message: MessageKey): void;
}

const unknown: ExternalState = { editor: undefined, platform: null };

/** The opener of a window. */
export function createExternalOpener(api: ExternalOpenerApi) {
  let state = unknown;
  let loading: Promise<void> | null = null;
  const listeners = new Set<() => void>();
  function publish(next: ExternalState) {
    if (next.editor?.id === state.editor?.id && next.editor?.name === state.editor?.name && next.platform === state.platform && (next.editor === null) === (state.editor === null)) return;
    state = next;
    for (const listener of [...listeners]) listener();
  }
  function accept(reply: ExternalEditorsResponse) {
    if (reply.status !== "ok" && reply.status !== "invalid") { publish({ editor: null, platform: null }); return; }
    publish({ editor: reply.current ? { id: reply.current.id, name: reply.current.name } : null, platform: reply.platform });
  }
  function load(refresh: boolean) {
    const next = api.list(refresh).then(accept, () => { if (state.editor === undefined) publish({ editor: null, platform: null }); });
    loading = next;
    return next;
  }
  async function run(target: ExternalTarget, call: () => Promise<string>) {
    let status: string;
    try { status = await call(); }
    catch { status = "failed"; }
    if (status === "ok") return;
    api.failed(failure(target, status));
    // The editor may have been removed, or another one chosen elsewhere.
    if (status === "no_editor") void load(true);
  }
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    state: () => state,
    /** Asks the host the first time it is needed. */
    ensure() { if (!loading) void load(false); },
    /** Asks the host again: the user chose another editor, or one was installed. */
    reload: () => load(false),
    /** What the host answered about the editors, from a page that changed them. */
    accept,
    openFolder: (target: ExternalTarget, projectId: string, sessionId: string | null = null) => run(target, () => api.openFolder(target, projectId, sessionId)),
    openEntry: (target: ExternalTarget, projectId: string, path: string, line: number | null = null) => run(target, () => api.openEntry(target, projectId, path, line)),
    openWorktree: (target: ExternalTarget, projectId: string, path: string) => run(target, () => api.openWorktree(target, projectId, path)),
  };
}

export type ExternalOpener = ReturnType<typeof createExternalOpener>;

/** What the window says when something was not opened. */
export function failure(target: ExternalTarget, status: string): MessageKey {
  if (target === "explorer") return status === "failed" ? "The file manager could not be opened." : "The folder could not be opened.";
  switch (status) {
    case "no_editor": return "No external editor was found. Choose one in Settings > General.";
    case "refused": return "The custom editor cannot be started: choose a program that exists and is not a batch file.";
    case "failed": return "The external editor could not be started.";
    default: return "The folder could not be opened.";
  }
}

/** The opener of the window; null where the window opens nothing outside (a fixture, a window without a host). */
export const ExternalOpenerContext = createContext<ExternalOpener | null>(null);

const noSubscription = () => () => { };

/**
 * The opener of the window and what it knows, followed. The window asks the host for its editor once, the first time
 * something that is shown wants it: a menu that opens, an action on the screen.
 * @param wanted Whether what calls it shows its actions now; a closed menu does not make the window ask.
 */
export function useExternalOpener(wanted = true): Readonly<{ opener: ExternalOpener | null } & ExternalState> {
  const opener = useContext(ExternalOpenerContext);
  const state = useSyncExternalStore(opener?.subscribe ?? noSubscription, opener?.state ?? (() => unknown), opener?.state ?? (() => unknown));
  useEffect(() => { if (wanted) opener?.ensure(); }, [opener, wanted]);
  return { opener, ...state };
}

const brands: Readonly<Record<string, BrandIconName>> = {
  vscode: "vscode", vscodeinsiders: "vscodeinsiders", cursor: "cursor", windsurf: "windsurf", zed: "zedindustries", sublimetext: "sublimetext",
};

/** The logo of an editor; the icon of code for one CodeAlta has no logo of (a custom editor). */
export function ExternalEditorIcon({ id, size = 14 }: { id: string; size?: number }) {
  const brand = brands[id];
  return brand ? <BrandIcon name={brand} size={size} /> : <AppIcon name="code" size={size} />;
}

/** "Open in {editor}", or with the name of what is opened. */
export function openInEditorKey(named: boolean): MessageKey {
  return named ? "Open {name} in {editor}" : "Open in {editor}";
}

/** "Open in File Explorer", "Open in Finder" or "Open in the file manager", with the name of what is opened or not. */
export function openInFileManagerKey(platform: string | null, named = false): MessageKey {
  if (platform === "windows") return named ? "Open {name} in File Explorer" : "Open in File Explorer";
  if (platform === "macos") return named ? "Open {name} in Finder" : "Open in Finder";
  return named ? "Open {name} in the file manager" : "Open in the file manager";
}
