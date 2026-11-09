// Disposable page: the production browser of the saved sessions, with sessions checked together and the answers the test gives it.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SessionBrowser } from "../SavedSessionBrowser";
import { ShellLanguageContext } from "../shellLanguage";

const session = (id: string, title: string, updatedAt: string, changes: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title, fullTitle: title, fullTitleTruncated: false, workspacePath: "/parser", providerKey: "codex", updatedAt,
  createdAt: "2026-11-01T09:00:00Z", parentSessionId: null, scopeKind: "project", projectId: "p1", lineageIssue: null, messageCount: 2,
  automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null,
  forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, ...changes,
});
// The newest first: Review has a child in this project, Plan one in another project, Tests works in a worktree.
const sessions = [
  session("tests", "Write the tests", "2026-11-09T10:00:00Z", { worktreePath: "/trees/parser/quiet-heron", worktreeRoot: "/trees/parser/quiet-heron", worktreeName: "quiet-heron" }),
  session("check", "Check the lexer", "2026-11-08T10:00:00Z", { parentSessionId: "review" }),
  session("review", "Review the parser", "2026-11-07T10:00:00Z"),
  session("plan", "Plan the release", "2026-11-06T10:00:00Z"),
];
const elsewhere = session("notes", "Release notes", "2026-11-05T10:00:00Z", { parentSessionId: "plan", projectId: "p2", workspacePath: "/docs" });
const archived = [
  session("old", "Old spike", "2026-10-01T10:00:00Z", { archived: true, archivedAt: "2026-10-02T10:00:00Z" }),
  session("older", "Older spike", "2026-09-01T10:00:00Z", { archived: true, archivedAt: "2026-09-02T10:00:00Z" }),
];
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", name: "Parser", path: "/parser", archived: false }, { id: "p2", name: "Docs", path: "/docs", archived: false }],
  sessions: [...sessions, elsewhere], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false } as WorkspaceSnapshot;
const state = { archived: [] as string[][], restored: [] as string[][], removed: [] as string[][], worktrees: [] as string[][], checked: [] as string[][], opened: [] as string[] };

let show: ((value: number | null) => void) | undefined;
function Fixture() {
  const [shown, setShown] = useState<number | null>(null);
  show = setShown;
  return <div className="ide-shell" style={{ display: "block", padding: 16 }}>
    {shown !== null && <SessionBrowser key={shown} snapshot={snapshot} projectId="p1" stale={false}
      open={tab => { state.opened.push(tab.sessionId); setShown(null); return true; }} close={() => setShown(null)}
      archived={{ sessions: archived, busy: false, deleteAsks: true, restore: () => { }, info: () => { }, remove: async () => null }}
      checking={{ catalog: [...sessions, elsewhere, ...archived], busy: false, projectName: id => id === "p1" ? "Parser" : "Docs",
        archive: async rows => { state.archived.push(rows.map(row => row.id)); return rows.map(row => row.id); },
        restore: async rows => { state.restored.push(rows.map(row => row.id)); return rows.map(row => row.id); },
        remove: async (rows, worktrees) => {
          state.removed.push(rows.map(row => row.id)); state.worktrees.push(worktrees.map(item => item.path));
          return { deleted: rows.map(row => row.id), error: null };
        },
        checkWorktrees: async (worktrees, deleted) => { state.checked.push([...deleted]); return worktrees; } }} />}
  </div>;
}

let revision = 0;
Object.assign(window, { checkedFixture: { state, show() { flushSync(() => show!(++revision)); } } });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
