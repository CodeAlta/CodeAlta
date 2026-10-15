// Disposable page: the production window of the child sessions, with the tree and the answers the test gives it.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { WorkspaceSession, WorkspaceSessionChild } from "#neoastra";
import { dismissDialogsOnOutsidePress } from "../modalDialogs";
import { ShellLanguageContext } from "../shellLanguage";
import { readSpaces } from "../spaces/spaces";
import type { SessionChildren, SessionTreeDeletion } from "./sessionChildren";
import { SessionChildrenDialog } from "./SessionChildrenDialog";

const session = (id: string, title: string, changes: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title, fullTitle: title, fullTitleTruncated: false, workspacePath: "/chats", providerKey: "codex", updatedAt: "2026-10-14T10:00:00Z",
  createdAt: "2026-10-14T09:00:00Z", parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, messageCount: 2,
  automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null,
  forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, ...changes,
});
// A chat of the Work space started a session in the Parser project, in the Personal space, which started a chat of its
// own there; the chat also started a second chat.
const children: WorkspaceSessionChild[] = [
  { session: session("child", "Review the parser", { scopeKind: "project", projectId: "p1", workspacePath: "/parser", spaceId: "personal" }), parentSessionId: "parent", depth: 1, atWork: false },
  { session: session("grandchild", "Write the tests", { spaceId: "personal" }), parentSessionId: "child", depth: 2, atWork: false },
  { session: session("second", "Summarize", { spaceId: "work" }), parentSessionId: "parent", depth: 1, atWork: false },
];
const tree = (working = false): SessionChildren => ({
  root: session("parent", "Plan the release", { spaceId: "work" }), rootAtWork: false,
  children: children.map(child => child.session.id === "grandchild" ? { ...child, atWork: working } : child),
  descendantIds: children.map(child => child.session.id), direct: 2, total: 3, truncated: false,
});
const spaces = readSpaces([
  { id: "default", name: "Default", description: null, icon: null, color: null, isDefault: true, projectIds: [], file: null, agentPrompt: null, disabledTools: [] },
  { id: "work", name: "Work", description: null, icon: null, color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [] },
  { id: "personal", name: "Personal", description: null, icon: null, color: null, isDefault: false, projectIds: ["p1"], file: null, agentPrompt: null, disabledTools: [] },
]);
const state = { reads: 0, deletes: [] as string[][], opened: [] as string[], closed: 0 };
let working = false;
// What the next deletion answers: an error code, or nothing for a deletion that is made.
let answer: string | null = null;

let show: ((value: { deleting: boolean; revision: number } | null) => void) | undefined;
let revision = 0;
function Fixture() {
  const [shown, setShown] = useState<{ deleting: boolean; revision: number } | null>(null);
  show = setShown;
  return <div className="ide-shell" style={{ display: "block", padding: 16 }}>
    {shown && <SessionChildrenDialog key={shown.revision} title="Plan the release" deleting={shown.deleting} canDelete
      read={async () => { state.reads++; return { kind: "read", value: tree(working) }; }}
      projects={[{ id: "p1", name: "Parser", path: "/parser", archived: false }]} spaces={spaces}
      onOpen={value => { state.opened.push(value.id); setShown(null); }}
      onDelete={async value => {
        state.deletes.push([...value.descendantIds]);
        return (answer ? { kind: "error", code: answer, deleted: [], blocking: null } : { kind: "deleted", deleted: [...value.descendantIds, value.root.id] }) satisfies SessionTreeDeletion;
      }}
      onClose={() => { state.closed++; setShown(null); }} />}
  </div>;
}

// As the application does: a press outside a window dismisses it.
dismissDialogsOnOutsidePress(document);
Object.assign(window, { childrenFixture: { state,
  show(deleting: boolean, atWork = false) { working = atWork; flushSync(() => show!({ deleting, revision: ++revision })); },
  /** What the next deletion answers. */
  answer(code: string | null) { answer = code; },
} });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
