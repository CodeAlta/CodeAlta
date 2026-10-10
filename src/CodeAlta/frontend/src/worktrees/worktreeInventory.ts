import type { MessageKey } from "../localization";
import { worktreeFailure } from "./worktrees";

/** A session of the catalog that records a checkout. */
export type InventorySession = Readonly<{ id: string; title: string; updatedAt: string; running: boolean }>;
/** Why a checkout is not removed: it is the one of the project or the main one, a session is at work in it, or git keeps it. */
export type Protection = "main" | "in_use" | "locked";
/**
 * A checkout of a project's repository, as the window of the worktrees shows it: what git lists, whether or not
 * a session still records it, with the sessions that do.
 */
export type InventoryRow = Readonly<{ path: string; name: string; branch: string | null; head: string | null; main: boolean; project: boolean; locked: boolean; missing: boolean;
  folder: string; busy: boolean; protection: Protection | null; sessions: readonly InventorySession[]; sessionCount: number; lastUsedAt: string | null }>;
export type Inventory = Readonly<{ rows: readonly InventoryRow[]; sessionsKnown: boolean;
  /** Git lists more checkouts than the host answered with. */
  truncated?: boolean }>;
/** What became of one worktree that was asked to go. */
export type Removal = Readonly<{ path: string; status: string; message: string | null; branchKept: string | null; branchDeleted: string | null }>;
export type RemovalOutcome = Removal & Readonly<{ row: InventoryRow }>;

/** The worktrees one request names: the window says how far it is between two of them, and can stop there. */
export const removalChunkSize = 4;

const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit;
const optional = (value: unknown, limit: number): value is string | null => value === null || text(value, limit);
const time = (value: unknown): value is string => text(value, 64) && Number.isFinite(Date.parse(value));
const protections: readonly unknown[] = ["main", "in_use", "locked"];

/** Accepts a well-formed `worktrees.inventory` answer for the asked project; anything else is its status. */
export function inventoryReply(reply: unknown, projectId: string): Inventory | string {
  if (!reply || typeof reply !== "object") return "read_failed";
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok") return text(value.status, 64) ? value.status : "read_failed";
  if (value.projectId !== projectId || !Array.isArray(value.worktrees) || value.worktrees.length > 512 || typeof value.sessionsKnown !== "boolean"
    || value.truncated !== undefined && typeof value.truncated !== "boolean") return "read_failed";
  const rows: InventoryRow[] = [];
  const seen = new Set<string>();
  for (const item of value.worktrees as unknown[]) {
    if (!item || typeof item !== "object") return "read_failed";
    const row = item as Record<string, unknown>;
    if (!text(row.path, 4096) || seen.has(row.path) || !text(row.name, 4096) || !optional(row.branch, 256) || !optional(row.head, 64) || !text(row.folder, 4096)
      || typeof row.main !== "boolean" || typeof row.project !== "boolean" || typeof row.locked !== "boolean" || typeof row.missing !== "boolean" || typeof row.busy !== "boolean"
      || row.protection !== null && !protections.includes(row.protection) || !Array.isArray(row.sessions) || row.sessions.length > 16
      || typeof row.sessionCount !== "number" || !Number.isInteger(row.sessionCount) || row.sessionCount < row.sessions.length
      || row.lastUsedAt !== null && !time(row.lastUsedAt)) return "read_failed";
    const sessions: InventorySession[] = [];
    for (const entry of row.sessions as unknown[]) {
      if (!entry || typeof entry !== "object") return "read_failed";
      const session = entry as Record<string, unknown>;
      if (!text(session.id, 256) || !text(session.title, 512) || !time(session.updatedAt) || typeof session.running !== "boolean") return "read_failed";
      sessions.push({ id: session.id, title: session.title, updatedAt: session.updatedAt, running: session.running });
    }
    seen.add(row.path);
    rows.push({ path: row.path, name: row.name, branch: row.branch, head: row.head, main: row.main, project: row.project, locked: row.locked, missing: row.missing,
      folder: row.folder, busy: row.busy, protection: row.protection as Protection | null, sessions, sessionCount: row.sessionCount, lastUsedAt: row.lastUsedAt });
  }
  return value.truncated ? { rows, sessionsKnown: value.sessionsKnown, truncated: true } : { rows, sessionsKnown: value.sessionsKnown };
}

/**
 * Accepts a `worktrees.removeMany` answer that says what became of exactly the folders that were asked, in their
 * order; anything else is its status. An answer for other folders is never read as an answer for these.
 */
export function removalReply(reply: unknown, asked: readonly string[]): readonly Removal[] | string {
  if (!reply || typeof reply !== "object") return "read_failed";
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok") return text(value.status, 64) ? value.status : "read_failed";
  if (!Array.isArray(value.results) || value.results.length !== asked.length) return "read_failed";
  const results: Removal[] = [];
  for (const [index, item] of (value.results as unknown[]).entries()) {
    if (!item || typeof item !== "object") return "read_failed";
    const result = item as Record<string, unknown>;
    if (result.path !== asked[index] || !text(result.status, 64) || !optional(result.message ?? null, 2048) || !optional(result.branchKept ?? null, 256)
      || !optional(result.branchDeleted ?? null, 256)) return "read_failed";
    results.push({ path: result.path as string, status: result.status, message: (result.message ?? null) as string | null,
      branchKept: (result.branchKept ?? null) as string | null, branchDeleted: (result.branchDeleted ?? null) as string | null });
  }
  return results;
}

/**
 * Whether a checkout can be asked to go: the host said nothing protects it, and nothing the row itself says does.
 * The host decides again when it is asked.
 */
export const removable = (row: InventoryRow) => row.protection === null && !row.main && !row.project && !row.busy && !row.locked;

/** Whether the changes of a checkout can be shown: every one whose folder is there, the main one of the repository included. */
export const showsChanges = (row: InventoryRow) => !row.missing;

/** The checkouts whose folder is there, then the ones git still lists without their folder. */
export function inventoryGroups(rows: readonly InventoryRow[]): Readonly<{ present: readonly InventoryRow[]; gone: readonly InventoryRow[] }> {
  return { present: rows.filter(row => !row.missing), gone: rows.filter(row => row.missing) };
}

/** What stays selected when the list was read again: the worktrees that are still there and can still go. */
export function keepSelection(selected: ReadonlySet<string>, rows: readonly InventoryRow[]): ReadonlySet<string> {
  const kept = new Set(rows.filter(row => removable(row) && selected.has(row.path)).map(row => row.path));
  return kept.size === selected.size ? selected : kept;
}

/** Selects every worktree that can go, or none when they all are. */
export function toggleAll(selected: ReadonlySet<string>, rows: readonly InventoryRow[]): ReadonlySet<string> {
  const all = rows.filter(removable).map(row => row.path);
  return all.length > 0 && all.every(path => selected.has(path)) ? new Set() : new Set(all);
}

export function toggleOne(selected: ReadonlySet<string>, row: InventoryRow): ReadonlySet<string> {
  const next = new Set(selected);
  if (!next.delete(row.path) && removable(row)) next.add(row.path);
  return next;
}

/** The selected worktrees that can go, in the order of the list. */
export const selectedRows = (selected: ReadonlySet<string>, rows: readonly InventoryRow[]) => rows.filter(row => removable(row) && selected.has(row.path));

/** When a checkout was last used and by which of the sessions that are named; null when no session records it. */
export function lastUse(row: InventoryRow): Readonly<{ at: string; session: InventorySession | null }> | null {
  if (row.lastUsedAt === null) return null;
  const latest = [...row.sessions].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt))[0] ?? null;
  return { at: row.lastUsedAt, session: latest };
}

/** The folders of one request after the other. */
export function removalChunks<T>(items: readonly T[], size = removalChunkSize): readonly (readonly T[])[] {
  const chunks: T[][] = [];
  for (let index = 0; index < items.length; index += size) chunks.push(items.slice(index, index + size));
  return chunks;
}

/** What was already known of some worktrees, with what a later request said of them in its place. */
export function mergeOutcomes(known: readonly RemovalOutcome[], later: readonly RemovalOutcome[]): readonly RemovalOutcome[] {
  const replaced = new Map(later.map(outcome => [outcome.row.path, outcome]));
  return [...known.map(outcome => replaced.get(outcome.row.path) ?? outcome), ...later.filter(outcome => !known.some(value => value.row.path === outcome.row.path))];
}

/** How many worktrees went, how many stay because they hold changes, and how many stay for another reason. */
export function removalCounts(outcomes: readonly RemovalOutcome[]): Readonly<{ removed: number; dirty: number; kept: number }> {
  const removed = outcomes.filter(outcome => outcome.status === "ok").length, dirty = outcomes.filter(outcome => outcome.status === "dirty").length;
  return { removed, dirty, kept: outcomes.length - removed - dirty };
}

const reasons: Readonly<Record<string, MessageKey>> = {
  canceled: "Not started.",
  unconfirmed: "The answer did not arrive: the list says whether it is still there.",
  read_failed: "The answer did not arrive: the list says whether it is still there.",
  unknown_project: "The project is no longer available.",
  project_unavailable: "The project is no longer available.",
};

/** Why a worktree stays, in a sentence. */
export function removalReason(status: string, message: string | null, t: (key: MessageKey) => string): string {
  const known = reasons[status];
  return known ? t(known) : worktreeFailure(status, message, t);
}

const inventoryFailures: Readonly<Record<string, MessageKey>> = {
  not_repository: "This folder is not in a git repository.",
  unknown_project: "The project is no longer available.",
  project_unavailable: "The project is no longer available.",
  stale_epoch: "The host changed. Reload the window.",
  unavailable: "Worktrees cannot be used in this window.",
};

/** Why the checkouts could not be listed. */
export const inventoryFailure = (status: string): MessageKey => inventoryFailures[status] ?? "The worktrees could not be listed.";

const editorFailures: Readonly<Record<string, MessageKey>> = {
  worktree_missing: "The folder of this worktree is gone.",
  not_worktree: "The worktree is no longer there.",
  no_window: "The code editor could not be opened.",
};

/** Why the code editor did not open on a checkout. */
export const editorFailure = (status: string, t: (key: MessageKey) => string): string => editorFailures[status] ? t(editorFailures[status]) : worktreeFailure(status, null, t);
