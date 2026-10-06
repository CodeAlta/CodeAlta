/** A file open in a project's code editor. The preview tab is the one the next previewed file takes over. */
export type EditorFile = Readonly<{ path: string; preview: boolean }>;
export type EditorFiles = Readonly<{
  open: readonly EditorFile[];
  active: string | null;
  /** The open paths by how recently they were shown, most recent first. */
  recent: readonly string[];
}>;
export const editorFileLimit = 48;
export const emptyEditorFiles: EditorFiles = Object.freeze({ open: Object.freeze([]), active: null, recent: Object.freeze([]) });

/** Whether a path is a folder or something inside it. */
export const underPath = (path: string, folder: string) => path === folder || path.startsWith(`${folder}/`);
/** A path after the entry `from`, or a folder above it, became `to`. */
export const movedPath = (path: string, from: string, to: string) => path === from ? to : path.startsWith(`${from}/`) ? to + path.slice(from.length) : path;

const shown = (recent: readonly string[], path: string) => [path, ...recent.filter(value => value !== path)];

/**
 * Opens a file, or shows its tab when it is already open. A previewed file takes the place of the preview tab;
 * any other opens beside the tab that is shown. At the limit the file shown longest ago that `keep` does not
 * hold (unsaved edits) is closed; when every file is held the file is not opened.
 */
export function openEditorFile(state: EditorFiles, path: string, preview = false, keep: (path: string) => boolean = () => false): EditorFiles {
  const existing = state.open.find(file => file.path === path);
  if (existing) {
    const open = existing.preview && !preview ? state.open.map(file => file === existing ? { path, preview: false } : file) : state.open;
    return open === state.open && state.active === path ? state : { open, active: path, recent: shown(state.recent, path) };
  }
  const file: EditorFile = { path, preview };
  const open = [...state.open];
  const replaced = preview ? open.findIndex(value => value.preview && !keep(value.path)) : -1;
  if (replaced >= 0) open[replaced] = file;
  else open.splice(state.active === null ? open.length : open.findIndex(value => value.path === state.active) + 1, 0, file);
  if (open.length > editorFileLimit) {
    const age = (value: EditorFile) => { const index = state.recent.indexOf(value.path); return index < 0 ? Number.MAX_SAFE_INTEGER : index; };
    const evicted = open.filter(value => value.path !== path && !keep(value.path)).sort((left, right) => age(right) - age(left))[0];
    if (!evicted) return state;
    open.splice(open.indexOf(evicted), 1);
  }
  return { open, active: path, recent: shown(state.recent, path).filter(value => open.some(entry => entry.path === value)) };
}

/** Keeps a previewed file open: its tab is no longer the one the next preview takes. */
export function pinEditorFile(state: EditorFiles, path: string): EditorFiles {
  return state.open.some(file => file.path === path && file.preview)
    ? { ...state, open: state.open.map(file => file.path === path ? { path, preview: false } : file) } : state;
}

export function activateEditorFile(state: EditorFiles, path: string): EditorFiles {
  return state.active === path || !state.open.some(file => file.path === path) ? state : { ...state, active: path, recent: shown(state.recent, path) };
}

/** Closes files. When the one shown closes, the file shown before it takes its place, or else its neighbor. */
export function closeEditorFiles(state: EditorFiles, paths: Iterable<string>): EditorFiles {
  const closing = new Set(paths);
  const open = state.open.filter(file => !closing.has(file.path));
  if (open.length === state.open.length) return state;
  const recent = state.recent.filter(path => !closing.has(path));
  let active = state.active;
  if (active !== null && closing.has(active)) {
    const at = state.open.findIndex(file => file.path === active);
    active = recent.find(path => open.some(file => file.path === path)) ?? state.open.slice(at).find(file => !closing.has(file.path))?.path ?? open.at(-1)?.path ?? null;
  }
  return { open, active, recent: active === null ? recent : shown(recent, active) };
}

export const closeEditorFile = (state: EditorFiles, path: string) => closeEditorFiles(state, [path]);

/** Moves a tab to another position of the strip. */
export function moveEditorFile(state: EditorFiles, path: string, index: number): EditorFiles {
  const from = state.open.findIndex(file => file.path === path);
  const to = Math.max(0, Math.min(state.open.length - 1, index));
  if (from < 0 || from === to) return state;
  const open = [...state.open];
  open.splice(to, 0, ...open.splice(from, 1));
  return { ...state, open };
}

/** The file after or before the one shown, around the strip; null when nothing else is open. */
export function cycleEditorFile(state: EditorFiles, delta: 1 | -1): string | null {
  if (state.open.length < 2) return null;
  const at = state.open.findIndex(file => file.path === state.active);
  return state.open[(at + delta + state.open.length) % state.open.length].path;
}

/** Follows a file or a folder that was renamed or moved: the open files keep their tabs under their new paths. */
export function renameEditorPath(state: EditorFiles, from: string, to: string): EditorFiles {
  if (!state.open.some(file => underPath(file.path, from))) return state;
  const seen = new Set<string>();
  const open = state.open.map(file => ({ ...file, path: movedPath(file.path, from, to) })).filter(file => !seen.has(file.path) && !!seen.add(file.path));
  return { open, active: state.active === null ? null : movedPath(state.active, from, to),
    recent: [...new Set(state.recent.map(path => movedPath(path, from, to)))] };
}

/** Closes what was open of a deleted file or folder. */
export function removeEditorPath(state: EditorFiles, path: string): EditorFiles {
  return closeEditorFiles(state, state.open.filter(file => underPath(file.path, path)).map(file => file.path));
}

/** The names shown on the tabs: a name that several open files share is followed by its folder. */
export function editorTabNames(files: readonly EditorFile[]): ReadonlyMap<string, Readonly<{ name: string; folder: string | null }>> {
  const base = (path: string) => path.slice(path.lastIndexOf("/") + 1);
  const count = new Map<string, number>();
  for (const file of files) count.set(base(file.path), (count.get(base(file.path)) ?? 0) + 1);
  return new Map(files.map(file => {
    const name = base(file.path), parent = file.path.slice(0, Math.max(0, file.path.length - name.length - 1));
    return [file.path, { name, folder: count.get(name)! > 1 ? parent || "." : null }];
  }));
}

/** The kinds of text file that also have a rendering. */
export const previewKinds = ["svg", "markdown"] as const;
export type EditorPreferences = Readonly<{
  /** The width of the files and search side, in CSS pixels. */
  width: number;
  wrap: boolean; minimap: boolean;
  /** Show what git ignores in the files, dimmed. */
  ignored: boolean;
  /** The kinds of file that open as their rendering instead of their text: the view last chosen for each kind. */
  previews: readonly (typeof previewKinds[number])[];
}>;
export type EditorSide = "files" | "search";
/** What a project's editor is restored with. */
export type StoredEditor = Readonly<{ files: readonly string[]; active: string | null; side: EditorSide | null; expanded: readonly string[] }>;
export type EditorStorage = Readonly<{ preferences: EditorPreferences; projects: Readonly<Record<string, StoredEditor>> }>;

export const editorStorageKey = "codealta.desktop.editor.v1";
export const defaultEditorPreferences: EditorPreferences = Object.freeze({ width: 264, wrap: true, minimap: false, ignored: false, previews: Object.freeze(["svg"] as const) });
export const emptyStoredEditor: StoredEditor = Object.freeze({ files: Object.freeze([]), active: null, side: "files", expanded: Object.freeze([]) });
export const editorSideWidth = (value: number) => Math.round(Math.max(170, Math.min(640, value)));
const storedProjects = 24, storedExpanded = 256, storedLength = 262144;
const path = (value: unknown): value is string => typeof value === "string" && value.length > 0 && value.length <= 1024;

function storedEditorOf(value: unknown): StoredEditor | null {
  if (!value || typeof value !== "object") return null;
  const data = value as { files?: unknown; active?: unknown; side?: unknown; expanded?: unknown };
  if (!Array.isArray(data.files) || !Array.isArray(data.expanded) || !data.files.every(path) || !data.expanded.every(path)) return null;
  const files = [...new Set(data.files)].slice(0, editorFileLimit);
  return { files, active: path(data.active) && files.includes(data.active) ? data.active : files[0] ?? null,
    side: data.side === "files" || data.side === "search" ? data.side : null, expanded: [...new Set(data.expanded)].slice(0, storedExpanded) };
}

/** Reads what the editors remember; anything malformed restores the defaults. */
export function restoreEditorStorage(read: () => string | null): EditorStorage {
  const none: EditorStorage = { preferences: defaultEditorPreferences, projects: {} };
  try {
    const raw = read();
    if (!raw || raw.length > storedLength) return none;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object" || (value as { version?: unknown }).version !== 1) return none;
    const data = value as { preferences?: Record<string, unknown>; projects?: unknown };
    const stored = data.preferences && typeof data.preferences === "object" ? data.preferences : {};
    const flag = (name: "wrap" | "minimap" | "ignored") => typeof stored[name] === "boolean" ? stored[name] as boolean : defaultEditorPreferences[name];
    const preferences: EditorPreferences = { width: typeof stored.width === "number" && Number.isFinite(stored.width) ? editorSideWidth(stored.width) : defaultEditorPreferences.width,
      wrap: flag("wrap"), minimap: flag("minimap"), ignored: flag("ignored"),
      previews: Array.isArray(stored.previews) ? previewKinds.filter(kind => (stored.previews as unknown[]).includes(kind)) : defaultEditorPreferences.previews };
    const projects: Record<string, StoredEditor> = {};
    if (data.projects && typeof data.projects === "object" && !Array.isArray(data.projects))
      for (const [id, entry] of Object.entries(data.projects).slice(-storedProjects)) {
        const editor = id.length > 0 && id.length <= 256 ? storedEditorOf(entry) : null;
        if (editor) projects[id] = editor;
      }
    return { preferences, projects };
  } catch { return none; }
}

export const storedEditor = (storage: EditorStorage, projectId: string): StoredEditor => storage.projects[projectId] ?? emptyStoredEditor;

/**
 * Writes one project's editor, or the preferences, over what is stored now: several editors share the storage,
 * so each change is applied to what the others last wrote.
 */
export function updateEditorStorage(read: () => string | null, write: (value: string) => void,
  change: Readonly<{ projectId: string; editor: StoredEditor } | { preferences: EditorPreferences }>): boolean {
  try {
    const current = restoreEditorStorage(read);
    let next: EditorStorage;
    if ("preferences" in change) next = { ...current, preferences: { ...change.preferences, width: editorSideWidth(change.preferences.width) } };
    else {
      // Written last, so that the projects edited most recently are the ones kept.
      const { [change.projectId]: _replaced, ...others } = current.projects;
      const editor: StoredEditor = { ...change.editor, files: change.editor.files.slice(0, editorFileLimit), expanded: change.editor.expanded.slice(0, storedExpanded) };
      next = { ...current, projects: Object.fromEntries([...Object.entries(others), [change.projectId, editor] as const].slice(-storedProjects)) };
    }
    const value = JSON.stringify({ version: 1, ...next });
    if (value.length > storedLength) return false;
    write(value);
    return true;
  } catch { return false; }
}

/**
 * Gives the editors of projects the files that were stored as tabs of their own, before an editor had tabs:
 * each opens its files, without the files of the project beside them. An editor that already has files keeps them.
 */
export function adoptLegacyFiles(read: () => string | null, write: (value: string) => void, files: ReadonlyMap<string, readonly string[]>): void {
  if (!files.size) return;
  const storage = restoreEditorStorage(read);
  for (const [projectId, paths] of files) {
    if (storage.projects[projectId]?.files.length) continue;
    updateEditorStorage(read, write, { projectId, editor: { ...storedEditor(storage, projectId), files: [...paths], active: paths.at(-1) ?? null, side: null } });
  }
}
