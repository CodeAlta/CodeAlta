// What the host and `alta window` call a tab: one text for each tab, whichever window holds it. The host routes the
// requests of a tab by it (`editor:<project>`, `changes:<project>`, `documentation`), so the two sides name tabs alike.
import { isCanvasTab, isClaudeCodeTab, isTerminalTab, type FileTab } from "../fileTabs";
import type { SessionTab } from "../sessionTabs";

/** The reference of the tab of a session. */
export const sessionTabRef = (sessionId: string) => `session:${sessionId}`;

/** The reference of a tab of the dock that is no session: its view and what it shows. */
export function fileTabRef(tab: FileTab): string {
  if (isTerminalTab(tab)) return `terminal:${tab.terminalId ?? ""}`;
  if (isClaudeCodeTab(tab)) return `claudeCode:${tab.key ?? ""}`;
  if (tab.view === "workflowRun") return `workflowRun:${tab.key ?? ""}`;
  if (isCanvasTab(tab)) return `canvas:${tab.pluginKey ?? ""}/${tab.canvasId ?? ""}/${tab.sessionId ? "" : tab.projectId}/${tab.sessionId ?? ""}/${tab.key ?? ""}`;
  // The tabs of a project, or of the folder of a plugin, a skill or a file a link named: one for each.
  if (tab.view === "editor" || tab.view === "changes") return `${tab.view}:${tab.projectId}`;
  // The tabs that the application has once: the automations, the work items, the issues, the canvases, the guide, the bookmarks.
  return tab.view;
}

/** The reference of the session of a tab of the dock, or of a file tab. */
export const tabRef = (tab: Readonly<{ session: SessionTab } | { file: FileTab }>) => "session" in tab ? sessionTabRef(tab.session.sessionId) : fileTabRef(tab.file);

/**
 * Whether a tab belongs to the space of its window, and so goes only to a window of that space: a session, and what
 * is of a project. The tabs of the application (the guide, the automations…) go to any window.
 */
export function spaceBound(tab: Readonly<{ session: SessionTab } | { file: FileTab }>): boolean {
  if ("session" in tab) return true;
  const file = tab.file;
  if (isTerminalTab(file)) return false;
  if (isCanvasTab(file)) return file.projectId !== "" || !!file.sessionId;
  return file.view === "editor" || file.view === "changes" || file.view === "claudeCode";
}

/** What a tab shows, from its reference: `session`, `editor`, `changes`, `terminal`, `canvas`, `documentation`… */
export function tabRefKind(ref: string): string {
  const colon = ref.indexOf(":");
  return colon < 0 ? ref : ref.slice(0, colon);
}

/** What a moved tab carries to the window that receives it, which only the pages read. */
export type TabTransfer = Readonly<{ version: 1; session: SessionTab } | { version: 1; file: FileTab }>;

/** The payload of a move of a tab. */
export const transferOf = (tab: Readonly<{ session: SessionTab } | { file: FileTab }>): string =>
  JSON.stringify("session" in tab ? { version: 1, session: tab.session } : { version: 1, file: tab.file });

/** The tab a payload carries; null for one that is not understood. */
export function readTransfer(payload: string | null | undefined): Readonly<{ session: SessionTab } | { file: FileTab }> | null {
  if (!payload || payload.length > 65_536) return null;
  try {
    const value: unknown = JSON.parse(payload);
    if (!value || typeof value !== "object" || (value as { version?: unknown }).version !== 1) return null;
    const session = (value as { session?: Partial<SessionTab> }).session;
    if (session && typeof session === "object") {
      if (typeof session.sessionId !== "string" || !session.sessionId || session.sessionId.length > 256) return null;
      const projectId = typeof session.projectId === "string" ? session.projectId : null;
      const path = typeof session.path === "string" ? session.path : null;
      return { session: { sessionId: session.sessionId, projectId, path } };
    }
    const file = (value as { file?: Partial<FileTab> }).file;
    if (!file || typeof file !== "object" || typeof file.view !== "string" || typeof file.projectId !== "string" || typeof file.projectPath !== "string") return null;
    return { file: file as FileTab };
  } catch { return null; }
}
