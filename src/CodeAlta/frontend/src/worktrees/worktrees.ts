import type { WorkspaceSession } from "#neoastra";
import type { MessageKey } from "../localization";

/** Where the next session of a project works: in the folder of the project, or in a new git worktree. */
export type WorkPlace = Readonly<{ worktree: boolean; base: string | null }>;
export const projectFolder: WorkPlace = Object.freeze({ worktree: false, base: null });
export const workPlacesKey = "codealta.desktop.workPlaces.v1";
const workPlaceLimit = 256;

/** A checkout of a project's repository: the one the project lives in, or a worktree. */
export type Worktree = Readonly<{ path: string; name: string; branch: string | null; head: string | null; main: boolean; locked: boolean; missing: boolean;
  folder: string; busy: boolean }>;
export type WorktreeList = Readonly<{ worktrees: readonly Worktree[]; newFolder: string | null }>;
export type Branch = Readonly<{ name: string; current: boolean; remote: boolean; worktree: string | null; worktreeName: string | null }>;
export type BranchList = Readonly<{ branches: readonly Branch[]; busy: boolean }>;

const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit;
const optional = (value: unknown, limit: number): value is string | null => value === null || text(value, limit);

/** A branch or commit name that can be handed to git: no space, none of the characters git forbids, never an option. */
export function referenceName(value: string): boolean {
  return value.length > 0 && value.length <= 200 && !/^[-/.]|[/.]$|[\u0000- \u007f~^:?*[\\]|\.\.|@\{|\/\/|\/\.|\.lock$/u.test(value) && value !== "@";
}

/** The places chosen for the projects, as they were stored; anything else reads as none. */
export function restoreWorkPlaces(read: () => string | null): ReadonlyMap<string, WorkPlace> {
  const places = new Map<string, WorkPlace>();
  try {
    const raw = read();
    if (!raw || raw.length > 65536) return places;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object" || Array.isArray(value)) return places;
    for (const [id, stored] of Object.entries(value as Record<string, unknown>)) {
      if (places.size === workPlaceLimit || !text(id, 256) || !stored || typeof stored !== "object") continue;
      const { worktree, base } = stored as { worktree?: unknown; base?: unknown };
      if (worktree !== true) continue; // The folder of the project is the place of a project that stores none.
      places.set(id, { worktree: true, base: typeof base === "string" && referenceName(base) ? base : null });
    }
  } catch { /* Nothing stored that can be read. */ }
  return places;
}

/** Sets the place of a project; the folder of the project is stored as no entry. */
export function withWorkPlace(places: ReadonlyMap<string, WorkPlace>, projectId: string, place: WorkPlace): ReadonlyMap<string, WorkPlace> {
  const next = new Map(places);
  next.delete(projectId);
  if (place.worktree) next.set(projectId, { worktree: true, base: place.base });
  while (next.size > workPlaceLimit) next.delete(next.keys().next().value!);
  return next;
}

export function persistWorkPlaces(write: (value: string) => void, places: ReadonlyMap<string, WorkPlace>): boolean {
  try { write(JSON.stringify(Object.fromEntries(places))); return true; }
  catch { return false; }
}

/** The worktree a session works in, as its row records it. */
export function sessionWorktree(session: Pick<WorkspaceSession, "worktreePath" | "worktreeRoot" | "worktreeName" | "worktreeMissing"> | undefined | null) {
  return session && text(session.worktreePath, 4096)
    ? { path: session.worktreePath, root: text(session.worktreeRoot, 4096) ? session.worktreeRoot : session.worktreePath,
      name: text(session.worktreeName, 256) ? session.worktreeName : folderName(session.worktreePath), missing: session.worktreeMissing === true }
    : null;
}

/** The last part of a folder. */
export function folderName(path: string): string {
  const parts = path.split(/[\\/]+/u).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}

/** Whether two folders are the same one: Windows folders differ neither by case nor by the kind of slash. */
export function sameFolder(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  const windows = /^[a-zA-Z]:[\\/]|^\\\\/u.test(a);
  const clean = (value: string) => { const trimmed = value.replace(/[\\/]+$/u, ""); return windows ? trimmed.replace(/\//gu, "\\").toLowerCase() : trimmed; };
  return clean(a) === clean(b);
}

/** The sessions that work in a checkout: the ones of its worktree, or for the checkout of the project, the ones that have none. */
export function worktreeSessions<T extends Pick<WorkspaceSession, "worktreePath" | "worktreeRoot" | "worktreeMissing">>(sessions: readonly T[], worktree: Worktree): readonly T[] {
  return sessions.filter(session => worktree.main ? !session.worktreePath || session.worktreeMissing
    : !!session.worktreePath && !session.worktreeMissing && sameFolder(session.worktreeRoot ?? session.worktreePath, worktree.path));
}

/** Accepts a well-formed `worktrees.list` answer for the asked project; anything else is its status. */
export function worktreesReply(reply: unknown, projectId: string): WorktreeList | string {
  if (!reply || typeof reply !== "object") return "read_failed";
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok") return text(value.status, 64) ? value.status : "read_failed";
  if (value.projectId !== projectId || !Array.isArray(value.worktrees) || value.worktrees.length > 512 || !optional(value.newFolder, 4096)) return "read_failed";
  const worktrees: Worktree[] = [];
  for (const item of value.worktrees as unknown[]) {
    if (!item || typeof item !== "object") return "read_failed";
    const row = item as Record<string, unknown>;
    if (!text(row.path, 4096) || !text(row.name, 4096) || !optional(row.branch, 256) || !optional(row.head, 64) || !text(row.folder, 4096)
      || typeof row.main !== "boolean" || typeof row.locked !== "boolean" || typeof row.missing !== "boolean" || typeof row.busy !== "boolean") return "read_failed";
    worktrees.push({ path: row.path, name: row.name, branch: row.branch, head: row.head, main: row.main, locked: row.locked, missing: row.missing, folder: row.folder, busy: row.busy });
  }
  return { worktrees, newFolder: value.newFolder };
}

/** Accepts a well-formed `worktrees.branches` answer; anything else is its status. */
export function branchesReply(reply: unknown): BranchList | string {
  if (!reply || typeof reply !== "object") return "read_failed";
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok") return text(value.status, 64) ? value.status : "read_failed";
  if (!Array.isArray(value.branches) || value.branches.length > 1024 || typeof value.busy !== "boolean") return "read_failed";
  const branches: Branch[] = [];
  for (const item of value.branches as unknown[]) {
    if (!item || typeof item !== "object") return "read_failed";
    const row = item as Record<string, unknown>;
    if (!text(row.name, 256) || typeof row.current !== "boolean" || typeof row.remote !== "boolean" || !optional(row.worktree, 4096) || !optional(row.worktreeName, 4096))
      return "read_failed";
    branches.push({ name: row.name, current: row.current, remote: row.remote, worktree: row.worktree, worktreeName: row.worktreeName });
  }
  return { branches, busy: value.busy };
}

/** The branches whose name holds every word typed, the current one first. */
export function filterBranches(branches: readonly Branch[], filter: string): readonly Branch[] {
  const words = filter.trim().toLowerCase().split(/\s+/u).filter(Boolean);
  const shown = words.length ? branches.filter(branch => words.every(word => branch.name.toLowerCase().includes(word))) : branches;
  return [...shown].sort((a, b) => Number(b.current) - Number(a.current));
}

/** Whether what is typed can be the name of a new branch: a valid name that no branch has. */
export function newBranchName(filter: string, branches: readonly Branch[]): string | null {
  const name = filter.trim();
  return referenceName(name) && !branches.some(branch => branch.name === name || branch.remote && branch.name.slice(branch.name.indexOf("/") + 1) === name) ? name : null;
}

const failures: Readonly<Record<string, MessageKey>> = {
  in_use: "A session is working there. Wait for it to finish, or stop it.",
  dirty: "The worktree holds changes that are not committed.",
  locked: "Git keeps this worktree locked: unlock it with git to remove it.",
  main: "This is the folder of the project: it is not a worktree to remove.",
  not_worktree: "The worktree is no longer there.",
  worktree_missing: "The worktree is no longer there.",
  not_found: "The branch is no longer there.",
  not_repository: "This folder is not in a git repository.",
  no_commit: "The repository has no commit yet: a worktree starts from one.",
  invalid: "The branch is not one of the repository.",
  invalid_request: "The name is not one git accepts.",
  git_unavailable: "Git is not installed.",
  timeout: "Git did not answer in time.",
  stale_epoch: "The host changed. Reload the window.",
  unavailable: "Worktrees cannot be used in this window.",
};

/** What to tell the user when a change to a checkout was refused: a sentence of ours, then what git said. */
export function worktreeFailure(status: string, message: string | null | undefined, t: (key: MessageKey) => string): string {
  const known = failures[status];
  const said = typeof message === "string" && message.trim() ? message.trim() : null;
  return known ? t(known) : said ?? t("Git could not do it.");
}
