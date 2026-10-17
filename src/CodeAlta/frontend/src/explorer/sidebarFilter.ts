import type { WorkspaceClaudeCodeConversation, WorkspaceClaudeCodeSession, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import type { SessionHierarchyRow } from "../sessionHierarchy";
import { fieldsScore } from "../search/searchResults";
import { plainTitle } from "../sessionTitle";

/** Whether a session of CodeAlta has every word typed in the filter of the sidebar in its title. */
export const sessionMatches = (session: Readonly<Pick<WorkspaceSession, "title" | "fullTitle">>, words: readonly string[]): boolean =>
  fieldsScore(words, [[plainTitle(session.title), 0], [session.fullTitle, 3]]) !== null;

/**
 * The rows of a list of sessions the filter keeps: those whose title has every word, and the sessions they are under,
 * so that a sub-agent found stays under its session. The order of the tree is kept.
 */
export function filterSessionRows(rows: readonly SessionHierarchyRow[], words: readonly string[]): SessionHierarchyRow[] {
  if (!words.length) return [...rows];
  const kept = new Set<number>();
  const path: number[] = [];
  rows.forEach((row, index) => {
    path.length = row.depth;
    if (sessionMatches(row.session, words)) { kept.add(index); for (const parent of path) if (parent !== undefined) kept.add(parent); }
    path[row.depth] = index;
  });
  return rows.filter((_, index) => kept.has(index));
}

/** The key the Claude Code sessions of no project are kept under, among those of the projects. */
export const unplacedKey = "";

/**
 * What the filter of the sidebar shows: the scopes with a session found (the chats as `null`), the projects shown (a
 * name found, a session found, or a Claude Code session found), and the Claude Code sessions found, by the project that
 * lists them (`unplacedKey` for those of no project), as rows of their group.
 */
export type SidebarFilterView = Readonly<{
  scopes: ReadonlySet<string | null>;
  projects: ReadonlySet<string>;
  claudeCode: ReadonlyMap<string, readonly WorkspaceClaudeCodeSession[]>;
}>;

/** What the words typed in the filter of the sidebar find in the projects, their sessions and the Claude Code sessions started elsewhere. */
export function sidebarFilterView(snapshot: WorkspaceSnapshot, words: readonly string[], conversations: readonly WorkspaceClaudeCodeConversation[] = []): SidebarFilterView {
  const scopes = new Set<string | null>();
  for (const session of snapshot.sessions) if (sessionMatches(session, words)) scopes.add(session.projectId);
  const known = new Set(snapshot.projects.map(project => project.id));
  const claudeCode = new Map<string, WorkspaceClaudeCodeSession[]>();
  for (const conversation of conversations) {
    if (conversation.projectId !== null && !known.has(conversation.projectId)) continue;
    if (fieldsScore(words, [[plainTitle(conversation.title), 0]]) === null) continue;
    const key = conversation.projectId ?? unplacedKey;
    const rows = claudeCode.get(key) ?? [];
    rows.push({ cliSessionId: conversation.cliSessionId, title: conversation.title, lastChangedAt: conversation.lastChangedAt, origin: conversation.origin,
      held: false, holderStatus: null, tag: conversation.tag, startDirectory: conversation.startDirectory });
    claudeCode.set(key, rows);
  }
  const projects = new Set<string>();
  for (const project of snapshot.projects)
    if (scopes.has(project.id) || claudeCode.has(project.id) || fieldsScore(words, [[project.name, 0]]) !== null) projects.add(project.id);
  return { scopes, projects, claudeCode };
}

/** Whether the filter opens a scope of the tree (the chats as `null`): what it found is in it. */
export const filterOpens = (view: SidebarFilterView, id: string | null): boolean => id === null ? view.scopes.has(null) : view.projects.has(id);

/**
 * The scopes the filter opens that were folded while it is used, with one more folded or opened again: kept apart from
 * the tree of the sidebar, which the filter does not change, and forgotten when it is emptied.
 */
export function toggleFilterFold(folded: ReadonlySet<string | null>, id: string | null): ReadonlySet<string | null> {
  const next = new Set(folded);
  if (!next.delete(id)) next.add(id);
  return next;
}
