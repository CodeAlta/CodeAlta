import type { ProjectFileFolder } from "#neoastra";
import type { MessageKey } from "../localization";
import { movedPath, underPath } from "./editorWorkbench";

export type TreeEntry = Readonly<{ name: string; directory: boolean; ignored: boolean }>;
export type TreeFolder = Readonly<{
  /** The name of these entries for the host: a folder that still has them is not sent again. */
  revision: string | null;
  entries: readonly TreeEntry[];
  truncated: boolean;
  /** Why the folder could not be listed; null once it was. */
  failure: string | null;
}>;
/**
 * The files of a project as far as they were looked at: the entries of the folders read so far, by
 * project-relative path ("" is the project folder), and the folders shown open. A folder is read when it is opened.
 */
export type FileTree = Readonly<{ folders: ReadonlyMap<string, TreeFolder>; expanded: ReadonlySet<string> }>;
export const emptyFileTree: FileTree = Object.freeze({ folders: new Map(), expanded: new Set<string>() });
/** Folders one request lists. */
export const treeQueryLimit = 256;

export const joinTreePath = (folder: string, name: string) => folder ? `${folder}/${name}` : name;
export const parentTreePath = (path: string) => path.slice(0, Math.max(0, path.lastIndexOf("/")));
export const treeBaseName = (path: string) => path.slice(path.lastIndexOf("/") + 1);
/** The folders above a path, outermost first: "a/b/c.ts" has "a" and "a/b". */
export function treeAncestors(path: string): string[] {
  const parts = path.split("/"), result: string[] = [];
  for (let index = 1; index < parts.length; index++) result.push(parts.slice(0, index).join("/"));
  return result;
}

const visible = (tree: FileTree, path: string) => treeAncestors(path).every(folder => tree.expanded.has(folder));

/** The folders to list: the project folder and every folder that is open down from it, with what is known of each. */
export function treeQueries(tree: FileTree): { path: string; knownRevision: string | null }[] {
  const paths = ["", ...[...tree.expanded].filter(path => path !== "" && visible(tree, path)).sort((left, right) => left.split("/").length - right.split("/").length || (left < right ? -1 : 1))];
  return paths.slice(0, treeQueryLimit).map(path => ({ path, knownRevision: tree.folders.get(path)?.revision ?? null }));
}

/**
 * Applies the host's answers. A folder that is gone closes with what it held, and a folder whose parent no
 * longer lists it is forgotten. The same tree is returned when nothing changed.
 */
export function applyTreeListing(tree: FileTree, answers: readonly ProjectFileFolder[]): FileTree {
  let folders: Map<string, TreeFolder> | null = null, expanded: Set<string> | null = null;
  const forget = (path: string) => {
    for (const key of [...(folders ?? tree.folders).keys()]) if (key !== "" && underPath(key, path)) (folders ??= new Map(tree.folders)).delete(key);
    for (const key of [...(expanded ?? tree.expanded)]) if (underPath(key, path)) (expanded ??= new Set(tree.expanded)).delete(key);
  };
  for (const answer of answers) {
    const current = (folders ?? tree.folders).get(answer.path);
    if (answer.status === "ok") {
      (folders ??= new Map(tree.folders)).set(answer.path, { revision: answer.revision, entries: answer.entries, truncated: answer.truncated, failure: null });
    } else if (answer.status === "unchanged") {
      if (current?.failure) (folders ??= new Map(tree.folders)).set(answer.path, { ...current, failure: null });
    } else if (answer.status === "not_found" && answer.path !== "") {
      forget(answer.path);
    } else if (current?.failure !== answer.status) {
      (folders ??= new Map(tree.folders)).set(answer.path, { revision: null, entries: current?.entries ?? [], truncated: current?.truncated ?? false, failure: answer.status });
    }
  }
  // What a listed parent no longer names was deleted, renamed or ignored.
  for (const path of [...(folders ?? tree.folders).keys()].concat([...(expanded ?? tree.expanded)])) {
    if (path === "") continue;
    const parent = (folders ?? tree.folders).get(parentTreePath(path));
    if (parent && !parent.failure && !parent.entries.some(entry => entry.directory && entry.name === treeBaseName(path))) forget(path);
  }
  return folders || expanded ? { folders: folders ?? tree.folders, expanded: expanded ?? tree.expanded } : tree;
}

export function toggleTreeFolder(tree: FileTree, path: string): FileTree {
  const expanded = new Set(tree.expanded);
  if (!expanded.delete(path)) expanded.add(path);
  return { ...tree, expanded };
}

/** Opens folders (the ones above a file to show, a folder that receives a new entry). */
export function expandTreeFolders(tree: FileTree, paths: readonly string[]): FileTree {
  const missing = paths.filter(path => path !== "" && !tree.expanded.has(path));
  return missing.length ? { ...tree, expanded: new Set([...tree.expanded, ...missing]) } : tree;
}

export const collapseTree = (tree: FileTree): FileTree => tree.expanded.size ? { ...tree, expanded: new Set() } : tree;

/** Follows a renamed or moved entry: what was read and open under it stays so under its new path. */
export function renameTreePath(tree: FileTree, from: string, to: string): FileTree {
  const folders = new Map([...tree.folders].map(([path, folder]) => [path === "" ? path : movedPath(path, from, to), folder]));
  return { folders, expanded: new Set([...tree.expanded].map(path => movedPath(path, from, to))) };
}

/** Forgets a deleted entry and what was read under it. */
export function removeTreePath(tree: FileTree, path: string): FileTree {
  return { folders: new Map([...tree.folders].filter(([key]) => key === "" || !underPath(key, path))),
    expanded: new Set([...tree.expanded].filter(key => !underPath(key, path))) };
}

/** A name being typed in the tree: for a new file or folder of `parent`, or for the entry at `path`. */
export type TreeEdit = Readonly<{ parent: string; directory: boolean; path: string | null }>;
export type TreeRow = Readonly<
  | { kind: "entry"; key: string; path: string; name: string; depth: number; directory: boolean; expanded: boolean; ignored: boolean; loading: boolean }
  | { kind: "input"; key: string; depth: number; directory: boolean; path: string | null; parent: string }
  | { kind: "note"; key: string; depth: number; note: "truncated" | "failed" }>;

/** The rows shown, top to bottom: each open folder is followed by its entries, one level deeper. */
export function treeRows(tree: FileTree, edit: TreeEdit | null = null): TreeRow[] {
  const rows: TreeRow[] = [];
  const walk = (folder: string, depth: number, ignored: boolean) => {
    const state = tree.folders.get(folder);
    if (edit && edit.path === null && edit.parent === folder) rows.push({ kind: "input", key: `new:${folder}`, depth, directory: edit.directory, path: null, parent: folder });
    if (!state) return;
    for (const entry of state.entries) {
      const path = joinTreePath(folder, entry.name);
      if (edit?.path === path) { rows.push({ kind: "input", key: `rename:${path}`, depth, directory: entry.directory, path, parent: folder }); continue; }
      const expanded = entry.directory && tree.expanded.has(path);
      rows.push({ kind: "entry", key: path, path, name: entry.name, depth, directory: entry.directory, expanded, ignored: ignored || entry.ignored,
        loading: expanded && !tree.folders.has(path) });
      if (expanded) walk(path, depth + 1, ignored || entry.ignored);
    }
    if (state.failure && !state.entries.length) rows.push({ kind: "note", key: `failed:${folder}`, depth, note: "failed" });
    if (state.truncated) rows.push({ kind: "note", key: `more:${folder}`, depth, note: "truncated" });
  };
  walk("", 0, false);
  return rows;
}

/** The entry rows only, for moving through them with the keyboard. */
export const treeEntryRows = (rows: readonly TreeRow[]) => rows.filter((row): row is Extract<TreeRow, { kind: "entry" }> => row.kind === "entry");

/** The next row after `from` whose name starts with what was typed, around the tree; null when none does. */
export function treeTypeAhead(rows: readonly Extract<TreeRow, { kind: "entry" }>[], from: string | null, typed: string): string | null {
  const text = typed.toLowerCase();
  if (!text || !rows.length) return null;
  const start = rows.findIndex(row => row.path === from);
  // A longer text keeps the row it is already on; one letter moves to the next match.
  for (let step = text.length > 1 ? 0 : 1; step <= rows.length; step++) {
    const row = rows[(Math.max(start, 0) + step) % rows.length];
    if (row.name.toLowerCase().startsWith(text)) return row.path;
  }
  return null;
}

/**
 * Why a typed name cannot be used, or null. A new entry may name folders above it ("a/b/c.ts"); a renamed one
 * keeps its folder.
 */
export function treeNameProblem(typed: string, siblings: readonly TreeEntry[], current: string | null, nested: boolean): MessageKey | null {
  const name = typed.replace(/\\/gu, "/");
  if (!name.trim()) return "A name is required.";
  const parts = name.split("/");
  if (parts.length > 1 && !nested) return "A name cannot contain a slash.";
  if (name.length > 512 || parts.some(part => part === "" || part === "." || part === ".." || part.length > 255 || /[\u0000-\u001f:"<>|*?]/u.test(part)))
    return "This name is not valid.";
  if (parts.some(part => part !== part.trim() || part.endsWith("."))) return "A name cannot start or end with a space, or end with a dot.";
  const first = parts[0].toLowerCase();
  if (siblings.some(entry => entry.name.toLowerCase() === first && entry.name !== current && (parts.length === 1 || !entry.directory)))
    return "A file or folder with this name already exists.";
  return null;
}

export type TreeDecorations = Readonly<{ files: ReadonlyMap<string, string>; folders: ReadonlySet<string> }>;
export const noTreeDecorations: TreeDecorations = Object.freeze({ files: new Map(), folders: new Set<string>() });

/**
 * The git status of the files of a project, by project-relative path, and the folders that hold a changed file.
 * `prefix` is the project folder inside the work tree: a change outside it is not one of the project's files.
 */
export function treeDecorations(changes: readonly Readonly<{ path: string; status: string }>[], prefix: string): TreeDecorations {
  const files = new Map<string, string>(), folders = new Set<string>();
  const lead = prefix ? `${prefix.replace(/\/+$/u, "")}/` : "";
  for (const change of changes) {
    if (change.status === "deleted" || !change.path.startsWith(lead)) continue;
    const path = change.path.slice(lead.length);
    if (!path) continue;
    files.set(path, change.status);
    for (const folder of treeAncestors(path)) folders.add(folder);
  }
  return { files, folders };
}
