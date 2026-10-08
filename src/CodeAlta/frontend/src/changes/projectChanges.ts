/** How a file of the work tree differs from the commit it is compared with. */
export type ChangeStatus = "modified" | "added" | "deleted" | "renamed" | "copied" | "conflicted" | "untracked";
export type ChangedFile = Readonly<{ path: string; originalPath: string | null; status: ChangeStatus;
  insertions: number | null; deletions: number | null; binary: boolean; revision: string }>;
/** What is compared: the work tree with the last commit or with the base of the branch, or a commit with its parent. */
export type ChangeComparison = "head" | "branch" | "commit";
/** What a changes tab shows: one of the comparisons, and the commit when it is one. */
export type ChangeScope = Readonly<{ kind: "head" } | { kind: "branch" } | { kind: "commit"; id: string }>;
export const changeScopeKey = (scope: ChangeScope) => scope.kind === "commit" ? `commit:${scope.id}` : scope.kind;
/** The changed files of a project's repository, as `projectGit.changes` lists them. */
export type ChangeList = Readonly<{ revision: string; root: string; prefix: string; branch: string; detached: boolean;
  comparison: ChangeComparison; baseReference: string | null; baseAhead: number | null; files: readonly ChangedFile[];
  insertions: number; deletions: number; truncated: boolean }>;
export type ChangeListReply = Readonly<{ kind: "list"; list: ChangeList } | { kind: "unchanged" } | { kind: "failed"; status: string }>;

const statuses: readonly ChangeStatus[] = ["modified", "added", "deleted", "renamed", "copied", "conflicted", "untracked"];
const count = (value: unknown): value is number => Number.isInteger(value) && (value as number) >= 0;
const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit;
// A path of the list: relative, with forward slashes, never leaving the repository.
const relative = (value: unknown): value is string => text(value, 1024) && !/[\0-\x1f]/.test(value) && !value.startsWith("/")
  && value.split("/").every(segment => segment !== "" && segment !== "." && segment !== "..");

/** Accepts a well-formed `projectGit.changes` answer for the asked project; anything else is a failed read. */
export function changeListReply(reply: unknown, projectId: string, known: string | null): ChangeListReply {
  if (!reply || typeof reply !== "object") return { kind: "failed", status: "read_failed" };
  const value = reply as Record<string, unknown>;
  if (typeof value.status !== "string" || value.status.length > 64) return { kind: "failed", status: "read_failed" };
  if (value.status === "unchanged") return value.projectId === projectId && known !== null && value.revision === known
    ? { kind: "unchanged" } : { kind: "failed", status: "read_failed" };
  if (value.status !== "ok") return { kind: "failed", status: /^[a-z_]+$/.test(value.status) ? value.status : "read_failed" };
  if (value.projectId !== projectId || !text(value.revision, 64) || !text(value.root, 4096) || typeof value.prefix !== "string"
    || value.prefix.length > 1024 || !text(value.branch, 256) || typeof value.detached !== "boolean"
    || (value.comparison !== "head" && value.comparison !== "branch" && value.comparison !== "commit") || !Array.isArray(value.files) || value.files.length > 4096
    || !count(value.insertions) || !count(value.deletions) || typeof value.truncated !== "boolean") return { kind: "failed", status: "read_failed" };
  const files: ChangedFile[] = [];
  const seen = new Set<string>();
  for (const item of value.files as unknown[]) {
    const file = item as Record<string, unknown> | null;
    if (!file || typeof file !== "object" || !relative(file.path) || seen.has(file.path) || !statuses.includes(file.status as ChangeStatus)
      || (file.originalPath !== null && !relative(file.originalPath)) || !text(file.revision, 64) || typeof file.binary !== "boolean"
      || (file.insertions !== null && !count(file.insertions)) || (file.deletions !== null && !count(file.deletions))) return { kind: "failed", status: "read_failed" };
    seen.add(file.path);
    files.push({ path: file.path, originalPath: file.originalPath as string | null, status: file.status as ChangeStatus,
      insertions: file.insertions as number | null, deletions: file.deletions as number | null, binary: file.binary, revision: file.revision });
  }
  const base = text(value.baseReference, 256) && count(value.baseAhead) ? { baseReference: value.baseReference, baseAhead: value.baseAhead } : { baseReference: null, baseAhead: null };
  return { kind: "list", list: { revision: value.revision, root: value.root, prefix: value.prefix, branch: value.branch, detached: value.detached,
    comparison: value.comparison, ...base, files, insertions: value.insertions, deletions: value.deletions, truncated: value.truncated } };
}

/** One commit of the history of the current branch. */
export type ChangeCommit = Readonly<{ id: string; shortId: string; author: string; time: string; subject: string }>;
export type ChangeCommits = Readonly<{ revision: string; commits: readonly ChangeCommit[]; more: boolean }>;
export const commitPageSize = 20;
export const commitLimitMaximum = 200;

/** Accepts a well-formed `projectGit.commits` answer for the asked project: the list, `"unchanged"`, or null for anything else. */
export function changeCommitsReply(reply: unknown, projectId: string, known: string | null): ChangeCommits | "unchanged" | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.projectId !== projectId || !text(value.revision, 64)) return null;
  if (value.status === "unchanged") return known !== null && value.revision === known ? "unchanged" : null;
  if (value.status !== "ok" || !Array.isArray(value.commits) || value.commits.length > commitLimitMaximum || typeof value.more !== "boolean") return null;
  const commits: ChangeCommit[] = [];
  for (const item of value.commits as unknown[]) {
    const commit = item as Record<string, unknown> | null;
    if (!commit || typeof commit !== "object" || typeof commit.id !== "string" || !/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(commit.id)
      || !text(commit.shortId, 64) || typeof commit.author !== "string" || commit.author.length > 128 || !text(commit.time, 64)
      || Number.isNaN(Date.parse(commit.time)) || typeof commit.subject !== "string" || commit.subject.length > 256) return null;
    commits.push({ id: commit.id, shortId: commit.shortId, author: commit.author, time: commit.time, subject: commit.subject });
  }
  return { revision: value.revision, commits, more: value.more };
}

/** What one side of a changed file holds. */
export type ChangeSideState = "text" | "absent" | "binary" | "too_large" | "unreadable";
export type ChangeContent = Readonly<{ path: string; revision: string; original: string; originalState: ChangeSideState;
  modified: string; modifiedState: ChangeSideState }>;
const sideStates: readonly ChangeSideState[] = ["text", "absent", "binary", "too_large", "unreadable"];

/** Accepts a well-formed `projectGit.file` answer for the asked file; anything else is the status of a failed read. */
export function changeContent(reply: unknown, projectId: string, path: string): ChangeContent | string {
  if (!reply || typeof reply !== "object") return "read_failed";
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok") return typeof value.status === "string" && /^[a-z_]{1,64}$/.test(value.status) ? value.status : "read_failed";
  const side = (content: unknown, state: unknown): content is string | null => sideStates.includes(state as ChangeSideState)
    && (state === "text" ? typeof content === "string" && content.length <= 2 * 1024 * 1024 : content === null);
  if (value.projectId !== projectId || value.path !== path || !text(value.revision, 64) || !side(value.original, value.originalState)
    || !side(value.modified, value.modifiedState)) return "read_failed";
  return { path, revision: value.revision, original: (value.original as string | null) ?? "", originalState: value.originalState as ChangeSideState,
    modified: (value.modified as string | null) ?? "", modifiedState: value.modifiedState as ChangeSideState };
}

/** Why a file has no text diff to show, or null when both sides can be compared. */
export function changeContentNotice(content: ChangeContent): "binary" | "too_large" | "unreadable" | null {
  for (const reason of ["binary", "too_large", "unreadable"] as const)
    if (content.originalState === reason || content.modifiedState === reason) return reason;
  return null;
}

/** The one-letter mark of a status, as source control views write it. */
export const changeLetter = (status: ChangeStatus) => ({ modified: "M", added: "A", deleted: "D", renamed: "R", copied: "C", conflicted: "!", untracked: "U" })[status];
export const changeLabel = (status: ChangeStatus) => ({ modified: "Modified", added: "Added", deleted: "Deleted", renamed: "Renamed", copied: "Copied",
  conflicted: "Conflicted", untracked: "Untracked" } as const)[status];

/** The files whose path contains every word of the filter, ignoring case. */
export function filterChanges(files: readonly ChangedFile[], query: string): readonly ChangedFile[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return files;
  return files.filter(file => { const path = file.path.toLowerCase(); return words.every(word => path.includes(word)); });
}

export const changeFileName = (path: string) => path.slice(path.lastIndexOf("/") + 1);
export const changeFolder = (path: string) => path.lastIndexOf("/") < 0 ? "" : path.slice(0, path.lastIndexOf("/"));

export type ChangeRow = Readonly<{ kind: "folder"; key: string; path: string; name: string; depth: number; collapsed: boolean;
  files: number; insertions: number; deletions: number }
  | { kind: "file"; key: string; file: ChangedFile; name: string; folder: string; depth: number }>;

/** The files in the order the tree shows them, which is also the order of the flat list and of the keyboard. */
export function orderChanges(files: readonly ChangedFile[]): ChangedFile[] {
  return changeTreeRows(files, new Set()).flatMap(row => row.kind === "file" ? [row.file] : []);
}

/** The rows of the flat list: one per file, in the order of the list. */
export function changeListRows(files: readonly ChangedFile[]): ChangeRow[] {
  return files.map(file => ({ kind: "file", key: `file:${file.path}`, file, name: changeFileName(file.path), folder: changeFolder(file.path), depth: 0 }));
}

type Folder = { name: string; path: string; folders: Map<string, Folder>; files: ChangedFile[]; count: number; insertions: number; deletions: number };

/**
 * The rows of the tree: folders before files, each in name order. A folder whose only content is one folder
 * is shown on the same row as it (`src/app/views`), and the rows under a collapsed folder are left out.
 */
export function changeTreeRows(files: readonly ChangedFile[], collapsed: ReadonlySet<string>): ChangeRow[] {
  const root: Folder = { name: "", path: "", folders: new Map(), files: [], count: 0, insertions: 0, deletions: 0 };
  const bump = (folder: Folder, file: ChangedFile) => { folder.count++; folder.insertions += file.insertions ?? 0; folder.deletions += file.deletions ?? 0; };
  for (const file of files) {
    const segments = file.path.split("/");
    let folder = root;
    bump(root, file);
    for (let index = 0; index < segments.length - 1; index++) {
      let next = folder.folders.get(segments[index]);
      if (!next) folder.folders.set(segments[index], next = { name: segments[index], path: segments.slice(0, index + 1).join("/"),
        folders: new Map(), files: [], count: 0, insertions: 0, deletions: 0 });
      folder = next;
      bump(folder, file);
    }
    folder.files.push(file);
  }
  const order = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base", numeric: true }) || (a < b ? -1 : a > b ? 1 : 0);
  const rows: ChangeRow[] = [];
  const visit = (folder: Folder, depth: number) => {
    for (const name of [...folder.folders.keys()].sort(order)) {
      let child = folder.folders.get(name)!, label = child.name;
      while (child.files.length === 0 && child.folders.size === 1) { child = [...child.folders.values()][0]; label += `/${child.name}`; }
      const closed = collapsed.has(child.path);
      rows.push({ kind: "folder", key: `folder:${child.path}`, path: child.path, name: label, depth, collapsed: closed,
        files: child.count, insertions: child.insertions, deletions: child.deletions });
      if (!closed) visit(child, depth + 1);
    }
    for (const file of [...folder.files].sort((a, b) => order(changeFileName(a.path), changeFileName(b.path))))
      rows.push({ kind: "file", key: `file:${file.path}`, file, name: changeFileName(file.path), folder: changeFolder(file.path), depth });
  };
  visit(root, 0);
  return rows;
}

/**
 * The file to show after a list was read: the selected one while it is still listed, otherwise the file that
 * took its place in the order (the next one, or the last), and the first file when nothing was selected.
 */
export function selectedChange(previous: readonly ChangedFile[], next: readonly ChangedFile[], selected: string | null): string | null {
  if (!next.length) return null;
  if (selected !== null && next.some(file => file.path === selected)) return selected;
  const index = selected === null ? -1 : previous.findIndex(file => file.path === selected);
  if (index < 0) return next[0].path;
  const later = previous.slice(index + 1).find(file => next.some(value => value.path === file.path));
  return later?.path ?? next[Math.min(index, next.length - 1)].path;
}

/** How many of the five squares of a change bar are additions and how many are removals. */
export function changeBar(insertions: number | null, deletions: number | null): Readonly<{ added: number; removed: number }> {
  const added = insertions ?? 0, removed = deletions ?? 0, total = added + removed;
  if (!total) return { added: 0, removed: 0 };
  const squares = Math.min(5, Math.max(1, Math.ceil(Math.log2(total + 1) / 2)));
  let green = Math.round(squares * added / total);
  if (added && !green) green = 1;
  if (removed && green === squares) green = squares - 1;
  return { added: green, removed: squares - green };
}

/** The height of a line of a diff, in pixels. */
export const changeLineHeight = 20;
/**
 * The tallest diff the view of all files shows whole, in pixels: a longer one scrolls by itself. An editor that
 * is as tall as its text draws every line of it, which a thousand lines still do in a fraction of a second.
 */
export const changeDiffLimit = 1000 * changeLineHeight;
const leastScrolledDiff = 12 * changeLineHeight;

/**
 * The height the diff of a file takes in the view of all files: that of its content up to the limit. A longer
 * one takes what the view shows at once (`viewport`, in pixels) and scrolls by itself.
 */
export function fittedDiffHeight(content: number, viewport: number): number {
  if (!(content > changeDiffLimit)) return content > 0 ? Math.ceil(content) : 0;
  return Math.min(changeDiffLimit, Math.max(leastScrolledDiff, Math.floor(viewport)));
}

/**
 * The height the diff of a file is given in the view of all files before it was read: its changed lines and
 * some lines around them, or the whole file when it is new or gone.
 */
export function estimatedDiffHeight(file: ChangedFile, viewport: number): number {
  const changed = (file.insertions ?? 0) + (file.deletions ?? 0);
  const whole = file.status === "added" || file.status === "untracked" || file.status === "deleted";
  const lines = file.binary || !changed ? 2 : whole ? changed : changed + 8;
  return fittedDiffHeight(12 + lines * changeLineHeight, viewport);
}

/**
 * The section a view that scrolled to `offset` shows first: the last one that starts at or above it, where
 * `top` gives the start of each section in their order. -1 when there is none.
 */
export function sectionAt(count: number, top: (index: number) => number, offset: number): number {
  let low = 0, high = count - 1, found = count > 0 ? 0 : -1;
  while (low <= high) {
    const middle = (low + high) >> 1;
    if (top(middle) <= offset) { found = middle; low = middle + 1; } else high = middle - 1;
  }
  return found;
}

/** The path of a changed file inside its project, or null when the file is outside the project folder. */
export function projectRelativePath(prefix: string, path: string): string | null {
  return !prefix ? path : path.startsWith(prefix) && path.length > prefix.length ? path.slice(prefix.length) : null;
}

/** What the diff side of a changes tab shows: the selected file alone, or every file, one under the other, in one view that scrolls. */
export type ChangesView = "file" | "all";
export const changesViews: readonly ChangesView[] = ["file", "all"];
export const changesViewLabel = (view: ChangesView) => view === "all" ? "All files in one view" : "One file at a time";

/** How the changes tab is laid out; kept for the next tab and the next start. */
export type ChangesPreferences = Readonly<{ layout: "tree" | "list"; view: ChangesView; sideBySide: boolean; wrap: boolean; ignoreWhitespace: boolean;
  collapseUnchanged: boolean; autoRefresh: boolean; listWidth: number;
  /** The height of the history under the files. */
  historyHeight: number }>;
export const changesPreferencesKey = "codealta.desktop.changes.v1";
export const defaultChangesPreferences: ChangesPreferences = { layout: "tree", view: "file", sideBySide: true, wrap: false, ignoreWhitespace: false,
  collapseUnchanged: true, autoRefresh: true, listWidth: 280, historyHeight: 220 };
export const changeListWidth = (value: number) => Math.min(560, Math.max(180, Math.round(value)));
export const changeHistoryHeight = (value: number) => Math.min(900, Math.max(64, Math.round(value)));

export function restoreChangesPreferences(read: () => string | null): ChangesPreferences {
  try {
    const raw = read();
    if (!raw || raw.length > 1024) return defaultChangesPreferences;
    const value = JSON.parse(raw) as Record<string, unknown> | null;
    if (!value || typeof value !== "object") return defaultChangesPreferences;
    const flag = (name: keyof ChangesPreferences) => typeof value[name] === "boolean" ? value[name] as boolean : defaultChangesPreferences[name] as boolean;
    return { layout: value.layout === "list" ? "list" : "tree", view: value.view === "all" ? "all" : "file", sideBySide: flag("sideBySide"), wrap: flag("wrap"), ignoreWhitespace: flag("ignoreWhitespace"),
      collapseUnchanged: flag("collapseUnchanged"), autoRefresh: flag("autoRefresh"),
      listWidth: Number.isFinite(value.listWidth) ? changeListWidth(value.listWidth as number) : defaultChangesPreferences.listWidth,
      historyHeight: Number.isFinite(value.historyHeight) ? changeHistoryHeight(value.historyHeight as number) : defaultChangesPreferences.historyHeight };
  } catch { return defaultChangesPreferences; }
}

export function persistChangesPreferences(write: (value: string) => void, value: ChangesPreferences): boolean {
  try { write(JSON.stringify(value)); return true; } catch { return false; }
}
