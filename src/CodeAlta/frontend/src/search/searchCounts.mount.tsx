// Disposable page; the search window, opened on its Sessions category as Ctrl+F opens it, over an index and files the test plays.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { LocalSearchHit, WorkspaceSnapshot } from "#neoastra";
import { GlobalSearch } from "./GlobalSearch";

const session = { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0,
  rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "s1", title: "A review of the plan", fullTitle: "A review of the plan", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project",
  projectId: "p1", lineageIssue: null, workspacePath: null, providerKey: null, updatedAt: "2026-10-01T08:00:00Z" };
const snapshot = { configured: true, projects: [{ id: "p1", name: "One", path: "/code/one", archived: false }], sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false } as unknown as WorkspaceSnapshot;
const hit = (ref: string, kind: string): LocalSearchHit => ({ ref, kind, title: "A hit", snippet: "a review", marks: [], sessionId: "s1", projectId: "p1", at: "2026-10-01T08:00:00Z", role: "user", match: "words", extra: null }) as unknown as LocalSearchHit;
const state = { asked: [] as string[][], held: null as null | Readonly<{ wait: Promise<void>; release: () => void }> };

const content = {
  search: async (_query: string, kinds: readonly string[]) => {
    state.asked.push([...kinds]);
    // A read the test holds shows the category as it is while the host works.
    await state.held?.wait;
    return { status: "ok", hits: [hit("m1", "message"), hit("m2", "message"), hit("d1", "doc")].filter(value => kinds.includes(value.kind)) } as never;
  },
};
const files = {
  project: { id: "p1", name: "One", path: "/code/one" },
  search: async () => ({ status: "ok", items: [{ path: "review.md", directory: false }] }) as never,
};

Object.assign(window, { searchCountsFixture: { state,
  hold() { let release = () => { }; const wait = new Promise<void>(resolve => { release = resolve; }); state.held = { wait, release }; },
  release() { state.held?.release(); state.held = null; } } });
const nothing = () => { };
createRoot(document.getElementById("root")!).render(<StrictMode>
  <GlobalSearch snapshot={snapshot} favorites={[]} start={{ category: "sessions" }} files={files} content={content} available={() => true} onCommand={nothing} onProject={nothing} onSession={nothing} onFile={nothing} onClose={nothing} />
</StrictMode>);
