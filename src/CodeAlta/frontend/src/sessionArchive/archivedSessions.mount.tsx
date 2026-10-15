// Disposable page: the production browser of the saved sessions, with archived sessions and the answers the test gives it.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SessionBrowser } from "../SavedSessionBrowser";
import { ShellLanguageContext } from "../shellLanguage";

const session = (id: string, title: string, changes: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title, fullTitle: title, fullTitleTruncated: false, workspacePath: "/chats", providerKey: "codex", updatedAt: "2026-10-14T10:00:00Z",
  createdAt: "2026-10-14T09:00:00Z", parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, messageCount: 2,
  automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null,
  forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, ...changes,
});
const snapshot: WorkspaceSnapshot = { configured: true, projects: [], sessions: [session("active", "Draft the notes")],
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
const archived = [
  session("old", "Plan the release", { archived: true, archivedAt: "2026-10-15T08:00:00Z" }),
  session("older", "Fix the parser", { archived: true, archivedAt: "2026-10-12T08:00:00Z" }),
  // Archived in a project: not in the scope of the chats.
  session("elsewhere", "Elsewhere", { archived: true, archivedAt: "2026-10-13T08:00:00Z", scopeKind: "project", projectId: "p1", workspacePath: "/p1" }),
];
const state = { restored: [] as string[], info: [] as string[], removed: [] as [string, boolean][], opened: [] as string[], closed: 0 };
// What the next deletion answers: why it was not done, or nothing for a deletion that is made.
let answer: string | null = null;
let asks = true;

let show: ((value: number | null) => void) | undefined;
function Fixture() {
  const [shown, setShown] = useState<number | null>(null);
  show = setShown;
  return <div className="ide-shell" style={{ display: "block", padding: 16 }}>
    {shown !== null && <SessionBrowser key={shown} snapshot={snapshot} projectId={null} stale={false}
      open={tab => { state.opened.push(tab.sessionId); setShown(null); return true; }} close={() => { state.closed++; setShown(null); }}
      archived={{ sessions: archived, busy: false, deleteAsks: asks,
        restore: value => { state.restored.push(value.id); setShown(null); },
        info: value => { state.info.push(value.id); },
        remove: async (value, remember) => { state.removed.push([value.id, remember]); return answer; } }} />}
  </div>;
}

let revision = 0;
Object.assign(window, { archivedFixture: { state,
  show(deleteAsks = true) { asks = deleteAsks; flushSync(() => show!(++revision)); },
  /** What the next deletion answers. */
  answer(text: string | null) { answer = text; },
} });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
